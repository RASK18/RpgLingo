using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace RpgLingo;
public class RpgMakerTranslator(Translate translate, TranslationCache cache, int maxLineLength = 55)
{
    private readonly Translate _translate = translate;
    private readonly TranslationCache _cache = cache;
    private int _translated;
    private int _skipped;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ==================== Archivos de diálogos (Maps, CommonEvents) ====================

    public void TranslateDialogFile(string filePath)
    {
        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return;

        // Maps tienen "events", CommonEvents es un array directo
        if (root is JsonObject obj && obj.ContainsKey("events"))
            TranslateEvents(obj["events"]!.AsArray());
        else if (root is JsonArray arr)
            TranslateCommonEvents(arr);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
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
                case 401: // Diálogo: agrupar líneas consecutivas
                    i = TranslateDialogBlock(list, i);
                    break;
                case 102: // Opciones de elección
                    TranslateChoices(cmd);
                    i++;
                    break;
                case 402: // Respuesta de elección
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
    /// Agrupa líneas consecutivas con el mismo code (401 o 405),
    /// las traduce como un bloque y redistribuye el resultado.
    /// </summary>
    private int TranslateDialogBlock(JsonArray list, int startIndex, int targetCode = 401)
    {
        // Recopilar todas las líneas consecutivas
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

        // Unir en un solo texto, preservando tags RPG Maker
        string combined = string.Join(" ", lines.Select(CleanForTranslation));

        if (string.IsNullOrWhiteSpace(combined) || combined.All(c => !char.IsLetter(c)))
        {
            _skipped += lines.Count;
            return i;
        }

        // Traducir (con caché)
        string translated = TranslateText(combined);
        if (translated == combined)
        {
            _skipped += lines.Count;
            return i;
        }

        // Redistribuir en líneas que quepan en la ventana de diálogo
        List<string> newLines = WrapText(translated, maxLineLength);

        // Actualizar las líneas existentes y añadir/eliminar según sea necesario
        int originalCount = lines.Count;
        JsonNode templateCmd = list[startIndex]!.Deserialize<JsonNode>()!;

        for (int j = 0; j < Math.Max(originalCount, newLines.Count); j++)
        {
            int listIndex = startIndex + j;

            if (j < newLines.Count && j < originalCount)
            {
                // Actualizar línea existente
                list[listIndex]!["parameters"]![0] = JsonValue.Create(newLines[j]);
                _translated++;
            }
            else if (j >= originalCount)
            {
                // Necesitamos más líneas: insertar nuevos nodos
                JsonNode newCmd = templateCmd.Deserialize<JsonNode>()!;
                newCmd["parameters"]![0] = JsonValue.Create(newLines[j]);
                list.Insert(listIndex, newCmd);
                _translated++;
                i++; // Ajustar el índice de salida
            }
            else
            {
                // Sobran líneas originales: poner vacías
                list[listIndex]!["parameters"]![0] = JsonValue.Create("");
                _skipped++;
            }
        }

        Console.WriteLine($"  [{_translated}] {combined[..Math.Min(80, combined.Length)]}...");
        return i;
    }

    private void TranslateChoices(JsonNode cmd)
    {
        JsonArray? choices = cmd["parameters"]?[0]?.AsArray();
        if (choices == null) return;

        for (int j = 0; j < choices.Count; j++)
        {
            string choice = choices[j]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(choice)) continue;

            string translated = TranslateText(choice);
            choices[j] = JsonValue.Create(translated);
            _translated++;
        }
    }

    private void TranslateChoiceAnswer(JsonNode cmd)
    {
        JsonArray? parameters = cmd["parameters"]?.AsArray();
        if (parameters == null || parameters.Count < 2) return;

        string text = parameters[1]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;

        parameters[1] = JsonValue.Create(TranslateText(text));
        _translated++;
    }

    // ==================== Archivos de objetos (Items, Weapons, etc.) ====================

    public void TranslateObjectFile(string filePath)
    {
        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonArray? root = JsonNode.Parse(json)?.AsArray();
        if (root == null) return;

        foreach (JsonNode? item in root)
        {
            if (item == null) continue;
            TranslateField(item, "name");
            TranslateField(item, "description");
            TranslateField(item, "message1");
            TranslateField(item, "message2");
            TranslateField(item, "message3");
            TranslateField(item, "message4");
            TranslateField(item, "nickname");
            TranslateField(item, "profile");
        }

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
    }

