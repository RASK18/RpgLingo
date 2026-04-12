using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RpgLingo.Translation;

public class GlossaryEntry
{
    public string Term { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Note { get; set; } = "";
}

public class Glossary
{
    private readonly string _path;
    private readonly List<GlossaryEntry> _entries;

    private const string PlaceholderPrefix = "⟦G";
    private const string PlaceholderSuffix = "⟧";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public int Count => _entries.Count;

    public Glossary(string glossaryPath)
    {
        _path = glossaryPath;

        if (File.Exists(_path))
        {
            string json = File.ReadAllText(_path);
            _entries = JsonSerializer.Deserialize<List<GlossaryEntry>>(json, JsonOptions) ?? [];
        }
        else
        {
            _entries = [];
        }
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
    }

    public int AutoPopulate(string dataPath)
    {
        int added = 0;
        HashSet<string> existingTerms = new(_entries.Select(e => e.Term), StringComparer.OrdinalIgnoreCase);

        added += ScanObjectFile(Path.Combine(dataPath, "Actors.json"), ["name", "nickname"], "Character", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "Classes.json"), ["name"], "Class", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "Skills.json"), ["name"], "Skill", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "Items.json"), ["name"], "Item", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "Weapons.json"), ["name"], "Weapon", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "Armors.json"), ["name"], "Armor", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "Enemies.json"), ["name"], "Enemy", existingTerms);
        added += ScanObjectFile(Path.Combine(dataPath, "States.json"), ["name"], "State", existingTerms);

        if (added > 0)
            Save();

        return added;
    }

    private int ScanObjectFile(string filePath, string[] fields, string noteCategory, HashSet<string> existingTerms)
    {
        if (!File.Exists(filePath)) return 0;

        int added = 0;
        string json = File.ReadAllText(filePath);
        JsonArray? root = JsonNode.Parse(json)?.AsArray();
        if (root == null) return 0;

        foreach (JsonNode? item in root)
        {
            if (item == null) continue;
            foreach (string field in fields)
            {
                string val = item[field]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(val) || !val.Any(char.IsLetter)) continue;
                if (val.Length > 40) continue;
                if (existingTerms.Contains(val)) continue;

                _entries.Add(new GlossaryEntry
                {
                    Term = val,
                    Translation = "",
                    Note = noteCategory
                });
                existingTerms.Add(val);
                added++;
            }
        }

        return added;
    }

    public string ApplyBeforeTranslation(string text)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            GlossaryEntry entry = _entries[i];
            if (string.IsNullOrWhiteSpace(entry.Translation)) continue;
            if (!text.Contains(entry.Term, StringComparison.OrdinalIgnoreCase)) continue;

            text = Regex.Replace(
                text,
                Regex.Escape(entry.Term),
                $"{PlaceholderPrefix}{i}{PlaceholderSuffix}",
                RegexOptions.IgnoreCase);
        }

        return text;
    }

    public string ApplyAfterTranslation(string text)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            string placeholder = $"{PlaceholderPrefix}{i}{PlaceholderSuffix}";
            if (!text.Contains(placeholder)) continue;

            text = text.Replace(placeholder, _entries[i].Translation);
        }

        return text;
    }

    public void ShowSummary()
    {
        int filled = _entries.Count(e => !string.IsNullOrWhiteSpace(e.Translation));
        int empty = _entries.Count - filled;

        Console.WriteLine($"  Glossary: {_entries.Count} terms ({filled} translated, {empty} pending)");

        if (empty > 0)
        {
            Console.WriteLine($"  Terms without translation will be kept as-is.");
            Console.WriteLine($"  Edit '{Path.GetFileName(_path)}' to add translations.");
        }
    }

    public void ShowEntries(int max = 20)
    {
        List<GlossaryEntry> entries = _entries.Take(max).ToList();
        foreach (GlossaryEntry? entry in entries)
        {
            string translation = string.IsNullOrWhiteSpace(entry.Translation)
                ? "(pending)"
                : entry.Translation;
            Console.WriteLine($"    {entry.Term,-25} → {translation,-25} [{entry.Note}]");
        }

        if (_entries.Count > max)
            Console.WriteLine($"    ... and {_entries.Count - max} more");
    }
}
