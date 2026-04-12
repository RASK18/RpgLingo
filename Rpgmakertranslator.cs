using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace RpgLingo;
public class RpgMakerTranslator(Translate translate, TranslationCache cache, SessionStats stats, int maxLineLength = 55, Glossary? glossary = null)
{
    private readonly Translate _translate = translate;
    private readonly TranslationCache _cache = cache;
    private readonly Glossary? _glossary = glossary;
    private readonly SessionStats _stats = stats;
    private int _translated;
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

    // ==================== Archivos de diálogos (Maps, CommonEvents) ====================

    public bool TranslateDialogFile(string filePath)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return false;

        // Maps tienen "events", CommonEvents es un array directo
        if (root is JsonObject obj && obj.ContainsKey("events"))
            TranslateEvents(obj["events"]!.AsArray());
        else if (root is JsonArray arr)
            TranslateCommonEvents(arr);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
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

        // Unir líneas y preparar control codes
        string combined = string.Join(" ", lines);
        ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(combined);

        if (string.IsNullOrWhiteSpace(prepared.TextForTranslation)
            || prepared.TextForTranslation.All(c => !char.IsLetter(c)))
        {
            _skipped += lines.Count;
            return i;
        }

        // Traducir (con caché)
        string translated = TranslateText(prepared);
        if (translated == combined)
        {
            _skipped += lines.Count;
            return i;
        }

        // Redistribuir en líneas que quepan en la ventana de diálogo
        List<string> newLines = WrapTextOptimal(translated, maxLineLength);

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

