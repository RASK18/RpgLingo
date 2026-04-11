using System.Text.Json;

namespace RpgLingo;
public class TranslationCache
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RpgLingo", "translation_cache.json");

    private readonly string _path;
    private readonly Dictionary<string, string> _cache;
    private int _hits;
    private int _misses;

    public TranslationCache(string? path = null)
    {
        _path = path ?? DefaultPath;

        if (File.Exists(_path))
        {
            string json = File.ReadAllText(_path);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            Console.WriteLine($"  Caché cargada: {_cache.Count} entradas");
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
        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(_path, JsonSerializer.Serialize(_cache, options));
        Console.WriteLine($"  Caché guardada: {_cache.Count} entradas (hits: {_hits}, misses: {_misses})");
    }
}
