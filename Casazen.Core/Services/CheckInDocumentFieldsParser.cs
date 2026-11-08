using System.Globalization;
using System.Text.Json;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// Turns the JSON answer of the document reader into fields. Values that are missing or implausible are dropped;
/// the raw text of the document is never kept.
/// </summary>
public static class CheckInDocumentFieldsParser
{
    public static CheckInDocumentFields? Parse(string? content, DateOnly today)
    {
        var json = ExtractObject(content);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var fields = new CheckInDocumentFields(
                Text(root, "firstName", 30),
                Text(root, "lastName", 50),
                Gender(root),
                Date(root, "dateOfBirth", today, past: true),
                Born(root),
                Text(root, "birthPlace", 100),
                Province(root),
                Text(root, "citizenship", 100),
                DocumentKind(root),
                DocumentNumber(root),
                Text(root, "issuePlace", 100));

            var any = fields.FirstName is not null
                || fields.LastName is not null
                || fields.Gender is not null
                || fields.DateOfBirth is not null
                || fields.BornInItaly is not null
                || fields.BirthPlace is not null
                || fields.BirthProvince is not null
                || fields.Citizenship is not null
                || fields.DocumentKind is not null
                || fields.DocumentNumber is not null
                || fields.IssuePlace is not null;
            return any ? fields : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first JSON object in the answer, without a markdown fence around it.</summary>
    public static string? ExtractObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }

    private static string? Text(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = string.Join(' ', value.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (text.Length == 0 || text.Length > max)
            return null;
        if (text.Any(c => char.IsDigit(c)))
            return null;
        return text;
    }

    private static string? Gender(JsonElement root)
    {
        var text = ReadString(root, "gender");
        if (text is null)
            return null;

        var key = text.Trim().ToUpperInvariant();
        return key is "M" or "MALE" or "MASCHIO" or "1" ? "Male"
            : key is "F" or "FEMALE" or "FEMMINA" or "2" ? "Female"
            : null;
    }

    private static string? Date(JsonElement root, string name, DateOnly today, bool past)
    {
        var text = ReadString(root, name);
        if (text is null || !DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        if (past && date > today)
            return null;
        if (date.Year < 1900)
            return null;
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool? Born(JsonElement root)
    {
        if (!root.TryGetProperty("bornInItaly", out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? Province(JsonElement root)
    {
        var text = ReadString(root, "birthProvince");
        if (text is null)
            return null;
        var code = text.Trim().ToUpperInvariant();
        return AlloggiatiRecordRules.IsValidProvince(code) ? code : null;
    }

    private static string? DocumentKind(JsonElement root)
    {
        var text = ReadString(root, "documentKind");
        return text?.Trim().ToUpperInvariant() switch
        {
            "PASSPORT" or "PASSAPORTO" => "Passport",
            "IDENTITYCARD" or "CARTA DI IDENTITA" or "CARTA IDENTITA" => "IdentityCard",
            "DRIVERSLICENSE" or "PATENTE" => "DriversLicense",
            _ => null,
        };
    }

    private static string? DocumentNumber(JsonElement root)
    {
        var text = ReadString(root, "documentNumber");
        if (text is null)
            return null;
        var number = AlloggiatiRecordRules.NormalizeDocumentNumber(text);
        return AlloggiatiRecordRules.IsValidDocumentNumber(number) ? number : null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