        Console.WriteLine($"  [{_translated}] {prepared.TextForTranslation[..Math.Min(80, prepared.TextForTranslation.Length)]}...");
        return i;
    }

    private void TranslateChoices(JsonNode cmd)
    {
        JsonArray? choices = cmd["parameters"]?[0]?.AsArray();
        if (choices == null) return;

        // Batch: recoger todos los choices y traducir de una vez
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

        List<string?> results = TranslateTextBatch(textsToTranslate);

        for (int k = 0; k < indices.Count; k++)
        {
            if (results[k] != null)
            {
                choices[indices[k]] = JsonValue.Create(results[k]);
                _translated++;
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
        parameters[1] = JsonValue.Create(TranslateText(prepared));
        _translated++;
    }

    // ==================== Archivos de objetos (Items, Weapons, etc.) ====================

    public bool TranslateObjectFile(string filePath)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonArray? root = JsonNode.Parse(json)?.AsArray();
        if (root == null) return false;

        // Batch: recoger todos los textos, traducirlos y aplicarlos
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

        // Extraer textos para batch
        List<string> textsForBatch = fieldsToTranslate
            .Select(f => f.node[f.field]!.GetValue<string>())
            .ToList();

        List<string?> translations = TranslateTextBatch(textsForBatch);

        for (int i = 0; i < fieldsToTranslate.Count; i++)
        {
            (JsonNode node, string field, bool neatly) = fieldsToTranslate[i];
            if (translations[i] != null)
            {
                string result = translations[i]!;
                if (neatly && result.Length > maxLineLength)
                {
                    List<string> neatLines = WrapTextOptimal(result, maxLineLength);
                    result = string.Join("\n", neatLines);
                }
                node[field] = JsonValue.Create(result);
                _translated++;
            }
        }

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
        return true;
    }

    // ==================== Archivos genéricos (plugins custom) ====================

    /// <summary>
    /// Traduce recursivamente todos los valores string que correspondan a las claves indicadas.
    /// Útil para archivos de plugins con estructura variable (GalleryList, etc.).
    /// </summary>
    public bool TranslateGenericFile(string filePath, HashSet<string> keysToTranslate)
    {
        if (IsAlreadyTranslated(filePath)) return false;

        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return false;

        TranslateRecursive(root, keysToTranslate);

        File.WriteAllText(filePath, root.ToJsonString(WriteOptions));
        MarkAsTranslated(filePath);
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
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
                        obj[prop.Key] = JsonValue.Create(TranslateText(prepared));
                        _translated++;
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

        _translated = 0;
        _skipped = 0;

        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return false;

        TranslateFieldSimple(root, "gameTitle");

        // terms.messages contiene los mensajes del sistema
        JsonNode? messages = root["terms"]?["messages"];
        if (messages is JsonObject msgObj)
        {
            foreach (KeyValuePair<string, JsonNode?> prop in msgObj.ToList())
            {
                string val = prop.Value?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(val) && val.Any(char.IsLetter))
                {
                    ControlCodeHelper.PreparedText prepared = ControlCodeHelper.Prepare(val);
                    msgObj[prop.Key] = JsonValue.Create(TranslateText(prepared));
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
        MarkAsTranslated(filePath);
        Console.WriteLine($"  Traducidos: {_translated} | Omitidos: {_skipped}");
        return true;
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

    // ==================== Utilidades de traducción ====================

    /// <summary>
    /// Traduce un texto ya preparado con ControlCodeHelper.
    /// </summary>
    private string TranslateText(ControlCodeHelper.PreparedText prepared)
    {
        string leadingSpace = prepared.Original.Length > 0 && prepared.Original[0] == ' ' ? " " : "";
        string cleanText = prepared.TextForTranslation;

        // Rastrear control codes y script vars detectados
        _stats.ControlCodesDetected += prepared.ControlCodes.Count;
        _stats.ScriptVarsDetected += prepared.ScriptVars.Count;

        if (_cache.TryGet(cleanText, out string cached))
        {
            _stats.AddCacheHit(cleanText.Length);
            return leadingSpace + ControlCodeHelper.Restore(cached, prepared);
        }

        // Aplicar glosario
        string textForApi = _glossary != null
            ? _glossary.ApplyBeforeTranslation(cleanText)
            : cleanText;

        string? result;
        try
        {
            result = _translate.ToSpanish(textForApi);
        }
        catch (Exception ex)
        {
            _stats.AddFailure();
            Console.WriteLine($"    Error traduciendo: {ex.Message}");
            return prepared.Original;
        }

        if (result == null)
            return prepared.Original;

        // Restaurar glosario
        if (_glossary != null)
            result = _glossary.ApplyAfterTranslation(result);

        // Preservar mayúsculas/minúsculas
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
    /// Traduce un batch de textos usando la API batch cuando es posible.
    /// </summary>
    private List<string?> TranslateTextBatch(List<string> texts)
    {
        List<ControlCodeHelper.PreparedText> prepared = texts.Select(ControlCodeHelper.Prepare).ToList();
        List<string> cleanTexts = prepared.Select(p => p.TextForTranslation).ToList();

        // Rastrear control codes y script vars
        foreach (ControlCodeHelper.PreparedText? p in prepared)
        {
            _stats.ControlCodesDetected += p.ControlCodes.Count;
            _stats.ScriptVarsDetected += p.ScriptVars.Count;
        }

        // Separar textos cacheados de los que necesitan API
        string?[] results = new string?[texts.Count];
        List<(int index, string text)> toTranslate = [];

        for (int i = 0; i < cleanTexts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(cleanTexts[i]) || !cleanTexts[i].Any(char.IsLetter))
            {
                results[i] = null;
                continue;
            }

            if (_cache.TryGet(cleanTexts[i], out string cached))
            {
                results[i] = ControlCodeHelper.Restore(cached, prepared[i]);
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
            apiResults = _translate.ToSpanishBatch(apiTexts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Error en batch: {ex.Message}");
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
                results[idx] = null;
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
            results[idx] = ControlCodeHelper.Restore(result, prepared[idx]);

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
        node[fieldName] = JsonValue.Create(TranslateText(prepared));
        _translated++;
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

        List<string?> results = TranslateTextBatch(textsToTranslate);

        for (int k = 0; k < indices.Count; k++)
        {
            if (results[k] != null)
            {
                arr[indices[k]] = JsonValue.Create(results[k]);
                _translated++;
            }
        }
    }

    // ==================== WrapText con programación dinámica ====================

    /// <summary>
    /// Distribuye texto en líneas minimizando el espacio sobrante (cubo).
    /// Produce líneas más equilibradas que el algoritmo greedy.
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
                    cost = 0; // Última línea no penaliza
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

        // Reconstruir líneas
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

    // ==================== Control de archivos ya traducidos ====================

    private static readonly string TranslatedMarkerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RpgLingo", "translated");

    private bool IsAlreadyTranslated(string filePath)
    {
        string marker = GetMarkerPath(filePath);
        if (!File.Exists(marker)) return false;

        Console.WriteLine($"  Saltando (ya traducido): {Path.GetFileName(filePath)}");
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
    /// Genera un path de marcador único basado en la ruta completa del archivo.
    /// Así cada juego tiene sus propios marcadores.
    /// </summary>
    private static string GetMarkerPath(string filePath)
    {
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(filePath))))[..16];
        return Path.Combine(TranslatedMarkerDir, hash + ".done");
    }

    /// <summary>
    /// Limpia los marcadores para forzar retraducción.
    /// </summary>
    public static void ClearTranslationMarkers()
    {
        if (Directory.Exists(TranslatedMarkerDir))
            Directory.Delete(TranslatedMarkerDir, true);
    }
}
