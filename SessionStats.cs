using System.Diagnostics;

namespace RpgLingo;

/// <summary>
/// Rastrea estadísticas de la sesión de traducción actual.
/// </summary>
public class SessionStats
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public int Translations { get; set; }
    public int CacheHits { get; set; }
    public int Failures { get; set; }
    public int FilesProcessed { get; set; }
    public int FilesSkipped { get; set; }
    public long CharactersTranslated { get; set; }
    public long CharactersCached { get; set; }
    public int ApiCalls { get; set; }
    public int BatchApiCalls { get; set; }
    public int ControlCodesDetected { get; set; }
    public int ScriptVarsDetected { get; set; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void AddTranslation(int charCount)
    {
        Translations++;
        CharactersTranslated += charCount;
        ApiCalls++;
    }

    public void AddBatchTranslation(int count, long totalChars)
    {
        Translations += count;
        CharactersTranslated += totalChars;
        BatchApiCalls++;
    }

    public void AddCacheHit(int charCount)
    {
        CacheHits++;
        CharactersCached += charCount;
    }

    public void AddFailure()
    {
        Failures++;
    }

    public void Show()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════╗");
        Console.WriteLine("  ║       Estadísticas de sesión         ║");
        Console.WriteLine("  ╚══════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"    Tiempo total:          {Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"    Archivos procesados:   {FilesProcessed}");
        Console.WriteLine($"    Archivos saltados:     {FilesSkipped}");
        Console.WriteLine();
        Console.WriteLine($"    Traducciones:          {Translations:N0}");
        Console.WriteLine($"    Cache hits:            {CacheHits:N0}");
        Console.WriteLine($"    Fallos:                {Failures:N0}");
        Console.WriteLine();
        Console.WriteLine($"    Caracteres traducidos: {CharactersTranslated:N0}");
        Console.WriteLine($"    Caracteres de caché:   {CharactersCached:N0}");
        Console.WriteLine();
        Console.WriteLine($"    Llamadas API:          {ApiCalls:N0}");
        Console.WriteLine($"    Llamadas batch:        {BatchApiCalls:N0}");
        Console.WriteLine();
        Console.WriteLine($"    Control codes:         {ControlCodesDetected:N0}");
        Console.WriteLine($"    Variables de script:   {ScriptVarsDetected:N0}");

        if (Translations + CacheHits > 0)
        {
            double hitRate = (double)CacheHits / (Translations + CacheHits) * 100;
            Console.WriteLine();
            Console.WriteLine($"    Tasa de caché:         {hitRate:F1}%");
        }

        if (Elapsed.TotalSeconds > 0 && Translations > 0)
        {
            double charsPerSec = CharactersTranslated / Elapsed.TotalSeconds;
            Console.WriteLine($"    Velocidad:             {charsPerSec:F0} chars/s");
        }
    }
}
