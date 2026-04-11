using System.Text.Json;

namespace RpgLingo;
public class TranslationCache
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RpgLingo", "translation_cache.json");

    private readonly string _path;
    private readonly long _maxSizeBytes;
    private Dictionary<string, string> _cache;
    private int _hits;
    private int _misses;

    public TranslationCache(string? path = null, int maxSizeMB = 512)
    {
        _path = path ?? DefaultPath;
        _maxSizeBytes = maxSizeMB * 1024L * 1024L;

        if (File.Exists(_path))
        {
            string json = File.ReadAllText(_path);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            Console.WriteLine($"  Caché cargada: {_cache.Count} entradas ({new FileInfo(_path).Length / 1024 / 1024}MB)");
        }
        else
        {
            _cache = [];
        }
    }

    public bool TryGet(string key, out string value)
    {
        if (_cache.TryGetValue(key, out value!))
        {
            _hits++;
            return true;
        }
        _misses++;
        value = string.Empty;
        return false;
    }

    public void Set(string key, string value)
    {
        _cache[key] = value;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Comprobar si hay que podar la caché
        PruneIfNeeded();

        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(_path, JsonSerializer.Serialize(_cache, options));

        long sizeMB = new FileInfo(_path).Length / 1024 / 1024;
        Console.WriteLine($"  Caché guardada: {_cache.Count} entradas ({sizeMB}MB) | hits: {_hits}, misses: {_misses}");
    }

    private void PruneIfNeeded()
    {
        if (_maxSizeBytes <= 0) return;

        // Estimar tamaño actual
        long estimatedSize = _cache.Sum(kvp =>
            (long)(kvp.Key.Length + kvp.Value.Length) * 2 + 20); // UTF-16 + JSON overhead

        if (estimatedSize <= _maxSizeBytes) return;

        // Eliminar las entradas más antiguas (las primeras del diccionario)
        int toRemove = _cache.Count / 4; // Eliminar el 25%
        List<string> keysToRemove = _cache.Keys.Take(toRemove).ToList();
        foreach (string? key in keysToRemove)
            _cache.Remove(key);

        Console.WriteLine($"  Caché podada: eliminadas {toRemove} entradas antiguas (límite: {_maxSizeBytes / 1024 / 1024}MB)");
    }
}
