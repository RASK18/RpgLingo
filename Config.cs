using System.Text.Json;

namespace RpgLingo;

public enum TranslationService
{
    DeepL,
    Google,
    Azure
}

public class TranslationEndpoint
{
    public TranslationService Service { get; set; } = TranslationService.DeepL;
    public string ApiKey { get; set; } = "";
    public string Region { get; set; } = "westeurope"; // Azure only
    public long CharLimit { get; set; } = 500_000;
    public long CharsUsed { get; set; }
    public string Label { get; set; } = ""; // Optional friendly name

    public long CharsRemaining => Math.Max(0, CharLimit - CharsUsed);
    public bool HasQuota(long additionalChars) => CharsUsed + additionalChars < CharLimit;

    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : $"{Service} ({MaskKey(ApiKey)})";

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "no key";
        if (key.Length <= 8) return "****";
        return key[..4] + "..." + key[^4..];
    }
}

public class Config
{
    public List<TranslationEndpoint> Endpoints { get; set; } = [];
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "es";
    public int MaxLineLength { get; set; } = 55;
    public int CacheMaxSizeMB { get; set; } = 512;

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RpgLingo");
    private static readonly string ConfigPath = Path.Combine(AppDataFolder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static Config Load()
    {
        if (!File.Exists(ConfigPath))
            return new Config();

        string json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<Config>(json, JsonOptions) ?? new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public bool HasAnyEndpoint() => Endpoints.Any(e => !string.IsNullOrWhiteSpace(e.ApiKey));

    public void ShowSummary()
    {
        Console.WriteLine("  Global configuration:");
        Console.WriteLine($"    Default source language:    {LanguageName(SourceLanguage)} ({SourceLanguage})");
        Console.WriteLine($"    Default target language:    {LanguageName(TargetLanguage)} ({TargetLanguage})");
        Console.WriteLine($"    Max line length:            {MaxLineLength}");
        Console.WriteLine($"    Cache max size:             {CacheMaxSizeMB} MB");
        Console.WriteLine();

        if (Endpoints.Count == 0)
        {
            Console.WriteLine("    No endpoints configured.");
            return;
        }

        Console.WriteLine("  Endpoints (in priority order):");
        for (int i = 0; i < Endpoints.Count; i++)
        {
            TranslationEndpoint ep = Endpoints[i];
            Console.WriteLine($"    [{i + 1}] {ep.DisplayName}");
            double pct = ep.CharLimit > 0 ? (double)ep.CharsUsed / ep.CharLimit * 100 : 0;
            Console.WriteLine($"        Service: {ep.Service} | Used: {ep.CharsUsed:N0} / {ep.CharLimit:N0} ({pct:F1}%)");
        }
    }

    public void RunSetupWizard()
    {
        Console.WriteLine("\n  Translation endpoint configuration:");
        Console.WriteLine("  Endpoints are used in order: the first with available quota is used.\n");

        bool adding = true;
        while (adding)
        {
            Console.WriteLine("  Current endpoints:");
            if (Endpoints.Count == 0)
                Console.WriteLine("    (none)");
            else
                for (int i = 0; i < Endpoints.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {Endpoints[i].DisplayName} ({Endpoints[i].Service})");

            Console.WriteLine();
            Console.WriteLine("  [A] Add endpoint");
            if (Endpoints.Count > 0)
            {
                Console.WriteLine("  [D] Delete endpoint");
                Console.WriteLine("  [R] Reset usage counters");
            }
            Console.WriteLine("  [S] Save and continue");
            Console.Write("  Option: ");
            string? opt = Console.ReadLine()?.Trim().ToUpper();

            switch (opt)
            {
                case "A":
                    AddEndpointWizard();
                    break;
                case "D" when Endpoints.Count > 0:
                    RemoveEndpointWizard();
                    break;
                case "R" when Endpoints.Count > 0:
                    ResetCounters();
                    Console.WriteLine("  Counters reset.");
                    break;
                case "S":
                    adding = false;
                    break;
            }
        }

        Console.WriteLine("\n  Languages (ISO 639-1 code, e.g.: en, es, ja, fr, de, pt, zh, ko):");
        SourceLanguage = Ask("Source language", SourceLanguage);
        TargetLanguage = Ask("Target language", TargetLanguage);

        string maxLine = Ask("Max line length", MaxLineLength.ToString());
        if (int.TryParse(maxLine, out int parsed) && parsed > 0)
            MaxLineLength = parsed;

        string cacheSize = Ask("Cache max size (MB)", CacheMaxSizeMB.ToString());
        if (int.TryParse(cacheSize, out int cacheParsed) && cacheParsed > 0)
            CacheMaxSizeMB = cacheParsed;

        Save();
        Console.WriteLine("\n  Configuration saved.\n");
    }

    private void AddEndpointWizard()
    {
        Console.WriteLine("\n  Available services:");
        Console.WriteLine("    [1] DeepL       (500K chars/month free)");
        Console.WriteLine("    [2] Google      ($300 free credits, 1 year)");
        Console.WriteLine("    [3] Azure       (2M chars/month free)");
        Console.Write("  Service: ");
        string? svc = Console.ReadLine()?.Trim();

        TranslationService service = svc switch
        {
            "1" => TranslationService.DeepL,
            "2" => TranslationService.Google,
            "3" => TranslationService.Azure,
            _ => TranslationService.DeepL
        };

        Console.Write("  API Key: ");
        string apiKey = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("  Cancelled (empty key).");
            return;
        }

        long defaultLimit = service == TranslationService.Azure ? 2_000_000 : 500_000;
        string limitStr = Ask("Character limit", defaultLimit.ToString());
        _ = long.TryParse(limitStr, out long limit);
        if (limit <= 0) limit = defaultLimit;

        string label = Ask("Label (optional)", "");

        string region = "";
        if (service == TranslationService.Azure)
            region = Ask("Azure Region", "westeurope");

        Endpoints.Add(new TranslationEndpoint
        {
            Service = service,
            ApiKey = apiKey,
            CharLimit = limit,
            Label = label,
            Region = region
        });

        Console.WriteLine($"  Endpoint added: {Endpoints[^1].DisplayName}");
    }

    private void RemoveEndpointWizard()
    {
        Console.Write("  Endpoint number to remove: ");
        if (int.TryParse(Console.ReadLine()?.Trim(), out int idx) && idx >= 1 && idx <= Endpoints.Count)
        {
            string name = Endpoints[idx - 1].DisplayName;
            Endpoints.RemoveAt(idx - 1);
            Console.WriteLine($"  Removed: {name}");
        }
    }

    public void ResetCounters()
    {
        foreach (TranslationEndpoint ep in Endpoints)
            ep.CharsUsed = 0;
        Save();
    }

    private static string Ask(string prompt, string currentValue)
    {
        string display = string.IsNullOrWhiteSpace(currentValue) ? "(empty)" : currentValue;
        Console.Write($"    {prompt} [{display}]: ");
        string? input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? currentValue : input;
    }

    public static string LanguageName(string code) => code.ToLower() switch
    {
        "en" => "English",
        "es" => "Español",
        "ja" => "日本語",
        "fr" => "Français",
        "de" => "Deutsch",
        "pt" => "Português",
        "it" => "Italiano",
        "zh" => "中文",
        "ko" => "한국어",
        "ru" => "Русский",
        "pl" => "Polski",
        "nl" => "Nederlands",
        "sv" => "Svenska",
        "da" => "Dansk",
        "fi" => "Suomi",
        "nb" => "Norsk",
        "tr" => "Türkçe",
        "ar" => "العربية",
        "cs" => "Čeština",
        "el" => "Ελληνικά",
        "hu" => "Magyar",
        "ro" => "Română",
        "uk" => "Українська",
        _ => code
    };
}
