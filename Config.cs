using System.Text.Json;

namespace RpgLingo;
public class Config
{
    public string DeepLApiKey { get; set; } = "";
    public string DeepLApiKey2 { get; set; } = "";
    public string GoogleApiKey { get; set; } = "";
    public string AzureApiKey { get; set; } = "";
    public string AzureRegion { get; set; } = "westeurope";
    public int MaxLineLength { get; set; } = 55;
    public long DeepLCount { get; set; }
    public long DeepLCount2 { get; set; }
    public long GoogleCount { get; set; }
    public long AzureCount { get; set; }

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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

    public bool HasAnyApiKey() =>
        !string.IsNullOrWhiteSpace(DeepLApiKey) ||
        !string.IsNullOrWhiteSpace(GoogleApiKey) ||
        !string.IsNullOrWhiteSpace(AzureApiKey);

    public void ShowSummary()
    {
        Console.WriteLine("  Configuración actual:");
        Console.WriteLine($"    DeepL API Key:     {MaskKey(DeepLApiKey)}");
        Console.WriteLine($"    DeepL API Key 2:   {MaskKey(DeepLApiKey2)}");
        Console.WriteLine($"    Google API Key:    {MaskKey(GoogleApiKey)}");
        Console.WriteLine($"    Azure API Key:     {MaskKey(AzureApiKey)}");
        Console.WriteLine($"    Azure Region:      {AzureRegion}");
        Console.WriteLine($"    Max line length:   {MaxLineLength}");
        Console.WriteLine();
        Console.WriteLine("  Uso acumulado:");
        Console.WriteLine($"    DeepL:     {DeepLCount:N0} / 500.000");
        Console.WriteLine($"    DeepL 2:   {DeepLCount2:N0} / 500.000");
        Console.WriteLine($"    Google:    {GoogleCount:N0} / 500.000");
        Console.WriteLine($"    Azure:     {AzureCount:N0} / 2.000.000");
    }

    public void RunSetupWizard()
    {
        Console.WriteLine("\n  Configuración de API Keys (Enter para mantener el valor actual):\n");

        DeepLApiKey = Ask("  DeepL API Key", DeepLApiKey);
        DeepLApiKey2 = Ask("  DeepL API Key 2 (opcional)", DeepLApiKey2);
        GoogleApiKey = Ask("  Google Cloud API Key (opcional)", GoogleApiKey);
        AzureApiKey = Ask("  Azure API Key (opcional)", AzureApiKey);

        if (!string.IsNullOrWhiteSpace(AzureApiKey))
            AzureRegion = Ask("  Azure Region", AzureRegion);

        string maxLine = Ask("  Max line length", MaxLineLength.ToString());
        if (int.TryParse(maxLine, out int parsed) && parsed > 0)
            MaxLineLength = parsed;

        Save();
        Console.WriteLine("\n  Configuración guardada.\n");
    }

    public void ResetCounters()
    {
        DeepLCount = 0;
        DeepLCount2 = 0;
        GoogleCount = 0;
        AzureCount = 0;
        Save();
    }

    private static string Ask(string prompt, string currentValue)
    {
        string display = string.IsNullOrWhiteSpace(currentValue) ? "(vacío)" : MaskKey(currentValue);
        Console.Write($"  {prompt} [{display}]: ");
        string? input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? currentValue : input;
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(vacío)";
        if (key.Length <= 8) return "****";
        return key[..4] + "..." + key[^4..];
    }
}
