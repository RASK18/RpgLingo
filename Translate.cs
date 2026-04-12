using Azure;
using Azure.AI.Translation.Text;
using DeepL;
using DeepL.Model;
using Google.Cloud.Translation.V2;

namespace RpgLingo;
public class Translate
{
    private const int MaxRetries = 4;
    private static readonly int[] BackoffScheduleMs = [1000, 2000, 4000, 8000];

    private readonly Config _config;
    private readonly List<TranslationEndpoint> _endpoints;
    private readonly Dictionary<string, object> _clients = [];
    private readonly string _sourceLang;
    private readonly string _targetLang;
    private string? _context;

    // In-flight deduplication for the current run
    private readonly Dictionary<string, string> _inflight = [];

    public Translate(Config config)
    {
        _config = config;
        _endpoints = config.Endpoints;
        _sourceLang = config.SourceLanguage;
        _targetLang = config.TargetLanguage;

        // Pre-create clients for each endpoint
        foreach (TranslationEndpoint ep in _endpoints)
        {
            string key = ep.ApiKey;
            if (string.IsNullOrWhiteSpace(key) || _clients.ContainsKey(ClientKey(ep)))
                continue;

            try
            {
                object client = ep.Service switch
                {
                    TranslationService.DeepL => new Translator(key),
                    TranslationService.Google => TranslationClient.CreateFromApiKey(key),
                    TranslationService.Azure => new TextTranslationClient(
                        new AzureKeyCredential(key),
                        new Uri("https://api.cognitive.microsofttranslator.com/"),
                        ep.Region),
                    _ => throw new NotSupportedException($"Unsupported service: {ep.Service}")
                };
                _clients[ClientKey(ep)] = client;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Could not create client for {ep.DisplayName}: {ex.Message}");
            }
        }
    }

    private static string ClientKey(TranslationEndpoint ep) => $"{ep.Service}_{ep.ApiKey}";

