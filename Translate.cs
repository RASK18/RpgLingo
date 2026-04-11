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
    private readonly Translator? _deepLClient;
    private readonly Translator? _deepLClient2;
    private readonly TranslationClient? _googleClient;
    private readonly TextTranslationClient? _azureClient;
    private string? _context;

    // Deduplicación de peticiones en la misma ejecución
    private readonly Dictionary<string, string> _inflight = [];

    public Translate(Config config)
    {
        _config = config;

        if (!string.IsNullOrWhiteSpace(config.DeepLApiKey))
            _deepLClient = new Translator(config.DeepLApiKey);

        if (!string.IsNullOrWhiteSpace(config.DeepLApiKey2))
            _deepLClient2 = new Translator(config.DeepLApiKey2);

        if (!string.IsNullOrWhiteSpace(config.GoogleApiKey))
            _googleClient = TranslationClient.CreateFromApiKey(config.GoogleApiKey);

        if (!string.IsNullOrWhiteSpace(config.AzureApiKey))
            _azureClient = new TextTranslationClient(
                new AzureKeyCredential(config.AzureApiKey),
                new Uri("https://api.cognitive.microsofttranslator.com/"),
                config.AzureRegion);
    }

    public string? ToSpanish(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.All(c => !char.IsLetter(c)))
            return null;

        // Deduplicación: si ya se tradujo este texto en esta ejecución
        if (_inflight.TryGetValue(text, out string? cached))
            return cached;

        string result = TranslateWithRetry(() => TranslateSingle(text));
        _inflight[text] = result;
        AppendContext(text);
        return result;
    }

    /// <summary>
    /// Traduce múltiples textos en una sola llamada (batch).
    /// Solo soportado por DeepL. Para otros servicios, traduce uno a uno.
    /// </summary>
    public List<string?> ToSpanishBatch(List<string> texts)
    {
        if (texts.Count == 0) return [];
        if (texts.Count == 1) return [ToSpanish(texts[0])];

        // Filtrar textos que ya están en inflight o son vacíos
        string?[] results = new string?[texts.Count];
        List<(int index, string text)> toTranslate = [];

        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i];
            if (string.IsNullOrWhiteSpace(text) || text.All(c => !char.IsLetter(c)))
            {
                results[i] = null;
            }
            else if (_inflight.TryGetValue(text, out string? cached))
            {
                results[i] = cached;
            }
            else
            {
                toTranslate.Add((i, text));
            }
        }

        if (toTranslate.Count == 0) return results.ToList();

        // Intentar batch con DeepL
        if (_deepLClient != null && CanUseDeepL(toTranslate.Sum(t => t.text.Length)))
        {
            List<string> batchTexts = toTranslate.Select(t => t.text).ToList();
            List<string> translations = TranslateWithRetry(() => TranslateDeepLBatch(batchTexts, false));

            for (int i = 0; i < toTranslate.Count; i++)
            {
                results[toTranslate[i].index] = translations[i];
                _inflight[toTranslate[i].text] = translations[i];
                AppendContext(toTranslate[i].text);
            }
        }
        else
        {
            // Fallback: traducir uno a uno
            foreach ((int index, string text) in toTranslate)
            {
                results[index] = ToSpanish(text);
            }
        }

        return results.ToList();
    }

    private bool CanUseDeepL(long additionalChars, bool alt = false)
    {
        if (alt)
            return _deepLClient2 != null && _config.DeepLCount2 + additionalChars < 500_000;
        return _deepLClient != null && _config.DeepLCount + additionalChars < 500_000;
    }

    private string TranslateSingle(string text)
    {
        if (_deepLClient != null && _config.DeepLCount + text.Length < 500_000)
            return TranslateDeepL(text, false);
        if (_deepLClient2 != null && _config.DeepLCount2 + text.Length < 500_000)
            return TranslateDeepL(text, true);
        if (_googleClient != null && _config.GoogleCount + text.Length < 500_000)
            return TranslateGoogle(text);
        if (_azureClient != null && _config.AzureCount + text.Length < 2_000_000)
            return TranslateAzure(text);

        throw new InvalidOperationException(
            "Todos los servicios han alcanzado su límite mensual o no hay API keys configuradas.");
    }

    private static T TranslateWithRetry<T>(Func<T> translateFunc)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return translateFunc();
            }
            catch (DeepLException ex) when (attempt < MaxRetries - 1 && IsTooManyRequests(ex))
            {
                int delay = GetBackoffDelay(attempt, ex);
                Console.WriteLine($"    429 Too Many Requests. Esperando {delay / 1000.0:F1}s...");
                Thread.Sleep(delay);
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                int delay = BackoffScheduleMs[Math.Min(attempt, BackoffScheduleMs.Length - 1)];
                Console.WriteLine($"    Reintento {attempt + 1}/{MaxRetries}: {ex.Message}");
                Thread.Sleep(delay);
            }
        }
        return translateFunc();
    }

    private static bool IsTooManyRequests(Exception ex)
    {
        return ex.Message.Contains("429") || ex is TooManyRequestsException;
    }

    private static int GetBackoffDelay(int attempt, Exception ex)
    {
        // Intentar leer Retry-After de la excepción
        if (ex is TooManyRequestsException)
        {
            // DeepL .NET SDK no expone Retry-After directamente,
            // usamos el schedule de backoff
        }
        return BackoffScheduleMs[Math.Min(attempt, BackoffScheduleMs.Length - 1)];
    }

    private string TranslateDeepL(string text, bool altClient)
    {
        Translator client = altClient ? _deepLClient2! : _deepLClient!;
        TextTranslateOptions opt = new()
        {
            Formality = Formality.PreferLess,
            PreserveFormatting = true,
            Context = _context,
            ModelType = ModelType.PreferQualityOptimized,
            TagHandling = "html"
        };

        TextResult result = client.TranslateTextAsync(text, LanguageCode.English, LanguageCode.Spanish, opt).Result;

        if (altClient)
            _config.DeepLCount2 += result.BilledCharacters;
        else
            _config.DeepLCount += result.BilledCharacters;

        _config.Save();
        return result.DetectedSourceLanguageCode == LanguageCode.Spanish ? text : result.Text;
    }

    /// <summary>
    /// Traduce múltiples textos en una sola llamada a DeepL.
    /// Hasta 50 textos por llamada (límite de la API).
    /// </summary>
    private List<string> TranslateDeepLBatch(List<string> texts, bool altClient)
    {
        Translator client = altClient ? _deepLClient2! : _deepLClient!;
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
            TextResult[] results = client.TranslateTextAsync(batch, LanguageCode.English, LanguageCode.Spanish, opt).Result;

            long billedChars = results.Sum(r => r.BilledCharacters);
            if (altClient)
                _config.DeepLCount2 += billedChars;
            else
                _config.DeepLCount += billedChars;

            foreach (TextResult result in results)
            {
                allResults.Add(result.DetectedSourceLanguageCode == LanguageCode.Spanish
                    ? texts[allResults.Count]
                    : result.Text);
            }
        }

        _config.Save();
        return allResults;
    }

    private string TranslateGoogle(string text)
    {
        TranslationResult result = _googleClient!.TranslateText(text, LanguageCodes.Spanish, LanguageCodes.English);
        _config.GoogleCount += text.Length;
        _config.Save();
        return result.DetectedSourceLanguage == LanguageCodes.Spanish ? text : result.TranslatedText;
    }

    private string TranslateAzure(string text)
    {
        Response<IReadOnlyList<TranslatedTextItem>> response = _azureClient!.Translate(
            targetLanguages: ["es"],
            content: [text],
            sourceLanguage: "en");

        _config.AzureCount += text.Length;
        _config.Save();
        return response.Value.Single().Translations.Single().Text;
    }

    private void AppendContext(string text)
    {
        _context = _context + "\n" + text;
        if (_context.Length > 15000)
            _context = _context[^15000..];
    }
}
