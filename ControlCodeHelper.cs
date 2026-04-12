using System.Text.RegularExpressions;

namespace RpgLingo;

/// <summary>
/// Maneja todos los escape sequences de RPG Maker (colores, iconos, variables, nombres, etc.)
/// reemplazándolos con placeholders unicode antes de traducir y restaurándolos después.
/// También maneja saltos de línea con reposicionamiento proporcional.
/// </summary>
public static partial class ControlCodeHelper
{
    // Patrón que captura todos los control codes de RPG Maker:
    // \C[n], \N[n], \V[n], \I[n], \{, \}, \$, \!, \., \|, \>, \<, \^, etc.
    // En JSON estos se representan como \\C[n], \\N[n], etc.
    private static readonly Regex ControlCodeRegex = BuildControlCodeRegex();

    // Patrones de variables de script y plugins de RPG Maker:
    // <tag:value>, <TAG[123]>, $gameVariables[n], _tv[\"name\"], set_npm(8,"x",0), etc.

    private const string TagPrefix = "⟦CC";
    private const string ScriptPrefix = "⟦SV";
    private const string TagSuffix = "⟧";
    private const string NewlinePrefix = "⟦NL";
    private const string NewlineSuffix = "⟧";

    [GeneratedRegex(@"\\\\[A-Za-z$!><\^\|\{\}](\[[^\]]*\])?")]
    private static partial Regex BuildControlCodeRegex();

    private static readonly Regex ScriptVarRegex = new(
        @"<[^>]*?:[^>]*?>" +
        @"|<[^>]*?\[[^\]]*?\][^>]*?>" +
        @"|\$game\w+\[\d+\]" +
        @"|\$\w+\[[^\]]+\]\s*=?" +
        @"|\w+\[\\\\?""[^""]*?\\\\?""\]" +
        @"|\w+\([^)]*?""[^""]*?""[^)]*?\)",
        RegexOptions.Compiled);

    public record PreparedText(
        string TextForTranslation,
        List<string> ControlCodes,
        List<string> ScriptVars,
        List<NewlineInfo> Newlines,
        string Original);

    public record NewlineInfo(string Token, string Value, double RelativePosition);

    /// <summary>
    /// Prepara el texto para traducción: extrae control codes, variables de script
    /// y saltos de línea, los reemplaza con placeholders y devuelve el texto limpio.
    /// </summary>
    public static PreparedText Prepare(string input)
    {
        string text = input;
        List<string> controlCodes = [];
        List<string> scriptVars = [];
        List<NewlineInfo> newlines = [];

        // Paso 1: Reemplazar variables de script con placeholders
        int svIndex = 0;
        text = ScriptVarRegex.Replace(text, match =>
        {
            scriptVars.Add(match.Value);
            return $"{ScriptPrefix}{svIndex++}{TagSuffix}";
        });

        // Paso 2: Reemplazar control codes con placeholders
        int ccIndex = 0;
        text = ControlCodeRegex.Replace(text, match =>
        {
            controlCodes.Add(match.Value);
            return $"{TagPrefix}{ccIndex++}{TagSuffix}";
        });

        // Paso 3: Extraer saltos de línea con posiciones relativas
        int nlIndex = 0;
        int textLengthWithoutNewlines = text.Replace("\\n", "").Replace("\n", "").Length;

        if (textLengthWithoutNewlines > 0)
        {
            // Manejar \n literal (como aparece en JSON de RPG Maker)
            Regex nlRegex = new(@"\\n|\n");
            int charsSoFar = 0;
            text = nlRegex.Replace(text, match =>
            {
                charsSoFar = match.Index;
                double relPos = textLengthWithoutNewlines > 0
                    ? (double)charsSoFar / textLengthWithoutNewlines
                    : 0;

                string token = $"{NewlinePrefix}{nlIndex++}{NewlineSuffix}";
                newlines.Add(new NewlineInfo(token, match.Value, Math.Clamp(relPos, 0, 1)));
                return " "; // Reemplazar con espacio para traducción
            });
        }

        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        return new PreparedText(text, controlCodes, scriptVars, newlines, input);
    }

    /// <summary>
    /// Restaura los control codes y saltos de línea en el texto traducido.
    /// Los saltos de línea se reinsertan en posiciones proporcionalmente equivalentes.
    /// </summary>
    public static string Restore(string translated, PreparedText prepared)
    {
        string result = translated;

        // Paso 1: Restaurar variables de script
        for (int i = 0; i < prepared.ScriptVars.Count; i++)
        {
            string placeholder = $"{ScriptPrefix}{i}{TagSuffix}";
            result = result.Replace(placeholder, prepared.ScriptVars[i]);
        }

        // Paso 2: Restaurar control codes
        for (int i = 0; i < prepared.ControlCodes.Count; i++)
        {
            string placeholder = $"{TagPrefix}{i}{TagSuffix}";
            result = result.Replace(placeholder, prepared.ControlCodes[i]);
        }

        // Paso 3: Reinsertar saltos de línea en posiciones proporcionales
        if (prepared.Newlines.Count > 0)
        {
            result = ReinsertNewlines(result, prepared.Newlines);
        }

        return result;
    }

    /// <summary>
    /// Elimina todos los control codes y variables de script del texto.
    /// </summary>
    public static string Strip(string input)
    {
        string result = ScriptVarRegex.Replace(input, "");
        return ControlCodeRegex.Replace(result, "").Trim();
    }

    /// <summary>
    /// Comprueba si el texto contiene algún control code o variable de script.
    /// </summary>
    public static bool HasControlCodes(string input)
    {
        return ControlCodeRegex.IsMatch(input) || ScriptVarRegex.IsMatch(input);
    }

    private static string ReinsertNewlines(string text, List<NewlineInfo> newlines)
    {
        if (string.IsNullOrEmpty(text) || newlines.Count == 0)
            return text;

        // Ordenar por posición relativa
        List<NewlineInfo> sorted = newlines.OrderBy(n => n.RelativePosition).ToList();
        int insertedOffset = 0;

        foreach (NewlineInfo? nl in sorted)
        {
            int targetIndex = (int)Math.Round(text.Length * nl.RelativePosition) + insertedOffset;
            targetIndex = Math.Clamp(targetIndex, 0, text.Length);

            // Buscar el espacio más cercano para no cortar palabras
            int bestIndex = FindNearestSpace(text, targetIndex);
            if (bestIndex >= 0 && bestIndex < text.Length)
            {
                // Reemplazar el espacio con el salto de línea original
                text = text[..bestIndex] + nl.Value + text[(bestIndex + 1)..];
                insertedOffset += nl.Value.Length - 1;
            }
        }

        return text;
    }

    private static int FindNearestSpace(string text, int targetIndex)
    {
        if (targetIndex >= text.Length) return text.Length - 1;

        // Buscar hacia adelante y hacia atrás
        int forward = -1, backward = -1;

        for (int i = targetIndex; i < text.Length; i++)
        {
            if (text[i] == ' ') { forward = i; break; }
        }

        for (int i = targetIndex; i >= 0; i--)
        {
            if (text[i] == ' ') { backward = i; break; }
        }

        if (forward < 0 && backward < 0) return -1;
        if (forward < 0) return backward;
        if (backward < 0) return forward;

        return (forward - targetIndex) <= (targetIndex - backward) ? forward : backward;
    }
}
