using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Casazen.Core.Services;

/// <summary>
/// Splits the typed data of a wizard draft into its plain half and its personal half, and puts the two together again (ADR-024-R2).
/// Pure functions on JSON: no database, no encryption (the personal half is encrypted by EF when it is written).
/// </summary>
/// <remarks>
/// <para><b>What goes to the personal half.</b> Every property whose name is in <see cref="WizardDraftPiiFields.PII_FIELD_NAMES"/>, at any
/// depth (a guest inside a list of guests included), with its whole value: <c>address</c> takes the street and the city with it. The plain
/// half keeps everything else, in the same shape (an object that lost all its properties stays as an empty object).</para>
/// <para><b>The personal half mirrors the shape.</b> Objects keep the path to their personal properties; in a list, an element with
/// nothing personal is a <c>null</c> placeholder, so the elements keep their position. <see cref="Merge"/> lays the personal half over the
/// plain one by that shape and returns the data as it was typed (property order aside).</para>
/// </remarks>
public static class WizardDraftPii
{
    // Not the default encoder: it would write every accented letter of an Italian text as \u00XX, and the stored text is read by people
    // and counted in bytes. The JSON is never put in a page.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>True when <paramref name="name"/> names a personal field (<see cref="WizardDraftPiiFields.PII_FIELD_NAMES"/>, any case).</summary>
    public static bool IsPersonalField(string name) => WizardDraftPiiFields.PII_FIELD_NAMES.Contains(name);

    /// <summary>
    /// True when a property name or a text anywhere in <paramref name="element"/> holds the character U+0000, which a <c>jsonb</c> column
    /// cannot store (PostgreSQL refuses it): a draft like that is refused with a 4xx before it reaches the database.
    /// </summary>
    public static bool HasNullCharacter(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains('\0') || HasNullCharacter(property.Value))
                        return true;
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasNullCharacter(item))
                        return true;
                }

                return false;
            case JsonValueKind.String:
                return element.GetString()?.Contains('\0') == true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The plain half (never holds a personal field) and the personal half (<c>null</c> when the data has none) of the data of a draft.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not a JSON object.</exception>
    public static WizardDraftSplit Split(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The data of a draft is a JSON object.", nameof(data));

        var plain = Write(writer => WritePlain(data, writer));
        var personal = ContainsPersonalField(data) ? Write(writer => WritePersonal(data, writer)) : null;
        return new WizardDraftSplit(plain, personal);
    }

    /// <summary>
    /// The data of a draft as it was typed: the plain half with the personal half laid over it. The personal half is optional (<c>null</c> or
    /// empty: the draft has none). The caller disposes the document.
    /// </summary>
    public static JsonDocument Merge(string plainJson, string? personalJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainJson);
        if (string.IsNullOrEmpty(personalJson))
            return JsonDocument.Parse(plainJson);

        using var plain = JsonDocument.Parse(plainJson);
        using var personal = JsonDocument.Parse(personalJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            WriteMerged(plain.RootElement, personal.RootElement, writer);

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>True when a personal field is anywhere in <paramref name="element"/>, at any depth.</summary>
    private static bool ContainsPersonalField(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPersonalField(property.Name) || ContainsPersonalField(property.Value))
                        return true;
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsPersonalField(item))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Writes <paramref name="element"/> without its personal properties.</summary>
    private static void WritePlain(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPersonalField(property.Name))
                        continue;

                    writer.WritePropertyName(property.Name);
                    WritePlain(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WritePlain(item, writer);

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Writes only the personal properties of <paramref name="element"/> (which contains at least one), under the path that leads to them;
    /// in a list, an element with none is a <c>null</c> so that the positions stay.
    /// </summary>
    private static void WritePersonal(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPersonalField(property.Name))
                    {
                        writer.WritePropertyName(property.Name);
                        property.Value.WriteTo(writer);
                    }
                    else if (ContainsPersonalField(property.Value))
                    {
                        writer.WritePropertyName(property.Name);
                        WritePersonal(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsPersonalField(item))
                        WritePersonal(item, writer);
                    else
                        writer.WriteNullValue();
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>Writes <paramref name="plain"/> with <paramref name="personal"/> laid over it; with no personal half, <paramref name="plain"/> as it is.</summary>
    private static void WriteMerged(JsonElement plain, JsonElement? personal, Utf8JsonWriter writer)
    {
        if (personal is not { } over || over.ValueKind == JsonValueKind.Null)
        {
            plain.WriteTo(writer);
            return;
        }

        if (plain.ValueKind == JsonValueKind.Object && over.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in plain.EnumerateObject())
            {
                written.Add(property.Name);
                writer.WritePropertyName(property.Name);
                if (over.TryGetProperty(property.Name, out var match))
                    WriteMerged(property.Value, match, writer);
                else
                    property.Value.WriteTo(writer);
            }

            // The properties that are personal themselves are not in the plain half: they come back from the personal one.
            foreach (var property in over.EnumerateObject())
            {
                if (written.Contains(property.Name))
                    continue;

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
            return;
        }

        if (plain.ValueKind == JsonValueKind.Array && over.ValueKind == JsonValueKind.Array)
        {
            List<JsonElement> personalItems = [.. over.EnumerateArray()];
            writer.WriteStartArray();
            var position = 0;
            foreach (var item in plain.EnumerateArray())
            {
                WriteMerged(item, position < personalItems.Count ? personalItems[position] : null, writer);
                position++;
            }

            writer.WriteEndArray();
            return;
        }

        // The two halves do not have the same shape (they always do when the service wrote them): the plain half is what is shown.
        plain.WriteTo(writer);
    }
}

/// <summary>The two halves of the data of a draft: what is stored in clear, and what is stored encrypted (<c>null</c> when there is no personal field).</summary>
public readonly record struct WizardDraftSplit(string PlainJson, string? PersonalJson);
