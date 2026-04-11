using Azure;
using Azure.AI.Translation.Text;
using DeepL;
using Google.Cloud.Translation.V2;

namespace RpgLingo;
public class Translate
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 2000;

    private readonly Config _config;
    private readonly Translator? _deepLClient;
    private readonly Translator? _deepLClient2;
    private readonly TranslationClient? _googleClient;
    private readonly TextTranslationClient? _azureClient;
    private string? _context;

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

        string result;

        if (_deepLClient != null && _config.DeepLCount + text.Length < 500_000)
            result = TranslateWithRetry(() => TranslateDeepL(text, false));
        else if (_deepLClient2 != null && _config.DeepLCount2 + text.Length < 500_000)
            result = TranslateWithRetry(() => TranslateDeepL(text, true));
        else if (_googleClient != null && _config.GoogleCount + text.Length < 500_000)
            result = TranslateWithRetry(() => TranslateGoogle(text));
        else if (_azureClient != null && _config.AzureCount + text.Length < 2_000_000)
            result = TranslateWithRetry(() => TranslateAzure(text));
        else
            throw new InvalidOperationException(
                "Todos los servicios han alcanzado su límite mensual o no hay API keys configuradas.");

        AppendContext(text);
        return result;
    }

    private static string TranslateWithRetry(Func<string> translateFunc)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return translateFunc();
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                Console.WriteLine($"    Reintento {attempt}/{MaxRetries}: {ex.Message}");
                Thread.Sleep(RetryDelayMs * attempt);
            }
        }
        return translateFunc(); // último intento, deja que lance excepción
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

        DeepL.Model.TextResult result = client.TranslateTextAsync(text, LanguageCode.English, LanguageCode.Spanish, opt).Result;

        if (altClient)
            _config.DeepLCount2 += result.BilledCharacters;
        else
            _config.DeepLCount += result.BilledCharacters;

        _config.Save();
        return result.DetectedSourceLanguageCode == LanguageCode.Spanish ? text : result.Text;
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
