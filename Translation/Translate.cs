using Azure;
using Azure.AI.Translation.Text;
using DeepL;
using DeepL.Model;
using Google.Cloud.Translation.V2;

namespace RpgLingo.Translation;
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

    /// <summary>
    /// Syncs usage data from APIs that support it (DeepL).
    /// Google and Azure don't expose usage APIs, so they keep local tracking.
    /// </summary>
    public void SyncUsage()
    {
        bool updated = false;
        foreach (TranslationEndpoint ep in _endpoints)
        {
            if (ep.Service != TranslationService.DeepL) continue;
            if (!_clients.ContainsKey(ClientKey(ep))) continue;

            try
            {
                Translator client = (Translator)_clients[ClientKey(ep)];
                Usage usage = client.GetUsageAsync().Result;
                if (usage.Character != null)
                {
                    ep.CharsUsed = usage.Character.Count;
                    ep.CharLimit = usage.Character.Limit;
                    updated = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Could not sync usage for {ep.DisplayName}: {ex.Message}");
            }
        }

        if (updated)
            _config.Save();
    }

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
    /// Tries all DeepL endpoints for batch, using each until exhausted.
    /// Falls back to one-by-one with progress for non-DeepL endpoints.
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

        // Try batch with each available DeepL endpoint, chunk by chunk
        const int maxBatchSize = 50;
        int batchProgress = 0;

        foreach (TranslationEndpoint ep in _endpoints)
        {
            if (ep.Service != TranslationService.DeepL) continue;
            if (ep.CharsRemaining <= 0) continue;
            if (!_clients.ContainsKey(ClientKey(ep))) continue;

            Translator client = (Translator)_clients[ClientKey(ep)];
            TextTranslateOptions opt = new()
            {
                Formality = Formality.PreferLess,
                PreserveFormatting = true,
                Context = _context,
                ModelType = ModelType.PreferQualityOptimized,
                TagHandling = "html"
            };

            // Process remaining texts in chunks of 50
            while (batchProgress < toTranslate.Count)
            {
                List<(int index, string text)> chunk = toTranslate.Skip(batchProgress).Take(maxBatchSize).ToList();
                string[] chunkTexts = chunk.Select(t => t.text).ToArray();

                try
                {
                    TextResult[] chunkResults = ExecuteWithRetry(() =>
                        client.TranslateTextAsync(chunkTexts, _sourceLang, _targetLang, opt).Result);

                    ep.CharsUsed += chunkResults.Sum(r => r.BilledCharacters);

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        string translated = chunkResults[j].DetectedSourceLanguageCode == _targetLang
                            ? chunk[j].text
                            : chunkResults[j].Text;

                        results[chunk[j].index] = translated;
                        _inflight[chunk[j].text] = translated;
                        AppendContext(chunk[j].text);
                    }

                    batchProgress += chunk.Count;
                }
                catch (Exception ex) when (IsQuotaExceeded(ex))
                {
                    Console.WriteLine($"    Quota exceeded for {ep.DisplayName}, trying next...");
                    ep.CharsUsed = ep.CharLimit;
                    _config.Save();
                    break; // Try next DeepL endpoint
                }
            }

            _config.Save();
            if (batchProgress >= toTranslate.Count)
                return results.ToList(); // All done via batch
        }

        // Fallback: translate remaining texts one by one with progress
        if (batchProgress < toTranslate.Count)
        {
            List<(int index, string text)> remaining = toTranslate.Skip(batchProgress).ToList();
            int done = 0;
            Console.Write($"    Translating one by one: ");
            foreach ((int index, string text) in remaining)
            {
                results[index] = TranslateText(text);
                done++;
                Console.Write($"\r    Translating one by one: {done}/{remaining.Count}");
            }
            Console.WriteLine();
        }

        return results.ToList();
    }

    private string TranslateSingle(string text)
    {
        foreach (TranslationEndpoint ep in _endpoints)
        {
            if (!ep.HasQuota(text.Length)) continue;
            if (!_clients.ContainsKey(ClientKey(ep))) continue;

            try
            {
                return ep.Service switch
                {
                    TranslationService.DeepL => ExecuteDeepL(text, ep),
                    TranslationService.Google => ExecuteGoogle(text, ep),
                    TranslationService.Azure => ExecuteAzure(text, ep),
                    _ => throw new NotSupportedException()
                };
            }
            catch (Exception ex) when (IsQuotaExceeded(ex))
            {
                Console.WriteLine($"    Quota exceeded for {ep.DisplayName}, trying next endpoint...");
                ep.CharsUsed = ep.CharLimit; // Mark as exhausted
                _config.Save();
                continue; // Try next endpoint
            }
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
            catch (Exception ex) when (attempt < MaxRetries - 1 && IsQuotaExceeded(ex))
            {
                throw; // Don't retry quota errors, propagate immediately
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

    private static bool IsQuotaExceeded(Exception ex)
    {
        // Check the full exception chain (AggregateException wraps the real error)
        string fullMessage = ex.ToString();
        if (fullMessage.Contains("Quota", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ex is DeepL.QuotaExceededException)
            return true;
        if (ex is AggregateException agg)
            return agg.InnerExceptions.Any(IsQuotaExceeded);
        if (ex.InnerException != null)
            return IsQuotaExceeded(ex.InnerException);
        return false;
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
