using System.Diagnostics;

namespace RpgLingo;

/// <summary>
/// Tracks statistics for the current translation session.
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
        Console.WriteLine("  ║         Session statistics           ║");
        Console.WriteLine("  ╚══════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"    Total time:            {Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"    Files processed:       {FilesProcessed}");
        Console.WriteLine($"    Files skipped:         {FilesSkipped}");
        Console.WriteLine();
        Console.WriteLine($"    Translations:          {Translations:N0}");
        Console.WriteLine($"    Cache hits:            {CacheHits:N0}");
        Console.WriteLine($"    Failures:              {Failures:N0}");
        Console.WriteLine();
        Console.WriteLine($"    Characters translated: {CharactersTranslated:N0}");
        Console.WriteLine($"    Characters from cache: {CharactersCached:N0}");
        Console.WriteLine();
        Console.WriteLine($"    API calls:             {ApiCalls:N0}");
        Console.WriteLine($"    Batch API calls:       {BatchApiCalls:N0}");
        Console.WriteLine();
        Console.WriteLine($"    Control codes:         {ControlCodesDetected:N0}");
        Console.WriteLine($"    Script variables:      {ScriptVarsDetected:N0}");

        if (Translations + CacheHits > 0)
        {
            double hitRate = (double)CacheHits / (Translations + CacheHits) * 100;
            Console.WriteLine();
            Console.WriteLine($"    Cache hit rate:        {hitRate:F1}%");
        }

        if (Elapsed.TotalSeconds > 0 && Translations > 0)
        {
            double charsPerSec = CharactersTranslated / Elapsed.TotalSeconds;
            Console.WriteLine($"    Speed:                 {charsPerSec:F0} chars/s");
        }
    }
}
