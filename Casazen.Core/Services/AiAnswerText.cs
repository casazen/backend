namespace Casazen.Core.Services;

public static class AiAnswerText
{
    private const string ReasoningOpening = "<think>";
    private const string ReasoningClosing = "</think>";

    public static string WithoutReasoning(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var closing = content.LastIndexOf(ReasoningClosing, StringComparison.OrdinalIgnoreCase);
        var text = closing >= 0 ? content[(closing + ReasoningClosing.Length)..] : content;
        var opening = text.IndexOf(ReasoningOpening, StringComparison.OrdinalIgnoreCase);
        return opening >= 0 ? text[..opening] : text;
    }

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
