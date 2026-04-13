using RpgLingo.Translation;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace RpgLingo.RpgMaker;
public class RpgMakerTranslator(Translate translate, TranslationCache cache, SessionStats stats, int maxLineLength = 55, Glossary? glossary = null)
{
    private readonly Translate _translate = translate;
    private readonly TranslationCache _cache = cache;
    private readonly Glossary? _glossary = glossary;
    private readonly SessionStats _stats = stats;
    private int _fromApi;
    private int _fromCache;
    private int _skipped;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> NeatlyFields = ["description", "profile"];

    private static readonly string[] ObjectFields =
        ["name", "description", "message1", "message2", "message3", "message4", "nickname", "profile"];

    public SessionStats Stats => _stats;

    // ==================== Dialog files (Maps, CommonEvents) ====================

    public bool TranslateDialogFile(string filePath)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _fromApi = 0;
        _fromCache = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return false;

        // Maps have "events", CommonEvents is a direct array
        if (root is JsonObject obj && obj.ContainsKey("events"))
            TranslateEvents(obj["events"]!.AsArray());
        else if (root is JsonArray arr)
            TranslateCommonEvents(arr);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  API: {_fromApi} | Cache: {_fromCache} | Skipped: {_skipped}");
        return true;
    }

    private void TranslateEvents(JsonArray events)
    {
        foreach (JsonNode? ev in events)
        {
            if (ev == null) continue;
            JsonArray? pages = ev["pages"]?.AsArray();
            if (pages == null) continue;

            foreach (JsonNode? page in pages)
            {
                if (page == null) continue;
                JsonArray? list = page["list"]?.AsArray();
                if (list == null) continue;
                TranslateCommandList(list);
            }
        }
    }

    private void TranslateCommonEvents(JsonArray events)
    {
        foreach (JsonNode? ev in events)
        {
            if (ev == null) continue;
            JsonArray? list = ev["list"]?.AsArray();
            if (list == null) continue;
            TranslateCommandList(list);
        }
    }

    private void TranslateCommandList(JsonArray list)
    {
        int i = 0;
        while (i < list.Count)
        {
            JsonNode? cmd = list[i];
            if (cmd == null) { i++; continue; }

            int code = cmd["code"]?.GetValue<int>() ?? 0;

            switch (code)
            {
                case 401: // Dialog: group consecutive lines
                    i = TranslateDialogBlock(list, i);
                    break;
                case 102: // Choices
                    TranslateChoices(cmd);
                    i++;
                    break;
                case 402: // Choice answer
                    TranslateChoiceAnswer(cmd);
                    i++;
                    break;
                case 405: // Scroll text
                    i = TranslateDialogBlock(list, i, 405);
                    break;
                default:
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// Groups consecutive lines with the same code (401 or 405),
    /// translates them as a block and redistributes the result.
    /// </summary>
    private int TranslateDialogBlock(JsonArray list, int startIndex, int targetCode = 401)
    {
        // Collect all consecutive lines
        List<string> lines = [];
        int i = startIndex;
        while (i < list.Count)
        {
            JsonNode? cmd = list[i];
            int code = cmd?["code"]?.GetValue<int>() ?? 0;
            if (code != targetCode) break;

            string line = cmd!["parameters"]![0]?.GetValue<string>() ?? "";
            lines.Add(line);
            i++;
        }

        if (lines.Count == 0) return i;

        // Join lines and prepare control codes
        string combined = string.Join(" ", lines);
        ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(combined);

        if (string.IsNullOrWhiteSpace(prepared.TextForTranslation)
            || prepared.TextForTranslation.All(c => !char.IsLetter(c)))
        {
            _skipped += lines.Count;
            return i;
        }

        // Translate (with cache)
        string translated = TranslateText(prepared, out bool fromCache);
        if (fromCache) _fromCache += lines.Count;
        else _fromApi += lines.Count;

        if (translated == combined)
            return i;

        // Redistribute into lines that fit the dialog window
        List<string> newLines = WrapTextOptimal(translated, maxLineLength);

        // Update existing lines and add/remove as needed
        int originalCount = lines.Count;
        JsonNode templateCmd = list[startIndex]!.Deserialize<JsonNode>()!;

        for (int j = 0; j < Math.Max(originalCount, newLines.Count); j++)
        {
            int listIndex = startIndex + j;

            if (j < newLines.Count && j < originalCount)
            {
                // Update existing line
                list[listIndex]!["parameters"]![0] = JsonValue.Create(newLines[j]);
            }
            else if (j >= originalCount)
            {
                // Need more lines: insert new nodes
                JsonNode newCmd = templateCmd.Deserialize<JsonNode>()!;
                newCmd["parameters"]![0] = JsonValue.Create(newLines[j]);
                list.Insert(listIndex, newCmd);
                i++; // Adjust the output index
            }
            else
            {
                // Extra original lines: set to empty
                list[listIndex]!["parameters"]![0] = JsonValue.Create("");
            }
        }

        Console.WriteLine($"  [{_fromApi + _fromCache}] {prepared.TextForTranslation[..Math.Min(80, prepared.TextForTranslation.Length)]}...");
        return i;
    }

    private void TranslateChoices(JsonNode cmd)
    {
        JsonArray? choices = cmd["parameters"]?[0]?.AsArray();
        if (choices == null) return;

        // Batch: collect all choices and translate at once
        List<string> textsToTranslate = [];
        List<int> indices = [];

        for (int j = 0; j < choices.Count; j++)
        {
            string choice = choices[j]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(choice)) continue;
            textsToTranslate.Add(choice);
            indices.Add(j);
        }

        if (textsToTranslate.Count == 0) return;

        List<(string? text, bool fromCache)> results = TranslateTextBatch(textsToTranslate);

        for (int k = 0; k < indices.Count; k++)
        {
            if (results[k].text != null)
            {
                choices[indices[k]] = JsonValue.Create(results[k].text);
                if (results[k].fromCache) _fromCache++;
                else _fromApi++;
            }
        }
    }

    private void TranslateChoiceAnswer(JsonNode cmd)
    {
        JsonArray? parameters = cmd["parameters"]?.AsArray();
        if (parameters == null || parameters.Count < 2) return;

        string text = parameters[1]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;

        ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(text);
        parameters[1] = JsonValue.Create(TranslateText(prepared, out bool fromCache));
        if (fromCache) _fromCache++;
        else _fromApi++;
    }

    // ==================== Object files (Items, Weapons, etc.) ====================

    public bool TranslateObjectFile(string filePath)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _fromApi = 0;
        _fromCache = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonArray? root = JsonNode.Parse(json)?.AsArray();
        if (root == null) return false;

        // Batch: collect all texts, translate and apply them
        List<(JsonNode node, string field, bool neatly)> fieldsToTranslate = [];

        foreach (JsonNode? item in root)
        {
            if (item == null) continue;
            foreach (string field in ObjectFields)
            {
                string val = item[field]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(val) && val.Any(char.IsLetter))
                    fieldsToTranslate.Add((item, field, NeatlyFields.Contains(field)));
                else
                    _skipped++;
            }
        }

        // Extract texts for batch
        List<string> textsForBatch = fieldsToTranslate
            .Select(f => f.node[f.field]!.GetValue<string>())
            .ToList();

        List<(string? text, bool fromCache)> translations = TranslateTextBatch(textsForBatch);

        for (int i = 0; i < fieldsToTranslate.Count; i++)
        {
            (JsonNode node, string field, bool neatly) = fieldsToTranslate[i];
            if (translations[i].text != null)
            {
                string result = translations[i].text!;
                if (neatly && result.Length > maxLineLength)
                {
                    List<string> neatLines = WrapTextOptimal(result, maxLineLength);
                    result = string.Join("\n", neatLines);
                }
                node[field] = JsonValue.Create(result);
                if (translations[i].fromCache) _fromCache++;
                else _fromApi++;
            }
        }

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  API: {_fromApi} | Cache: {_fromCache} | Skipped: {_skipped}");
        return true;
    }

    // ==================== Generic files (custom plugins) ====================

    /// <summary>
    /// Recursively translates all string values matching the given keys.
    /// Useful for plugin files with variable structure (GalleryList, etc.).
    /// </summary>
    public bool TranslateGenericFile(string filePath, HashSet<string> keysToTranslate)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _fromApi = 0;
        _fromCache = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return false;

        TranslateRecursive(root, keysToTranslate);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  API: {_fromApi} | Cache: {_fromCache} | Skipped: {_skipped}");
        return true;
    }

    private void TranslateRecursive(JsonNode node, HashSet<string> keys)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> prop in obj.ToList())
            {
                if (prop.Value is JsonObject or JsonArray)
                    TranslateRecursive(prop.Value!, keys);
                else if (keys.Contains(prop.Key))
                {
                    string val = prop.Value?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(val) && val.Any(char.IsLetter))
                    {
                        ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(val);
                        obj[prop.Key] = JsonValue.Create(TranslateText(prepared, out bool fromCache));
                        if (fromCache) _fromCache++;
                        else _fromApi++;
                    }
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (item is JsonObject or JsonArray)
                    TranslateRecursive(item!, keys);
            }
        }
    }

    // ==================== System.json ====================

    public bool TranslateSystemFile(string filePath)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _fromApi = 0;
        _fromCache = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return false;

        TranslateFieldSimple(root, "gameTitle");

        // terms.messages contains system messages
        JsonNode? messages = root["terms"]?["messages"];
        if (messages is JsonObject msgObj)
        {
            foreach (KeyValuePair<string, JsonNode?> prop in msgObj.ToList())
            {
                string val = prop.Value?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(val) && val.Any(char.IsLetter))
                {
                    ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(val);
                    msgObj[prop.Key] = JsonValue.Create(TranslateText(prepared, out bool fromCache));
                    if (fromCache) _fromCache++;
                    else _fromApi++;
                }
            }
        }

        // terms.basic and terms.commands
        TranslateStringArray(root["terms"]?["basic"]);
        TranslateStringArray(root["terms"]?["commands"]);

        // armorTypes, skillTypes, weaponTypes, elements
        TranslateStringArray(root["armorTypes"]);
        TranslateStringArray(root["skillTypes"]);
        TranslateStringArray(root["weaponTypes"]);
        TranslateStringArray(root["elements"]);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  API: {_fromApi} | Cache: {_fromCache} | Skipped: {_skipped}");
        return true;
    }

    // ==================== CSV localization files (text.csv) ====================

    /// <summary>
    /// Language column header mappings: ISO 639-1 code → possible CSV headers.
    /// </summary>
    private static readonly Dictionary<string, string[]> LanguageHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["English", "EN", "english", "ENG", "eng"],
        ["es"] = ["Spanish", "ES", "spanish", "Español", "español"],
        ["ja"] = ["JP", "JA", "Japanese", "japanese", "日本語", "日本语"],
        ["zh"] = ["繁中", "簡中", "Chinese", "ZH", "中文"],
        ["vi"] = ["VT", "VI", "Vietnamese", "vietnamese"],
        ["fr"] = ["FR", "French", "french", "Français"],
        ["de"] = ["DE", "German", "german", "Deutsch"],
        ["pt"] = ["PT", "Portuguese", "portuguese", "Português"],
        ["ko"] = ["KO", "Korean", "korean", "한국어"],
        ["ru"] = ["RU", "Russian", "russian", "Русский"],
        ["it"] = ["IT", "Italian", "italian", "Italiano"],
    };

    /// <summary>
    /// Translates a CSV localization file (text.csv).
    /// Finds the source language column, translates each cell,
    /// and overwrites that column with the translations.
    /// </summary>
    public bool TranslateCsvFile(string filePath, string sourceLang, string targetLang)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _fromApi = 0;
        _fromCache = 0;
        _skipped = 0;

        // Parse CSV respecting CRLF as row delimiter and LF inside quotes as content
        string raw = File.ReadAllText(filePath);
        List<List<string>> rows = ParseCsv(raw);
        if (rows.Count < 2) return false; // Need at least header + 1 data row

        List<string> header = rows[0];

        // Find source language column
        int sourceCol = FindLanguageColumn(header, sourceLang);
        if (sourceCol < 0)
        {
            Console.WriteLine($"  Could not find column for language '{sourceLang}' in CSV header.");
            Console.WriteLine($"  Available columns: {string.Join(", ", header.Where(h => !string.IsNullOrWhiteSpace(h)))}");
            return false;
        }

        Console.WriteLine($"  Source column: [{sourceCol}] \"{header[sourceCol]}\"");

        // Collect all texts to translate
        List<(int row, string text)> textsToTranslate = [];
        for (int r = 1; r < rows.Count; r++)
        {
            if (sourceCol >= rows[r].Count) continue;
            string cell = rows[r][sourceCol];
            if (!string.IsNullOrWhiteSpace(cell) && cell.Any(char.IsLetter))
                textsToTranslate.Add((r, cell));
        }

        if (textsToTranslate.Count == 0)
        {
            Console.WriteLine("  No translatable text found in source column.");
            return false;
        }

        Console.WriteLine($"  Translating {textsToTranslate.Count} cells...");

        // Translate in batches
        List<string> allTexts = textsToTranslate.Select(t => t.text).ToList();
        List<(string? text, bool fromCache)> translations = TranslateTextBatch(allTexts);

        for (int i = 0; i < textsToTranslate.Count; i++)
        {
            int rowIdx = textsToTranslate[i].row;
            (string? text, bool fromCache) = translations[i];
            if (text != null)
            {
                // Ensure row has enough columns
                while (rows[rowIdx].Count <= sourceCol)
                    rows[rowIdx].Add("");
                rows[rowIdx][sourceCol] = text;
                if (fromCache) _fromCache++;
                else _fromApi++;
            }
        }

        // Write back as CSV with CRLF line endings
        File.WriteAllText(filePath, WriteCsv(rows));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  API: {_fromApi} | Cache: {_fromCache} | Skipped: {_skipped}");
        return true;
    }

    public CharCount CountCsvFile(string filePath, string sourceLang)
    {
        string raw = File.ReadAllText(filePath);
        List<List<string>> rows = ParseCsv(raw);
        if (rows.Count < 2) return new(0, 0, 0, 0);

        int sourceCol = FindLanguageColumn(rows[0], sourceLang);
        if (sourceCol < 0) return new(0, 0, 0, 0);

        CharCounter counter = new(_cache);
        for (int r = 1; r < rows.Count; r++)
        {
            if (sourceCol >= rows[r].Count) continue;
            counter.Add(rows[r][sourceCol]);
        }
        return counter.Result;
    }

    private static int FindLanguageColumn(List<string> header, string langCode)
    {
        // Try exact match with known headers
        if (LanguageHeaders.TryGetValue(langCode, out string[]? candidates))
        {
            foreach (string candidate in candidates)
            {
                int idx = header.FindIndex(h => h.Trim() == candidate);
                if (idx >= 0) return idx;
            }
        }

        // Fallback: case-insensitive search for the code itself
        int fallback = header.FindIndex(h =>
            h.Trim().Equals(langCode, StringComparison.OrdinalIgnoreCase));
        return fallback;
    }

    /// <summary>
    /// Parses CSV respecting quoted fields.
    /// CRLF = row delimiter, LF inside quotes = cell content.
    /// </summary>
    private static List<List<string>> ParseCsv(string raw)
    {
        List<List<string>> rows = [];
        List<string> currentRow = [];
        System.Text.StringBuilder currentField = new();
        bool inQuotes = false;
        int i = 0;

        while (i < raw.Length)
        {
            char c = raw[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote ""
                    if (i + 1 < raw.Length && raw[i + 1] == '"')
                    {
                        currentField.Append('"');
                        i += 2;
                    }
                    else
                    {
                        // End of quoted field
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    // Any character inside quotes (including LF) is content
                    currentField.Append(c);
                    i++;
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                }
                else if (c == ',')
                {
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    i++;
                }
                else if (c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    // CRLF = end of row
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow);
                    currentRow = [];
                    i += 2;
                }
                else if (c == '\n' && !inQuotes)
                {
                    // Bare LF outside quotes: treat as row end too (safety)
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow);
                    currentRow = [];
                    i++;
                }
                else
                {
                    currentField.Append(c);
                    i++;
                }
            }
        }

        // Last field/row
        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }

    /// <summary>
    /// Writes rows back to CSV with proper quoting and CRLF line endings.
    /// </summary>
    private static string WriteCsv(List<List<string>> rows)
    {
        System.Text.StringBuilder sb = new();
        foreach (List<string> row in rows)
        {
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0) sb.Append(',');

                string field = row[c];
                // Quote if contains comma, quote, LF, or CR
                if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                {
                    sb.Append('"');
                    sb.Append(field.Replace("\"", "\"\""));
                    sb.Append('"');
                }
                else
                {
                    sb.Append(field);
                }
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    // ==================== Character counting (dry run) ====================

    public record CharCount(long Total, long Cached, long ToTranslate, int Strings);

    public CharCount CountDialogFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return new(0, 0, 0, 0);

        CharCounter counter = new(_cache);

        if (root is JsonObject obj && obj.ContainsKey("events"))
            CountEvents(obj["events"]!.AsArray(), counter);
        else if (root is JsonArray arr)
            CountCommonEvents(arr, counter);

        return counter.Result;
    }

    public CharCount CountObjectFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        JsonArray? root = JsonNode.Parse(json)?.AsArray();
        if (root == null) return new(0, 0, 0, 0);

        CharCounter counter = new(_cache);

        foreach (JsonNode? item in root)
        {
            if (item == null) continue;
            foreach (string field in ObjectFields)
                counter.Add(item[field]?.GetValue<string>());
        }

        return counter.Result;
    }

    public CharCount CountSystemFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return new(0, 0, 0, 0);

        CharCounter counter = new(_cache);
        counter.Add(root["gameTitle"]?.GetValue<string>());

        JsonNode? messages = root["terms"]?["messages"];
        if (messages is JsonObject msgObj)
            foreach (KeyValuePair<string, JsonNode?> prop in msgObj)
                counter.Add(prop.Value?.GetValue<string>());

        CountJsonArray(root["terms"]?["basic"], counter);
        CountJsonArray(root["terms"]?["commands"], counter);
        CountJsonArray(root["armorTypes"], counter);
        CountJsonArray(root["skillTypes"], counter);
        CountJsonArray(root["weaponTypes"], counter);
        CountJsonArray(root["elements"], counter);

        return counter.Result;
    }

    private static void CountEvents(JsonArray events, CharCounter counter)
    {
        foreach (JsonNode? ev in events)
        {
            if (ev == null) continue;
            JsonArray? pages = ev["pages"]?.AsArray();
            if (pages == null) continue;
            foreach (JsonNode? page in pages)
            {
                JsonArray? list = page?["list"]?.AsArray();
                if (list != null) CountCommandList(list, counter);
            }
        }
    }

    private static void CountCommonEvents(JsonArray events, CharCounter counter)
    {
        foreach (JsonNode? ev in events)
        {
            JsonArray? list = ev?["list"]?.AsArray();
            if (list != null) CountCommandList(list, counter);
        }
    }

    private static void CountCommandList(JsonArray list, CharCounter counter)
    {
        int i = 0;
        while (i < list.Count)
        {
            int code = list[i]?["code"]?.GetValue<int>() ?? 0;
            switch (code)
            {
                case 401 or 405:
                    {
                        List<string> lines = [];
                        while (i < list.Count && (list[i]?["code"]?.GetValue<int>() ?? 0) == code)
                        {
                            lines.Add(list[i]!["parameters"]![0]?.GetValue<string>() ?? "");
                            i++;
                        }
                        string combined = string.Join(" ", lines);
                        ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(combined);
                        counter.Add(prepared.TextForTranslation);
                        break;
                    }
                case 102:
                    {
                        JsonArray? choices = list[i]?["parameters"]?[0]?.AsArray();
                        if (choices != null)
                            foreach (JsonNode? c in choices)
                                counter.Add(c?.GetValue<string>());
                        i++;
                        break;
                    }
                case 402:
                    {
                        JsonArray? p = list[i]?["parameters"]?.AsArray();
                        if (p is { Count: >= 2 })
                            counter.Add(p[1]?.GetValue<string>());
                        i++;
                        break;
                    }
                default:
                    i++;
                    break;
            }
        }
    }

    private static void CountJsonArray(JsonNode? node, CharCounter counter)
    {
        if (node is not JsonArray arr) return;
        foreach (JsonNode? item in arr)
            counter.Add(item?.GetValue<string>());
    }

    private class CharCounter(TranslationCache cache)
    {
        private readonly TranslationCache _cache = cache;
        private long _total;
        private long _cached;
        private int _strings;

        public void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter)) return;
            ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(text);
            _total += prepared.TextForTranslation.Length;
            _strings++;
            if (_cache.TryGet(prepared.TextForTranslation, out _))
                _cached += prepared.TextForTranslation.Length;
        }

        public CharCount Result => new(_total, _cached, _total - _cached, _strings);
    }

    // ==================== Translation utilities ====================

    /// <summary>
    /// Translates a text already prepared with ControlCodeHelper.
    /// </summary>
    private string TranslateText(ControlCodeHelper.PreparedText prepared, out bool fromCache)
    {
        string leadingSpace = prepared.Original.Length > 0 && prepared.Original[0] == ' ' ? " " : "";
        string cleanText = prepared.TextForTranslation;

        // Track detected control codes and script vars
        _stats.ControlCodesDetected += prepared.ControlCodes.Count;
        _stats.ScriptVarsDetected += prepared.ScriptVars.Count;

        if (_cache.TryGet(cleanText, out string cached))
        {
            _stats.AddCacheHit(cleanText.Length);
            fromCache = true;
            return leadingSpace + ControlCodeHelper.Restore(cached, prepared);
        }

        fromCache = false;

        // Apply glossary
        string textForApi = _glossary != null
            ? _glossary.ApplyBeforeTranslation(cleanText)
            : cleanText;

        string? result;
        try
        {
            result = _translate.TranslateText(textForApi);
        }
        catch (Exception ex)
        {
            _stats.AddFailure();
            Console.WriteLine($"    Translation error: {ex.Message}");
            return prepared.Original;
        }

        if (result == null)
            return prepared.Original;

        // Restore glossary
        if (_glossary != null)
            result = _glossary.ApplyAfterTranslation(result);

        // Preserve upper/lowercase
        if (cleanText.Length > 0 && result.Length > 0)
        {
            if (char.IsLower(cleanText[0]) && char.IsUpper(result[0]))
                result = char.ToLower(result[0]) + result[1..];
        }

        result = HttpUtility.HtmlDecode(result);
        _cache.Set(cleanText, result);
        _stats.AddTranslation(cleanText.Length);

        return leadingSpace + ControlCodeHelper.Restore(result, prepared);
    }

    /// <summary>
    /// Translates a batch of texts using the batch API when possible.
    /// </summary>
    private List<(string? text, bool fromCache)> TranslateTextBatch(List<string> texts)
    {
        List<ControlCodeHelper.PreparedText> prepared = texts.Select(ControlCodeHelper.Prepare).ToList();
        List<string> cleanTexts = prepared.Select(p => p.TextForTranslation).ToList();

        // Track control codes and script vars
        foreach (ControlCodeHelper.PreparedText? p in prepared)
        {
            _stats.ControlCodesDetected += p.ControlCodes.Count;
            _stats.ScriptVarsDetected += p.ScriptVars.Count;
        }

        // Separate cached texts from those needing API
        (string? text, bool fromCache)[] results = new (string?, bool)[texts.Count];
        List<(int index, string text)> toTranslate = [];

        for (int i = 0; i < cleanTexts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(cleanTexts[i]) || !cleanTexts[i].Any(char.IsLetter))
            {
                results[i] = (null, false);
                continue;
            }

            if (_cache.TryGet(cleanTexts[i], out string cached))
            {
                results[i] = (ControlCodeHelper.Restore(cached, prepared[i]), true);
                _stats.AddCacheHit(cleanTexts[i].Length);
                continue;
            }

            string forApi = _glossary != null
                ? _glossary.ApplyBeforeTranslation(cleanTexts[i])
                : cleanTexts[i];
            toTranslate.Add((i, forApi));
        }

        if (toTranslate.Count == 0) return results.ToList();

        // Batch translate
        List<string> apiTexts = toTranslate.Select(t => t.text).ToList();
        List<string?> apiResults;
        try
        {
            apiResults = _translate.TranslateTextBatch(apiTexts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Batch error: {ex.Message}");
            _stats.AddFailure();
            return results.ToList();
        }

        long batchChars = 0;
        int batchCount = 0;

        for (int k = 0; k < toTranslate.Count; k++)
        {
            int idx = toTranslate[k].index;
            string? result = apiResults[k];

            if (result == null)
            {
                results[idx] = (null, false);
                continue;
            }

            if (_glossary != null)
                result = _glossary.ApplyAfterTranslation(result);

            if (cleanTexts[idx].Length > 0 && result.Length > 0)
            {
                if (char.IsLower(cleanTexts[idx][0]) && char.IsUpper(result[0]))
                    result = char.ToLower(result[0]) + result[1..];
            }

            result = HttpUtility.HtmlDecode(result);
            _cache.Set(cleanTexts[idx], result);
            results[idx] = (ControlCodeHelper.Restore(result, prepared[idx]), false);

            batchChars += cleanTexts[idx].Length;
            batchCount++;
        }

        if (batchCount > 0)
            _stats.AddBatchTranslation(batchCount, batchChars);

        return results.ToList();
    }

    private void TranslateFieldSimple(JsonNode node, string fieldName)
    {
        string val = node[fieldName]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(val) || !val.Any(char.IsLetter))
        {
            _skipped++;
            return;
        }

        ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(val);
        node[fieldName] = JsonValue.Create(TranslateText(prepared, out bool fromCache));
        if (fromCache) _fromCache++;
        else _fromApi++;
    }

    private void TranslateStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr) return;

        // Batch: recoger todos los textos del array
        List<string> textsToTranslate = [];
        List<int> indices = [];

        for (int i = 0; i < arr.Count; i++)
        {
            string val = arr[i]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(val) || !val.Any(char.IsLetter))
            {
                _skipped++;
                continue;
            }
            textsToTranslate.Add(val);
            indices.Add(i);
        }

        if (textsToTranslate.Count == 0) return;

        List<(string? text, bool fromCache)> results = TranslateTextBatch(textsToTranslate);

        for (int k = 0; k < indices.Count; k++)
        {
            if (results[k].text != null)
            {
                arr[indices[k]] = JsonValue.Create(results[k].text);
                if (results[k].fromCache) _fromCache++;
                else _fromApi++;
            }
        }
    }

    // ==================== WrapText with dynamic programming ====================

    /// <summary>
    /// Distributes text into lines minimizing leftover space (cubic penalty).
    /// Produces more balanced lines than the greedy algorithm.
    /// </summary>
    private static List<string> WrapTextOptimal(string text, int maxLength)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = words.Length;

        // Para textos cortos, no hace falta optimizar
        if (n == 0) return [""];
        if (text.Length <= maxLength) return [text.Trim()];

        double[] minPenalty = new double[n + 1];
        int[] breakPoints = new int[n + 1];
        Array.Fill(minPenalty, double.MaxValue);
        minPenalty[0] = 0;

        for (int j = 1; j <= n; j++)
        {
            int extraSpace = maxLength + 1;
            int iMin = Math.Max(1, j + 1 - (int)Math.Ceiling(maxLength / 2.0));

            for (int i = j; i >= iMin; i--)
            {
                extraSpace -= words[i - 1].Length + 1;

                double cost;
                if (extraSpace < 0)
                    cost = double.MaxValue;
                else if (j == n && extraSpace >= 0)
                    cost = 0; // Last line has no penalty
                else
                    cost = (double)extraSpace * extraSpace * extraSpace;

                double penalty = minPenalty[i - 1] + cost;
                if (penalty < minPenalty[j])
                {
                    minPenalty[j] = penalty;
                    breakPoints[j] = i;
                }
            }
        }

        // Reconstruct lines
        List<string> lines = [];
        ReconstructLines(words, n, breakPoints, lines);
        return lines.Count > 0 ? lines : [""];
    }

    private static void ReconstructLines(string[] words, int j, int[] breakPoints, List<string> lines)
    {
        if (j <= 0) return;
        int i = breakPoints[j];
        ReconstructLines(words, i - 1, breakPoints, lines);
        lines.Add(string.Join(' ', words[(i - 1)..j]));
    }

    // ==================== Already-translated file tracking ====================

    private static readonly string TranslatedMarkerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RpgLingo", "translated");

    private bool IsAlreadyTranslated(string filePath)
    {
        string marker = GetMarkerPath(filePath);
        if (!File.Exists(marker)) return false;

        Console.WriteLine($"  Skipping (already translated): {Path.GetFileName(filePath)}");
        _stats.FilesSkipped++;
        return true;
    }

    private void MarkAsTranslated(string filePath)
    {
        string marker = GetMarkerPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, DateTime.Now.ToString("o"));
        _stats.FilesProcessed++;
    }

    /// <summary>
    /// Generates a unique marker path based on the full file path.
    /// Each game gets its own set of markers.
    /// </summary>
    private static string GetMarkerPath(string filePath)
    {
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(filePath))))[..16];
        return Path.Combine(TranslatedMarkerDir, hash + ".done");
    }

    /// <summary>
    /// Clears markers to force retranslation.
    /// </summary>
    public static void ClearTranslationMarkers()
    {
        if (Directory.Exists(TranslatedMarkerDir))
            Directory.Delete(TranslatedMarkerDir, true);
    }
}
