using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// How the configuration files of the self-hosted identity verification (the decision policy and the formats of the documents, E70/F01/US02) are read:
/// strictly, and, for the policy, through the <b>canonical form</b> its pinned hash is computed on (ADR-013-R18). Local files only: nothing here reaches a network.
/// <para>
/// <b>Strict.</b> A file is refused, never repaired: comments, trailing commas, a repeated member, a member the record does not have, a member the record
/// needs and the file lacks, a <c>null</c> for a member of a record and a number written as a string are all errors. A member that is spelled wrong
/// therefore cannot go unnoticed, and what the code reads is exactly what the hash covers. <b>What the reader does not refuse is a <c>null</c> INSIDE a list or a
/// dictionary</b> (System.Text.Json does not check the items of a collection against their nullability): the validator of each loader refuses it
/// (<c>format_layout_invalid</c> for the layout of a format, <c>policy_invalid</c> for the policy), and the registry skips a file that makes its reader or its
/// validator throw with <c>format_json_invalid</c>, so one broken file never hides the other formats.
/// </para>
/// <para>
/// <b>Canonical form</b> (in the spirit of RFC 8785): the members of every object sorted by name in ordinal order, the items of an array in their order, no
/// white space at all, a string with only the quote, the backslash and the control characters escaped (the short forms <c>\b \t \n \f \r</c>, the others as
/// <c>\u00xx</c>), a number written as the shortest decimal that reads back to the same value in the invariant culture (an integer as it is, <c>0.70</c> and
/// <c>0.7</c> alike, <c>-0</c> as <c>0</c>), then UTF-8 without a byte order mark. The line endings and the indentation of a checkout, the order of the
/// members and the way a number is written therefore never change the hash: a change of a value, of a name or of a word does.
/// </para>
/// </summary>
internal static class IdentityConfigurationJson
{
    /// <summary>The deepest nesting a file may have: the files are a few levels deep, anything else is not one of ours.</summary>
    private const int MaxDepth = 16;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>The options every file of the identity configuration is deserialized with (see the remarks of the class).</summary>
    internal static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxDepth,
    };

    /// <summary>
    /// The canonical form of a JSON file. False, with an empty text, when it is not JSON in the strict sense above (a syntax error, comments, a repeated
    /// member, a number out of range, a lone surrogate): it never throws.
    /// </summary>
    internal static bool TryCanonicalize(ReadOnlyMemory<byte> utf8Json, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            if (utf8Json.Span.StartsWith(Utf8Bom))
                utf8Json = utf8Json[Utf8Bom.Length..];

            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = MaxDepth });
            var builder = new StringBuilder(utf8Json.Length);
            Append(builder, document.RootElement);
            canonical = builder.ToString();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The SHA-256 of a canonical text as UTF-8, lower case hexadecimal (64 characters).</summary>
    internal static string Sha256Hex(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text)));
    }

    /// <summary>Reads a canonical text (or any strict JSON text) into <typeparamref name="T"/> with <see cref="Strict"/>; a <see cref="JsonException"/> names what is wrong.</summary>
    internal static T? ReadStrict<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Strict);
    }

    private static void Append(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AppendObject(builder, element);
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                        builder.Append(',');

                    first = false;
                    Append(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(builder, element.GetString() ?? throw new FormatException("A string is not valid text."));
                break;
            case JsonValueKind.Number:
                builder.Append(NormalizeNumber(element));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new FormatException("A value of an unknown kind.");
        }
    }

    private static void AppendObject(StringBuilder builder, JsonElement element)
    {
        var members = element.EnumerateObject().Select(member => (member.Name, member.Value)).OrderBy(member => member.Name, StringComparer.Ordinal).ToList();
        builder.Append('{');
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                // The members are sorted, so a repeated name is next to its twin: which of the two values is meant cannot be told, the file is refused.
                if (string.Equals(members[i].Name, members[i - 1].Name, StringComparison.Ordinal))
                    throw new FormatException("A member is repeated.");

                builder.Append(',');
            }

            AppendString(builder, members[i].Name);
            builder.Append(':');
            Append(builder, members[i].Value);
        }

        builder.Append('}');
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    private static string NormalizeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);

        if (!element.TryGetDouble(out var real) || double.IsNaN(real) || double.IsInfinity(real))
            throw new FormatException("A number is out of range.");

        return real == 0 ? "0" : real.ToString("R", CultureInfo.InvariantCulture);
    }
}
