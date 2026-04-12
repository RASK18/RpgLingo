using System.Text.Json;

namespace RpgLingo;
public class TranslationCache
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RpgLingo", "translation_cache.json");

    private readonly string _path;
    private readonly long _maxSizeBytes;
    private readonly int _saveInterval;
    private readonly Dictionary<string, string> _cache;
    private int _hits;
    private int _misses;
    private int _newSinceLastSave;

    public int Hits => _hits;
    public int Misses => _misses;
    public int Count => _cache.Count;

    public TranslationCache(string? path = null, int maxSizeMB = 512, int saveInterval = 10)
    {
        _path = path ?? DefaultPath;
        _maxSizeBytes = maxSizeMB * 1024L * 1024L;
        _saveInterval = saveInterval;

        if (File.Exists(_path))
        {
            string json = File.ReadAllText(_path);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            Console.WriteLine($"  Cache loaded: {_cache.Count} entries ({new FileInfo(_path).Length / 1024 / 1024}MB)");
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
        _newSinceLastSave++;

        if (_newSinceLastSave >= _saveInterval)
            SaveQuiet();
    }

    /// <summary>
    /// Silent periodic save (no console output).
    /// </summary>
    private void SaveQuiet()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            PruneIfNeeded();
            JsonSerializerOptions options = new() { WriteIndented = true };
            File.WriteAllText(_path, JsonSerializer.Serialize(_cache, options));
            _newSinceLastSave = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Warning: Could not save periodic cache: {ex.Message}");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        PruneIfNeeded();

        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(_path, JsonSerializer.Serialize(_cache, options));
        _newSinceLastSave = 0;

        long sizeMB = new FileInfo(_path).Length / 1024 / 1024;
        Console.WriteLine($"  Cache saved: {_cache.Count} entries ({sizeMB}MB) | hits: {_hits}, misses: {_misses}");
    }

    private void PruneIfNeeded()
    {
        if (_maxSizeBytes <= 0) return;

        // Estimate current size
        long estimatedSize = _cache.Sum(kvp =>
            (long)(kvp.Key.Length + kvp.Value.Length) * 2 + 20); // UTF-16 + JSON overhead

        if (estimatedSize <= _maxSizeBytes) return;

        // Remove oldest entries (first in dictionary)
        int toRemove = _cache.Count / 4; // Remove 25%
        List<string> keysToRemove = _cache.Keys.Take(toRemove).ToList();
        foreach (string? key in keysToRemove)
            _cache.Remove(key);

        Console.WriteLine($"  Cache pruned: removed {toRemove} old entries (limit: {_maxSizeBytes / 1024 / 1024}MB)");
    }
}
