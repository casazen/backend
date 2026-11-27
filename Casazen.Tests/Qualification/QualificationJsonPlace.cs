using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Casazen.Tests.Qualification;

// E74/F01/US01/T02 (NF-198, D12): where a JSON file could not be read, said without a word the author typed. The path the parser reports holds the NAME of a member
// it does not know ($.persons[0].<name>) and the author's own spelling of one it does know: a name is text the author typed (a typo, or worse, a person), so it must
// not reach a message. The place that leaves the toolkit is built from the members the reader KNOWS, written as the model writes them, and from indexes; any other
// name is written "?". Nothing of the parser's text is ever copied: what is written is a known name, "?", an index read as a number and the punctuation of a path.

/// <summary>The place of a JSON reading failure (a path and a line) without anything the author typed.</summary>
internal static class QualificationJsonPlace
{
    private const string UnknownName = "?";

    /// <summary>The names of the public members of <paramref name="types"/> as the JSON writes them (camel case), found without regard to letter case.</summary>
    public static IReadOnlyDictionary<string, string> MemberNames(params Type[] types)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                names[name] = name;
            }
        }

        return names;
    }

    /// <summary>
    /// The path of a failure as the parser reports it (<c>$.persons[0].id</c>, <c>$.persons[0]['a name']</c>) with every member name that is not in
    /// <paramref name="knownMembers"/> written <c>?</c> and every known one written as the model writes it. The result is built from known names, <c>?</c> and
    /// indexes read as numbers; whatever else the parser put in the path is dropped, whatever it looks like.
    /// </summary>
    public static string SafePath(string? path, IReadOnlyDictionary<string, string> knownMembers)
    {
        var safe = new StringBuilder("$");
        var text = path ?? string.Empty;
        var at = text.StartsWith('$') ? 1 : 0;
        while (at < text.Length)
        {
            if (text[at] == '.')
            {
                // A plain name runs to the next separator.
                var length = text.AsSpan(at + 1).IndexOfAny('.', '[');
                var end = length < 0 ? text.Length : at + 1 + length;
                safe.Append('.').Append(knownMembers.GetValueOrDefault(text[(at + 1)..end], UnknownName));
                at = end;
            }
            else if (text[at] == '[' && at + 1 < text.Length && text[at + 1] == '\'')
            {
                // A name with special characters is written ['...'], never escaped: it ends at the first '] and is never a known one.
                var close = text.IndexOf("']", at + 2, StringComparison.Ordinal);
                safe.Append('.').Append(UnknownName);
                at = close < 0 ? text.Length : close + 2;
            }
            else if (text[at] == '[' && TryReadIndex(text, at, out var index, out var next))
            {
                safe.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                at = next;
            }
            else
            {
                at++;
            }
        }

        return safe.ToString();
    }

    /// <summary>The place as it goes in a message: <c>at $.persons[0].id, line 3</c> (the line counts from 1, and is <c>unknown</c> when the parser does not know it).</summary>
    public static string Describe(string safePath, long? line)
    {
        var where = line is { } number ? number.ToString(CultureInfo.InvariantCulture) : "unknown";
        return "at " + safePath + ", line " + where;
    }

    // [12]: the digits between the brackets, as a number (so that only a real index is ever written).
    private static bool TryReadIndex(string text, int open, out int index, out int next)
    {
        index = 0;
        next = open;
        var close = text.IndexOf(']', open + 1);
        if (close < 0 || !int.TryParse(text.AsSpan(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;

        next = close + 1;
        return true;
    }
}
