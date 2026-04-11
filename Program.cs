using RpgLingo;

// ==================== Detectar rutas automáticamente ====================
string exeDir = AppContext.BaseDirectory;
string gamePath = Path.Combine(exeDir, "www", "data");
string outputPath = Path.Combine(exeDir, "www", "data_es");

Console.WriteLine();
Console.WriteLine("  ╔══════════════════════════════════╗");
Console.WriteLine("  ║          R P G L I N G O         ║");
Console.WriteLine("  ╚══════════════════════════════════╝");
Console.WriteLine();

if (!Directory.Exists(gamePath))
{
    Console.WriteLine($"  No se ha encontrado la carpeta del juego:");
    Console.WriteLine($"  {gamePath}");
    Console.WriteLine();
    Console.WriteLine("  Coloca RpgLingo.exe en la carpeta raíz del juego");
    Console.WriteLine("  (donde está Game.exe) y vuelve a ejecutar.");
    Console.ReadKey();
    return;
}

Console.WriteLine($"  Juego detectado en: {gamePath}");
Console.WriteLine($"  Salida en:          {outputPath}");
Console.WriteLine();

// ==================== Cargar configuración ====================
Config config = Config.Load();

if (!config.HasAnyApiKey())
{
    Console.WriteLine("  No hay API keys configuradas. Necesitas al menos una.\n");
    config.RunSetupWizard();
}
else
{
    config.ShowSummary();
    Console.WriteLine();
    Console.Write("  ¿Quieres cambiar la configuración? (s/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "s")
        config.RunSetupWizard();
}

if (!config.HasAnyApiKey())
{
    Console.WriteLine("  No se ha configurado ninguna API key. Saliendo.");
    Console.ReadKey();
    return;
}

// ==================== Opción de forzar retraducción ====================
if (Directory.Exists(outputPath))
{
    Console.WriteLine();
    Console.WriteLine("  Se ha encontrado una traducción previa (data_es).");
    Console.WriteLine("  [1] Continuar donde se quedó (saltar archivos ya traducidos)");
    Console.WriteLine("  [2] Volver a traducir todo desde cero");
    Console.Write("  Opción: ");
    string? opcion = Console.ReadLine()?.Trim();

    if (opcion == "2")
    {
        RpgMakerTranslator.ClearTranslationMarkers();
        Directory.Delete(outputPath, true);
        Console.WriteLine("  Marcadores limpiados. Se retraducirá todo.");
    }
}

// ==================== Inicializar ====================
TranslationCache cache = new();
Translate translate = new(config);
RpgMakerTranslator translator = new(translate, cache, config.MaxLineLength);

// Archivos de diálogos (Maps y CommonEvents)
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

// ==================== Fase 1: Conteo de caracteres ====================
Console.WriteLine("\n  Analizando archivos...\n");

long totalChars = 0, cachedChars = 0, toTranslateChars = 0;
int totalStrings = 0;

void AddCount(string label, RpgMakerTranslator.CharCount count)
{
    totalChars += count.Total;
    cachedChars += count.Cached;
    toTranslateChars += count.ToTranslate;
    totalStrings += count.Strings;
    if (count.Total > 0)
        Console.WriteLine($"    {label,-25} {count.Total,8:N0} chars ({count.Cached:N0} en caché)");
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

Console.WriteLine();
Console.WriteLine($"    Total de cadenas:      {totalStrings:N0}");
Console.WriteLine($"    Total de caracteres:   {totalChars:N0}");
Console.WriteLine($"    Ya en caché:           {cachedChars:N0}");
Console.WriteLine($"    Por traducir:          {toTranslateChars:N0}");
Console.WriteLine();

if (toTranslateChars == 0)
{
    Console.WriteLine("  No hay nada nuevo que traducir. Todo está en caché.");
    Console.ReadKey();
    return;
}

Console.Write("  ¿Continuar con la traducción? (s/n): ");
if (Console.ReadLine()?.Trim().ToLower() != "s")
{
    Console.WriteLine("  Cancelado.");
    Console.ReadKey();
    return;
}

// ==================== Fase 2: Copia de seguridad ====================
if (!Directory.Exists(outputPath))
{
    CopyDirectory(gamePath, outputPath);
    Console.WriteLine($"\n  Copia creada en: {outputPath}\n");
}
else
{
    Console.WriteLine($"\n  Usando copia existente: {outputPath}\n");
}

// ==================== Fase 3: Traducción ====================
foreach (string file in dialogFiles)
{
    string outFile = Path.Combine(outputPath, Path.GetFileName(file));
    Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");
    translator.TranslateDialogFile(outFile);
}

// Archivos de objetos (Items, Weapons, Armors, Skills, Enemies, etc.)
foreach (string name in objectFileNames)
{
    string path = Path.Combine(outputPath, name);
    if (File.Exists(path))
    {
        Console.WriteLine($"\n--- {name} ---");
        translator.TranslateObjectFile(path);
    }
}

// System.json (títulos, términos del juego)
string outputSystemPath = Path.Combine(outputPath, "System.json");
if (File.Exists(outputSystemPath))
{
    Console.WriteLine("\n--- System.json ---");
    translator.TranslateSystemFile(outputSystemPath);
}

// ==================== Finalizar ====================
cache.Save();
config.Save();
Console.WriteLine("\n  ¡LISTO! Los archivos traducidos están en:");
Console.WriteLine($"  {outputPath}");
Console.WriteLine("\n  Reemplaza la carpeta 'data' por 'data_es' (renombrándola)");
Console.WriteLine("  para aplicar la traducción al juego.");
Console.ReadKey();

static void CopyDirectory(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (string file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
    foreach (string dir in Directory.GetDirectories(source))
        CopyDirectory(dir, Path.Combine(dest, new DirectoryInfo(dir).Name));
}