    // ==================== System.json ====================

    public void TranslateSystemFile(string filePath)
    {
        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return;

        TranslateField(root, "gameTitle");

        // terms.messages contiene los mensajes del sistema
        JsonNode? messages = root["terms"]?["messages"];
        if (messages is JsonObject msgObj)
        {
            foreach (KeyValuePair<string, JsonNode?> prop in msgObj.ToList())
            {
                string val = prop.Value?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(val) && val.Any(char.IsLetter))
                {
                    msgObj[prop.Key] = JsonValue.Create(TranslateText(val));
                    _translated++;
                }
            }
        }

        // terms.basic y terms.commands
        TranslateStringArray(root["terms"]?["basic"]);
        TranslateStringArray(root["terms"]?["commands"]);

        // armorTypes, skillTypes, weaponTypes, elements
        TranslateStringArray(root["armorTypes"]);
        TranslateStringArray(root["skillTypes"]);
        TranslateStringArray(root["weaponTypes"]);
        TranslateStringArray(root["elements"]);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
    }

    // ==================== Conteo de caracteres (dry run) ====================

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
        string[] fields = ["name", "description", "message1", "message2", "message3", "message4", "nickname", "profile"];

        foreach (JsonNode? item in root)
        {
            if (item == null) continue;
            foreach (string field in fields)
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
                        string combined = string.Join(" ", lines.Select(CleanForTranslation));
                        counter.Add(combined);
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
            string clean = text.Replace("\\n", " ").Trim();
            _total += clean.Length;
            _strings++;
            if (_cache.TryGet(clean, out _))
                _cached += clean.Length;
        }

        public CharCount Result => new(_total, _cached, _total - _cached, _strings);
    }

    // ==================== Utilidades ====================

    private string TranslateText(string text)
    {
        string cleanText = CleanForTranslation(text);

        if (_cache.TryGet(cleanText, out string cached))
            return RestoreTags(text, cached);

        string? result = _translate.ToSpanish(cleanText);
        if (result == null)
            return text;

        // Preservar mayúsculas/minúsculas del original
        if (text.Length > 0 && cleanText.Length > 0 && result.Length > 0)
        {
            if (char.IsLower(cleanText[0]) && char.IsUpper(result[0]))
                result = char.ToLower(result[0]) + result[1..];
        }

        result = HttpUtility.HtmlDecode(result);
        _cache.Set(cleanText, result);
        return RestoreTags(text, result);
    }

    /// <summary>
    /// Limpia tags de RPG Maker para que no confundan al traductor,
    /// pero los preserva para poder restaurarlos después.
    /// </summary>
    private static string CleanForTranslation(string text)
    {
        // Eliminar \N[x], \V[x], \C[x], etc. temporalmente reemplazándolos con placeholders
        // que el traductor no toque
        return text
            .Replace("\\n", " ")
            .Trim();
    }

    private static string RestoreTags(string original, string translated)
    {
        // Si el original empezaba con \N[x], restaurar al inicio
        if (original.StartsWith("\\N["))
        {
            int end = original.IndexOf(']');
            if (end > 0)
            {
                string tag = original[..(end + 1)];
                translated = tag + translated;
            }
        }
        return translated;
    }

    private void TranslateField(JsonNode node, string fieldName)
    {
        string val = node[fieldName]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(val) || !val.Any(char.IsLetter))
        {
            _skipped++;
            return;
        }

        node[fieldName] = JsonValue.Create(TranslateText(val));
        _translated++;
    }

    private void TranslateStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr) return;
        for (int i = 0; i < arr.Count; i++)
        {
            string val = arr[i]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(val) || !val.Any(char.IsLetter))
            {
                _skipped++;
                continue;
            }
            arr[i] = JsonValue.Create(TranslateText(val));
            _translated++;
        }
    }

    /// <summary>
    /// Redistribuye texto en líneas de longitud máxima,
    /// cortando por espacios para que quepan en la ventana de diálogo.
    /// </summary>
    private static List<string> WrapText(string text, int maxLength)
    {
        List<string> lines = [];
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string current = "";

        foreach (string word in words)
        {
            if (current.Length == 0)
            {
                current = word;
            }
            else if (current.Length + 1 + word.Length <= maxLength)
            {
                current += " " + word;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
            lines.Add(current);

        return lines.Count > 0 ? lines : [""];
    }
}
