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
    public string Region { get; set; } = "westeurope"; // Solo para Azure
    public long CharLimit { get; set; } = 500_000;
    public long CharsUsed { get; set; }
    public string Label { get; set; } = ""; // Nombre opcional para identificarlo

    public long CharsRemaining => Math.Max(0, CharLimit - CharsUsed);
    public bool HasQuota(long additionalChars) => CharsUsed + additionalChars < CharLimit;

    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : $"{Service} ({MaskKey(ApiKey)})";

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "sin key";
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
        Console.WriteLine("  Configuración actual:");
        Console.WriteLine($"    Idioma origen:     {LanguageName(SourceLanguage)} ({SourceLanguage})");
        Console.WriteLine($"    Idioma destino:    {LanguageName(TargetLanguage)} ({TargetLanguage})");
        Console.WriteLine($"    Max line length:   {MaxLineLength}");
        Console.WriteLine($"    Cache max size:    {CacheMaxSizeMB}MB");
        Console.WriteLine();

        if (Endpoints.Count == 0)
        {
            Console.WriteLine("    No hay endpoints configurados.");
            return;
        }

        Console.WriteLine("  Endpoints (en orden de prioridad):");
        for (int i = 0; i < Endpoints.Count; i++)
        {
            TranslationEndpoint ep = Endpoints[i];
            Console.WriteLine($"    [{i + 1}] {ep.DisplayName}");
            Console.WriteLine($"        Servicio: {ep.Service} | Usado: {ep.CharsUsed:N0} / {ep.CharLimit:N0}");
        }
    }

    public void RunSetupWizard()
    {
        Console.WriteLine("\n  Configuración de endpoints de traducción:");
        Console.WriteLine("  Los endpoints se usan en orden: el primero con cuota disponible se usa.\n");

        bool adding = true;
        while (adding)
        {
            Console.WriteLine("  Endpoints actuales:");
            if (Endpoints.Count == 0)
                Console.WriteLine("    (ninguno)");
            else
                for (int i = 0; i < Endpoints.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {Endpoints[i].DisplayName} ({Endpoints[i].Service})");

            Console.WriteLine();
            Console.WriteLine("  [A] Añadir endpoint");
            if (Endpoints.Count > 0)
            {
                Console.WriteLine("  [D] Eliminar endpoint");
                Console.WriteLine("  [R] Resetear contadores de uso");
            }
            Console.WriteLine("  [G] Guardar y continuar");
            Console.Write("  Opción: ");
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
                    Console.WriteLine("  Contadores reseteados.");
                    break;
                case "G":
                    adding = false;
                    break;
            }
        }

        Console.WriteLine("\n  Idiomas (código ISO 639-1, ej: en, es, ja, fr, de, pt, zh, ko):");
        SourceLanguage = Ask("Idioma origen", SourceLanguage);
        TargetLanguage = Ask("Idioma destino", TargetLanguage);

        string maxLine = Ask("Max line length", MaxLineLength.ToString());
        if (int.TryParse(maxLine, out int parsed) && parsed > 0)
            MaxLineLength = parsed;

        string cacheSize = Ask("Cache max size (MB)", CacheMaxSizeMB.ToString());
        if (int.TryParse(cacheSize, out int cacheParsed) && cacheParsed > 0)
            CacheMaxSizeMB = cacheParsed;

        Save();
        Console.WriteLine("\n  Configuración guardada.\n");
    }

    private void AddEndpointWizard()
    {
        Console.WriteLine("\n  Servicios disponibles:");
        Console.WriteLine("    [1] DeepL       (500K chars/mes gratis)");
        Console.WriteLine("    [2] Google      ($300 créditos gratis, 1 año)");
        Console.WriteLine("    [3] Azure       (2M chars/mes gratis)");
        Console.Write("  Servicio: ");
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
            Console.WriteLine("  Cancelado (key vacía).");
            return;
        }

        long defaultLimit = service == TranslationService.Azure ? 2_000_000 : 500_000;
        string limitStr = Ask("Límite de caracteres", defaultLimit.ToString());
        _ = long.TryParse(limitStr, out long limit);
        if (limit <= 0) limit = defaultLimit;

        string label = Ask("Etiqueta (opcional)", "");

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

        Console.WriteLine($"  Endpoint añadido: {Endpoints[^1].DisplayName}");
    }

    private void RemoveEndpointWizard()
    {
        Console.Write("  Número de endpoint a eliminar: ");
        if (int.TryParse(Console.ReadLine()?.Trim(), out int idx) && idx >= 1 && idx <= Endpoints.Count)
        {
            string name = Endpoints[idx - 1].DisplayName;
            Endpoints.RemoveAt(idx - 1);
            Console.WriteLine($"  Eliminado: {name}");
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
        string display = string.IsNullOrWhiteSpace(currentValue) ? "(vacío)" : currentValue;
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
