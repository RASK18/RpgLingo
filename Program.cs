using RpgLingo;

// ==================== Detectar rutas automáticamente ====================
string exeDir = AppContext.BaseDirectory;
string wwwDir = Path.Combine(exeDir, "www");
string dataPath = Path.Combine(wwwDir, "data");
string dataOriginalPath = Path.Combine(wwwDir, "data_original");

Console.WriteLine();
Console.WriteLine("  ╔══════════════════════════════════╗");
Console.WriteLine("  ║          R P G L I N G O         ║");
Console.WriteLine("  ╚══════════════════════════════════╝");
Console.WriteLine();

// ==================== Detectar datos del juego ====================
// Si ya se aplicó una traducción antes, los originales están en data_original
string gamePath = Directory.Exists(dataOriginalPath) ? dataOriginalPath : dataPath;

if (!Directory.Exists(gamePath))
{
    Console.WriteLine($"  No se ha encontrado la carpeta del juego:");
    Console.WriteLine($"  {dataPath}");
    Console.WriteLine();
    Console.WriteLine("  Coloca RpgLingo.exe en la carpeta raíz del juego");
    Console.WriteLine("  (donde está Game.exe) y vuelve a ejecutar.");
    Console.ReadKey();
    return;
}

Console.WriteLine($"  Datos originales en: {gamePath}");
Console.WriteLine();

// ==================== Cargar configuración ====================
Config config = Config.Load();

if (!config.HasAnyEndpoint())
{
    Console.WriteLine("  No hay endpoints configurados. Necesitas al menos uno.\n");
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

if (!config.HasAnyEndpoint())
{
    Console.WriteLine("  No se ha configurado ningún endpoint. Saliendo.");
    Console.ReadKey();
    return;
}

// ==================== Confirmar idiomas ====================
string sourceLang = config.SourceLanguage;
string targetLang = config.TargetLanguage;
string savedSourceLang = sourceLang;
string savedTargetLang = targetLang;

Console.WriteLine($"  Idiomas: {Config.LanguageName(sourceLang)} ({sourceLang}) → {Config.LanguageName(targetLang)} ({targetLang})");
Console.Write("  ¿Es correcto? (s/n): ");
if (Console.ReadLine()?.Trim().ToLower() == "n")
{
    Console.Write($"    Idioma origen [{sourceLang}]: ");
    string? newSource = Console.ReadLine()?.Trim().ToLower();
    if (!string.IsNullOrEmpty(newSource))
        sourceLang = newSource;

    Console.Write($"    Idioma destino [{targetLang}]: ");
    string? newTarget = Console.ReadLine()?.Trim().ToLower();
    if (!string.IsNullOrEmpty(newTarget))
        targetLang = newTarget;

    Console.WriteLine($"\n  Nuevo: {Config.LanguageName(sourceLang)} ({sourceLang}) → {Config.LanguageName(targetLang)} ({targetLang})");
    Console.Write("  ¿Guardar como idiomas por defecto? (s/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "s")
    {
        savedSourceLang = sourceLang;
        savedTargetLang = targetLang;
        Console.WriteLine("  Guardado como predeterminado.");
    }
    else
    {
        Console.WriteLine("  Se usará solo para esta ejecución.");
    }
}

config.SourceLanguage = sourceLang;
config.TargetLanguage = targetLang;

// ==================== Rutas de salida (dependen del idioma) ====================
string outputPath = Path.Combine(wwwDir, $"data_{targetLang}");
Console.WriteLine($"  Salida en: {outputPath}");

// ==================== Opción de forzar retraducción ====================
if (Directory.Exists(outputPath))
{
    Console.WriteLine();
    Console.WriteLine($"  Se ha encontrado una traducción previa (data_{targetLang}).");
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
TranslationCache cache = new(maxSizeMB: config.CacheMaxSizeMB);
Translate translate = new(config);

// ==================== Glosario ====================
string glossaryPath = Path.Combine(exeDir, "glossary.json");
Glossary glossary = new(glossaryPath);

if (glossary.Count == 0)
{
    Console.WriteLine("\n  No se ha encontrado glosario. Generando desde los archivos del juego...");
    int added = glossary.AutoPopulate(gamePath);
    Console.WriteLine($"  Se han encontrado {added} términos (nombres, objetos, habilidades, etc.).");
    Console.WriteLine($"  Guardado en: {glossaryPath}");
    Console.WriteLine();
    Console.WriteLine("  Puedes editar 'glossary.json' para añadir las traducciones que desees.");
    Console.WriteLine("  Los términos sin traducción se dejarán como están en el original.");
    Console.WriteLine("  Ejemplo:");
    Console.WriteLine("    { \"Term\": \"Dark Knight\", \"Translation\": \"Caballero Oscuro\", \"Note\": \"Clase\" }");
    Console.WriteLine();

    if (added > 0)
    {
        glossary.ShowEntries(10);
        Console.WriteLine();
        Console.Write("  ¿Quieres editar el glosario antes de continuar? (s/n): ");
        if (Console.ReadLine()?.Trim().ToLower() == "s")
        {
            Console.WriteLine($"\n  Edita el archivo y vuelve a ejecutar RpgLingo.");
            Console.ReadKey();
            return;
        }
    }
}
else
{
    glossary.ShowSummary();
}

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

Console.WriteLine("  Cuota disponible:");
foreach (TranslationEndpoint ep in config.Endpoints)
    Console.WriteLine($"    {ep.DisplayName}: {ep.CharsRemaining:N0} chars libres");
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

// ==================== Fase 4: Aplicar traducción ====================
cache.Save();
config.SourceLanguage = savedSourceLang;
config.TargetLanguage = savedTargetLang;
config.Save();
stats.Show();

Console.WriteLine();
Console.Write("  ¿Aplicar la traducción al juego? (s/n): ");
if (Console.ReadLine()?.Trim().ToLower() == "s")
{
    // Guardar originales si es la primera vez
    if (!Directory.Exists(dataOriginalPath))
    {
        Directory.Move(dataPath, dataOriginalPath);
        Console.WriteLine($"  Originales guardados en: data_original");
    }
    else if (Directory.Exists(dataPath))
    {
        // data ya es una traducción anterior, eliminarla
        Directory.Delete(dataPath, true);
    }

    // Copiar traducción como data activa
    CopyDirectory(outputPath, dataPath);
    Console.WriteLine("  Traducción aplicada. El juego arrancará traducido.");
    Console.WriteLine();
    Console.WriteLine("  Para volver al idioma original:");
    Console.WriteLine("    1. Elimina la carpeta 'data'");
    Console.WriteLine("    2. Renombra 'data_original' a 'data'");
}
else
{
    Console.WriteLine($"\n  Los archivos traducidos están en: {outputPath}");
    Console.WriteLine("  Puedes aplicarlos manualmente renombrando las carpetas.");
}

Console.ReadKey();

static void CopyDirectory(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (string file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
    foreach (string dir in Directory.GetDirectories(source))
        CopyDirectory(dir, Path.Combine(dest, new DirectoryInfo(dir).Name));
}