    public string? TranslateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.All(c => !char.IsLetter(c)))
            return null;

        if (_inflight.TryGetValue(text, out string? cached))
            return cached;

        string result = ExecuteWithRetry(() => TranslateSingle(text));
        _inflight[text] = result;
        AppendContext(text);
        return result;
    }

    /// <summary>
    /// Translates multiple texts in a single API call (batch).
    /// Only supported by DeepL. Other services fall back to one-by-one.
    /// </summary>
    public List<string?> TranslateTextBatch(List<string> texts)
    {
        if (texts.Count == 0) return [];
        if (texts.Count == 1) return [TranslateText(texts[0])];

        // Filter texts already in-flight or empty
        string?[] results = new string?[texts.Count];
        List<(int index, string text)> toTranslate = [];

        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i];
            if (string.IsNullOrWhiteSpace(text) || text.All(c => !char.IsLetter(c)))
                results[i] = null;
            else if (_inflight.TryGetValue(text, out string? cached))
                results[i] = cached;
            else
                toTranslate.Add((i, text));
        }

        if (toTranslate.Count == 0) return results.ToList();

        // Try batch with the first available DeepL endpoint
        long totalChars = toTranslate.Sum(t => (long)t.text.Length);
        TranslationEndpoint? deepLEndpoint = _endpoints.FirstOrDefault(ep =>
            ep.Service == TranslationService.DeepL &&
            ep.HasQuota(totalChars) &&
            _clients.ContainsKey(ClientKey(ep)));

        if (deepLEndpoint != null)
        {
            List<string> batchTexts = toTranslate.Select(t => t.text).ToList();
            List<string> translations = ExecuteWithRetry(() =>
                ExecuteDeepLBatch(batchTexts, deepLEndpoint));

            for (int i = 0; i < toTranslate.Count; i++)
            {
                results[toTranslate[i].index] = translations[i];
                _inflight[toTranslate[i].text] = translations[i];
                AppendContext(toTranslate[i].text);
            }
        }
        else
        {
            // Fallback: translate one by one
            foreach ((int index, string text) in toTranslate)
                results[index] = TranslateText(text);
        }

        return results.ToList();
    }

    private string TranslateSingle(string text)
    {
        foreach (TranslationEndpoint ep in _endpoints)
        {
            if (!ep.HasQuota(text.Length)) continue;
            if (!_clients.ContainsKey(ClientKey(ep))) continue;

            return ep.Service switch
            {
                TranslationService.DeepL => ExecuteDeepL(text, ep),
                TranslationService.Google => ExecuteGoogle(text, ep),
                TranslationService.Azure => ExecuteAzure(text, ep),
                _ => throw new NotSupportedException()
            };
        }

        throw new InvalidOperationException(
            "All endpoints have reached their limit or no endpoints are configured.");
    }

    private static T ExecuteWithRetry<T>(Func<T> translateFunc)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return translateFunc();
            }
            catch (DeepLException ex) when (attempt < MaxRetries - 1 && IsTooManyRequests(ex))
            {
                int delay = BackoffScheduleMs[Math.Min(attempt, BackoffScheduleMs.Length - 1)];
                Console.WriteLine($"    429 Too Many Requests. Waiting {delay / 1000.0:F1}s...");
                Thread.Sleep(delay);
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                int delay = BackoffScheduleMs[Math.Min(attempt, BackoffScheduleMs.Length - 1)];
                Console.WriteLine($"    Retry {attempt + 1}/{MaxRetries}: {ex.Message}");
                Thread.Sleep(delay);
            }
        }
        return translateFunc();
    }

    private static bool IsTooManyRequests(Exception ex)
    {
        return ex.Message.Contains("429") || ex is TooManyRequestsException;
    }

    // ==================== DeepL ====================

    private string ExecuteDeepL(string text, TranslationEndpoint ep)
    {
        Translator client = (Translator)_clients[ClientKey(ep)];
        TextTranslateOptions opt = new()
        {
            Formality = Formality.PreferLess,
            PreserveFormatting = true,
            Context = _context,
            ModelType = ModelType.PreferQualityOptimized,
            TagHandling = "html"
        };

        TextResult result = client.TranslateTextAsync(text, _sourceLang, _targetLang, opt).Result;
        ep.CharsUsed += result.BilledCharacters;
        _config.Save();

        return result.DetectedSourceLanguageCode == _targetLang ? text : result.Text;
    }

    /// <summary>
    /// Translates multiple texts in a single DeepL API call.
    /// Up to 50 texts per call (API limit).
    /// </summary>
    private List<string> ExecuteDeepLBatch(List<string> texts, TranslationEndpoint ep)
    {
        Translator client = (Translator)_clients[ClientKey(ep)];
        TextTranslateOptions opt = new()
        {
            Formality = Formality.PreferLess,
            PreserveFormatting = true,
            Context = _context,
            ModelType = ModelType.PreferQualityOptimized,
            TagHandling = "html"
        };

        const int maxBatchSize = 50;
        List<string> allResults = [];

        for (int i = 0; i < texts.Count; i += maxBatchSize)
        {
            string[] batch = texts.Skip(i).Take(maxBatchSize).ToArray();
            TextResult[] results = client.TranslateTextAsync(batch, _sourceLang, _targetLang, opt).Result;

            ep.CharsUsed += results.Sum(r => r.BilledCharacters);

            foreach (TextResult result in results)
            {
                allResults.Add(result.DetectedSourceLanguageCode == _targetLang
                    ? texts[allResults.Count]
                    : result.Text);
            }
        }

        _config.Save();
        return allResults;
    }

    // ==================== Google ====================

    private string ExecuteGoogle(string text, TranslationEndpoint ep)
    {
        TranslationClient client = (TranslationClient)_clients[ClientKey(ep)];
        TranslationResult result = client.TranslateText(text, _targetLang, _sourceLang);

        ep.CharsUsed += text.Length;
        _config.Save();

        return result.DetectedSourceLanguage == _targetLang ? text : result.TranslatedText;
    }

    // ==================== Azure ====================

    private string ExecuteAzure(string text, TranslationEndpoint ep)
    {
        TextTranslationClient client = (TextTranslationClient)_clients[ClientKey(ep)];
        Response<IReadOnlyList<TranslatedTextItem>> response = client.Translate(
            targetLanguages: [_targetLang],
            content: [text],
            sourceLanguage: _sourceLang);

        ep.CharsUsed += text.Length;
        _config.Save();

        return response.Value.Single().Translations.Single().Text;
    }

    // ==================== Context ====================

    private void AppendContext(string text)
    {
        _context = _context + "\n" + text;
        if (_context.Length > 15000)
            _context = _context[^15000..];
    }
}
