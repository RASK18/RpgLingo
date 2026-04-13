using RpgLingo;
using RpgLingo.RpgMaker;
using RpgLingo.Translation;

string exeDir = AppContext.BaseDirectory;
string wwwDir = Path.Combine(exeDir, "www");
string dataPath = Path.Combine(wwwDir, "data");
string dataOriginalPath = Path.Combine(wwwDir, "data_original");

PrintHeader();

// ==================== Detect game data ====================
// If a translation was already applied, originals are in data_original
string gamePath = Directory.Exists(dataOriginalPath) ? dataOriginalPath : dataPath;

if (!Directory.Exists(gamePath))
{
    Console.WriteLine($"  Game data folder not found:");
    Console.WriteLine($"  {dataPath}");
    Console.WriteLine();
    Console.WriteLine("  Place RpgLingo.exe in the game's root folder");
    Console.WriteLine("  (where Game.exe is) and run again.");
    Console.WriteLine();
    Console.WriteLine($"\n  Press any key to exit...");
    Console.ReadKey();
    return;
}

// ==================== Load configuration ====================
Config config = Config.Load();

if (!config.HasAnyEndpoint())
{
    Console.WriteLine("  No endpoints configured. You need at least one.\n");
    config.RunSetupWizard();
}
else
{
    config.ShowSummary();
    Console.WriteLine();
    Console.Write("  Change configuration? (y/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "y")
        config.RunSetupWizard();
}

if (!config.HasAnyEndpoint())
{
    Console.WriteLine("  No endpoints configured. Exiting.");
    Console.WriteLine();
    Console.WriteLine($"\n  Press any key to exit...");
    Console.ReadKey();
    return;
}

Console.Clear();
PrintHeader();

// ==================== Confirm languages ====================
string sourceLang = config.SourceLanguage;
string targetLang = config.TargetLanguage;
string savedSourceLang = sourceLang;
string savedTargetLang = targetLang;

Console.WriteLine($"  For this game: {Config.LanguageName(sourceLang)} ({sourceLang}) → {Config.LanguageName(targetLang)} ({targetLang})");
Console.Write("  Is this correct? (y/n): ");
if (Console.ReadLine()?.Trim().ToLower() == "n")
{
    Console.Write($"    Source language [{sourceLang}]: ");
    string? newSource = Console.ReadLine()?.Trim().ToLower();
    if (!string.IsNullOrEmpty(newSource))
        sourceLang = newSource;

    Console.Write($"    Target language [{targetLang}]: ");
    string? newTarget = Console.ReadLine()?.Trim().ToLower();
    if (!string.IsNullOrEmpty(newTarget))
        targetLang = newTarget;

    Console.WriteLine($"\n  New: {Config.LanguageName(sourceLang)} ({sourceLang}) → {Config.LanguageName(targetLang)} ({targetLang})");
    Console.Write("  Save as default languages? (y/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "y")
    {
        savedSourceLang = sourceLang;
        savedTargetLang = targetLang;
        Console.WriteLine("  Saved as default.");
    }
    else
    {
        Console.WriteLine("  Will be used for this session only.");
    }
}

config.SourceLanguage = sourceLang;
config.TargetLanguage = targetLang;

Console.Clear();
PrintHeader();

// ==================== Output paths (depend on language) ====================
string outputPath = Path.Combine(wwwDir, $"data_{targetLang}");
Console.WriteLine($"  Output in: {outputPath}");

// ==================== Retranslation option ====================
if (Directory.Exists(outputPath))
{
    Console.WriteLine();
    Console.WriteLine($"  Previous translation found (data_{targetLang}).");
    Console.WriteLine("  [1] Continue where it left off (skip already translated files)");
    Console.WriteLine("  [2] Retranslate everything from scratch");
    Console.Write("  Option: ");
    string? option = Console.ReadLine()?.Trim();

    if (option == "2")
    {
        RpgMakerTranslator.ClearTranslationMarkers();
        Directory.Delete(outputPath, true);
        Console.WriteLine("  Markers cleared. Full retranslation will begin.");
    }
}

// ==================== Initialize ====================
TranslationCache cache = new(maxSizeMB: config.CacheMaxSizeMB);
Translate translate = new(config);
translate.SyncUsage();

// ==================== Glossary ====================
string glossaryPath = Path.Combine(exeDir, "glossary.json");
Glossary glossary = new(glossaryPath);

if (glossary.Count == 0)
{
    Console.WriteLine("\n  No glossary found. Generating from game files...");
    int added = glossary.AutoPopulate(gamePath);

    if (added > 0)
    {
        Console.WriteLine($"  Found {added} terms (character names, items, skills, etc.).");
        Console.WriteLine($"  Saved to: glossary.json");
        Console.WriteLine();
        Console.WriteLine("  A glossary ensures that specific terms (character names, items, skills) are always");
        Console.WriteLine("  translated the same way. Edit glossary.json to add your preferred translations.");
        Console.WriteLine();
        Console.Write("  Would you like to exit and edit glossary.json first? (y/n): ");
        if (Console.ReadLine()?.Trim().ToLower() == "y")
        {
            Console.WriteLine($"\n  Edit the file and run RpgLingo again.");
            Console.WriteLine();
            Console.WriteLine($"\n  Press any key to exit...");
            Console.ReadKey();
            return;
        }
    }
    else
    {
        Console.WriteLine("  No translatable terms found in game files.");
    }
}
else
{
    glossary.ShowSummary();
}

Console.Clear();
PrintHeader();

SessionStats stats = new();
RpgMakerTranslator translator = new(translate, cache, stats, config.MaxLineLength, glossary);

string[] dialogFiles = Directory.GetFiles(gamePath)
    .Where(f =>
    {
        string name = Path.GetFileName(f);
        return (name.StartsWith("Map") && !name.Contains("Infos"))
               || name.StartsWith("CommonEvents");
    })
    .ToArray();

string[] objectFileNames = ["Items.json", "Weapons.json", "Armors.json", "Skills.json",
                             "Enemies.json", "Classes.json", "States.json"];

// ==================== Phase 1: Character counting ====================
Console.WriteLine("\n  Analyzing files...\n");

long totalChars = 0, cachedChars = 0, toTranslateChars = 0;
int totalStrings = 0;

void AddCount(string label, RpgMakerTranslator.CharCount count)
{
    totalChars += count.Total;
    cachedChars += count.Cached;
    toTranslateChars += count.ToTranslate;
    totalStrings += count.Strings;
    if (count.Total > 0)
        Console.WriteLine($"    {label,-25} {count.Total,8:N0} chars ({count.Cached:N0} cached)");
}

foreach (string file in dialogFiles)
    AddCount(Path.GetFileName(file), translator.CountDialogFile(file));

foreach (string name in objectFileNames)
{
    string path = Path.Combine(gamePath, name);
    if (File.Exists(path))
        AddCount(name, translator.CountObjectFile(path));
}

string systemPath = Path.Combine(gamePath, "System.json");
if (File.Exists(systemPath))
    AddCount("System.json", translator.CountSystemFile(systemPath));

// CSV localization file (text.csv)
string csvPath = Path.Combine(gamePath, "text.csv");
bool hasCsv = File.Exists(csvPath);
if (hasCsv)
    AddCount("text.csv", translator.CountCsvFile(csvPath, sourceLang));

Console.WriteLine();
Console.WriteLine($"    Total strings:         {totalStrings:N0}");
Console.WriteLine($"    Total characters:      {totalChars:N0}");
Console.WriteLine($"    Already cached:        {cachedChars:N0}");
Console.WriteLine($"    To translate:          {toTranslateChars:N0}");
Console.WriteLine();

Console.WriteLine("  Available quota:");
foreach (TranslationEndpoint ep in config.Endpoints)
    Console.WriteLine($"    {ep.DisplayName}: {ep.CharsRemaining:N0} chars remaining");
Console.WriteLine();

if (totalStrings == 0)
{
    Console.WriteLine("  No translatable text found in game files.");
    Console.WriteLine();
    Console.WriteLine($"\n  Press any key to exit...");
    Console.ReadKey();
    return;
}

if (toTranslateChars == 0)
    Console.WriteLine("  All text is cached. No API calls needed.");

Console.Write("  Continue with translation? (y/n): ");
if (Console.ReadLine()?.Trim().ToLower() != "y")
{
    Console.WriteLine("  Cancelled.");
    Console.WriteLine();
    Console.WriteLine($"\n  Press any key to exit...");
    Console.ReadKey();
    return;
}

Console.Clear();
PrintHeader();

// ==================== Phase 2: Backup ====================
if (!Directory.Exists(outputPath))
{
    CopyDirectory(gamePath, outputPath);
    Console.WriteLine($"\n  Copy created in: {outputPath}\n");
}
else
{
    Console.WriteLine($"\n  Using existing copy: {outputPath}\n");
}

// ==================== Phase 3: Translation ====================
foreach (string file in dialogFiles)
{
    string outFile = Path.Combine(outputPath, Path.GetFileName(file));
    Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");
    translator.TranslateDialogFile(outFile);
}

foreach (string name in objectFileNames)
{
    string path = Path.Combine(outputPath, name);
    if (File.Exists(path))
    {
        Console.WriteLine($"\n--- {name} ---");
        translator.TranslateObjectFile(path);
    }
}

string outputSystemPath = Path.Combine(outputPath, "System.json");
if (File.Exists(outputSystemPath))
{
    Console.WriteLine("\n--- System.json ---");
    translator.TranslateSystemFile(outputSystemPath);
}

// CSV localization file
string outputCsvPath = Path.Combine(outputPath, "text.csv");
if (hasCsv && File.Exists(outputCsvPath))
{
    Console.WriteLine("\n--- text.csv ---");
    translator.TranslateCsvFile(outputCsvPath, sourceLang, targetLang);
}

// ==================== Phase 4: Apply translation ====================
cache.Save();
config.SourceLanguage = savedSourceLang;
config.TargetLanguage = savedTargetLang;
config.Save();
stats.Show();

Console.WriteLine();
Console.Write("  Apply the translation to the game? (y/n): ");
if (Console.ReadLine()?.Trim().ToLower() == "y")
{
    Console.Clear();
    PrintHeader();

    // Save originals if this is the first time
    if (!Directory.Exists(dataOriginalPath))
    {
        Directory.Move(dataPath, dataOriginalPath);
        Console.WriteLine($"  Originals saved in: data_original");
    }
    else if (Directory.Exists(dataPath))
    {
        // Data is already a previous translation, remove it
        Directory.Delete(dataPath, true);
    }

    // Copy translation as active data
    CopyDirectory(outputPath, dataPath);
    Console.WriteLine("  Translation applied. The game will start translated.");
    Console.WriteLine();
    Console.WriteLine("  To revert to the original language:");
    Console.WriteLine("    1. Delete the 'data' folder");
    Console.WriteLine("    2. Rename 'data_original' to 'data'");
}
else
{
    Console.Clear();
    PrintHeader();

    Console.WriteLine($"\n  Translated files are in: {outputPath}");
    Console.WriteLine("  You can apply them manually by renaming the folders.");
}

// ==================== END ====================
Console.WriteLine($"\n  Press any key to exit...");
Console.ReadKey();

static void CopyDirectory(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (string file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
    foreach (string dir in Directory.GetDirectories(source))
        CopyDirectory(dir, Path.Combine(dest, new DirectoryInfo(dir).Name));
}

static void PrintHeader()
{
    Console.WriteLine("  ╔═══════════════════╗");
    Console.WriteLine("  ║ ► RPG Lingo  v1.0 ║");
    Console.WriteLine("  ╚═══════════════════╝");
    Console.WriteLine();
}
