using System.Text.RegularExpressions;

namespace RpgLingo.Translation;

/// <summary>
/// Handles all RPG Maker escape sequences (colors, icons, variables, names, etc.)
/// by replacing them with unicode placeholders before translation and restoring them afterward.
/// Also handles newlines with proportional repositioning.
/// </summary>
public static partial class ControlCodeHelper
{
    // Pattern capturing all RPG Maker control codes:
    // \C[n], \N[n], \V[n], \I[n], \{, \}, \$, \!, \., \|, \>, \<, \^, etc.
    // In JSON these appear as \\C[n], \\N[n], etc.
    private static readonly Regex ControlCodeRegex = BuildControlCodeRegex();

    // Patterns for script variables and RPG Maker plugin constructs:
    // <tag:value>, <TAG[123]>, $gameVariables[n], _tv[\"name\"], set_npm(8,"x",0), etc.

    private const string TagPrefix = "⟦CC";
    private const string ScriptPrefix = "⟦SV";
    private const string TagSuffix = "⟧";
    private const string NewlinePrefix = "⟦NL";
    private const string NewlineSuffix = "⟧";

    [GeneratedRegex(@"\\{1,2}[A-Za-z$!><\^\|\{\}]\w*(\[[^\]]*\])?")]
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
    /// Prepares text for translation: extracts control codes, script variables
    /// and newlines, replaces them with placeholders, and returns the clean text.
    /// </summary>
    public static PreparedText Prepare(string input)
    {
        string text = input;
        List<string> controlCodes = [];
        List<string> scriptVars = [];
        List<NewlineInfo> newlines = [];

        // Step 1: Replace script variables with placeholders
        int svIndex = 0;
        text = ScriptVarRegex.Replace(text, match =>
        {
            scriptVars.Add(match.Value);
            return $"{ScriptPrefix}{svIndex++}{TagSuffix}";
        });

        // Step 2: Replace control codes with placeholders
        int ccIndex = 0;
        text = ControlCodeRegex.Replace(text, match =>
        {
            controlCodes.Add(match.Value);
            return $"{TagPrefix}{ccIndex++}{TagSuffix}";
        });

        // Step 3: Extract newlines with relative positions
        int nlIndex = 0;
        int textLengthWithoutNewlines = text.Replace("\\n", "").Replace("\n", "").Length;

        if (textLengthWithoutNewlines > 0)
        {
            // Handle literal \n (as it appears in RPG Maker JSON)
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
                return " "; // Replace with space for translation
            });
        }

        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        return new PreparedText(text, controlCodes, scriptVars, newlines, input);
    }

    /// <summary>
    /// Restores control codes and newlines in the translated text.
    /// Newlines are reinserted at proportionally equivalent positions.
    /// </summary>
    public static string Restore(string translated, PreparedText prepared)
    {
        string result = translated;

        // Step 1: Restore script variables
        for (int i = 0; i < prepared.ScriptVars.Count; i++)
        {
            string placeholder = $"{ScriptPrefix}{i}{TagSuffix}";
            result = result.Replace(placeholder, prepared.ScriptVars[i]);
        }

        // Step 2: Restore control codes
        for (int i = 0; i < prepared.ControlCodes.Count; i++)
        {
            string placeholder = $"{TagPrefix}{i}{TagSuffix}";
            result = result.Replace(placeholder, prepared.ControlCodes[i]);
        }

        // Step 3: Reinsert newlines at proportional positions
        if (prepared.Newlines.Count > 0)
        {
            result = ReinsertNewlines(result, prepared.Newlines);
        }

        return result;
    }

    /// <summary>
    /// Strips all control codes and script variables from text.
    /// </summary>
    public static string Strip(string input)
    {
        string result = ScriptVarRegex.Replace(input, "");
        return ControlCodeRegex.Replace(result, "").Trim();
    }

    /// <summary>
    /// Checks whether the text contains any control codes or script variables.
    /// </summary>
    public static bool HasControlCodes(string input)
    {
        return ControlCodeRegex.IsMatch(input) || ScriptVarRegex.IsMatch(input);
    }

    private static string ReinsertNewlines(string text, List<NewlineInfo> newlines)
    {
        if (string.IsNullOrEmpty(text) || newlines.Count == 0)
            return text;

        // Sort by relative position
        List<NewlineInfo> sorted = newlines.OrderBy(n => n.RelativePosition).ToList();
        int insertedOffset = 0;

        foreach (NewlineInfo? nl in sorted)
        {
            int targetIndex = (int)Math.Round(text.Length * nl.RelativePosition) + insertedOffset;
            targetIndex = Math.Clamp(targetIndex, 0, text.Length);

            // Find nearest space to avoid splitting words
            int bestIndex = FindNearestSpace(text, targetIndex);
            if (bestIndex >= 0 && bestIndex < text.Length)
            {
                // Replace the space with the original newline
                text = text[..bestIndex] + nl.Value + text[(bestIndex + 1)..];
                insertedOffset += nl.Value.Length - 1;
            }
        }

        return text;
    }

    private static int FindNearestSpace(string text, int targetIndex)
    {
        if (targetIndex >= text.Length) return text.Length - 1;

        // Search forward and backward
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
