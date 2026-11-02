namespace Casazen.Core.Services;

/// <summary>
/// Reads what a model sent back when the prompt asked for one JSON object: the text after a reasoning (<c>&lt;think&gt;…&lt;/think&gt;</c>), and the
/// objects in it, the last one first (the ones before it are drafts of the answer). The answer may be wrapped in a code fence or in a sentence; what
/// is inside the object is for the caller to read and check.
/// </summary>
public static class AiAnswerText
{
    private const string ReasoningOpening = "<think>";
    private const string ReasoningClosing = "</think>";

    /// <summary>
    /// What follows the last closing tag of a reasoning, without a reasoning that was opened and never closed: the text of a reasoning is not an
    /// answer, even when it holds an object.
    /// </summary>
    public static string WithoutReasoning(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var closing = content.LastIndexOf(ReasoningClosing, StringComparison.OrdinalIgnoreCase);
        var text = closing >= 0 ? content[(closing + ReasoningClosing.Length)..] : content;
        var opening = text.IndexOf(ReasoningOpening, StringComparison.OrdinalIgnoreCase);
        return opening >= 0 ? text[..opening] : text;
    }

    /// <summary>
    /// The JSON objects of the answer as text, the last one first, braces inside strings left out of the count; an object that never closes is
    /// skipped. Nothing when the answer is empty or holds no object.
    /// </summary>
    public static IEnumerable<string> JsonObjectsLastFirst(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            yield break;

        var text = WithoutReasoning(content);
        for (var start = text.LastIndexOf('{'); start >= 0; start = start == 0 ? -1 : text.LastIndexOf('{', start - 1))
        {
            var end = EndOfObject(text, start);
            if (end >= 0)
                yield return text[start..(end + 1)];
        }
    }

    /// <summary>The index of the brace that closes the object opened at <paramref name="start"/>, braces inside strings left out; -1 when it never closes.</summary>
    private static int EndOfObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
