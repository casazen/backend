using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;

namespace Casazen.Core.OfficialData;

/// <summary>
/// Rejects an extract that is missing the act identity or the downloaded PDF URL: no amounts are written in that case.
/// Extra JSON properties are ignored; missing required fields fail.
/// </summary>
public static partial class ComuneOfficialExtractValidator
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    public sealed record Result(bool Accepted, string? Rejection, ComuneOfficialExtractDto? Extract);

    public static Result Validate(string? json, string downloadedPdfUrl)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Result(false, "json_missing", null);

        ComuneOfficialExtractDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ComuneOfficialExtractDto>(StripFence(json), JsonOptions);
        }
        catch (JsonException)
        {
            return new Result(false, "json_invalid", null);
        }

        if (dto is null)
            return new Result(false, "json_invalid", null);

        if (dto.LegalValue)
            return new Result(false, "legal_value_not_false", dto);

        if (string.IsNullOrWhiteSpace(dto.ActId) || !ActNumberRegex().IsMatch(dto.ActId))
            return new Result(false, "act_id_missing", dto);

        if (!IsHttps(dto.SourceUrl) || !SameUrl(dto.SourceUrl, downloadedPdfUrl))
            return new Result(false, "source_url_mismatch", dto);

        if (string.Equals(dto.Status, ComuneOfficialExtractStatus.Unreadable, StringComparison.OrdinalIgnoreCase))
            return new Result(true, null, dto);

        if (!string.Equals(dto.Status, ComuneOfficialExtractStatus.Extracted, StringComparison.OrdinalIgnoreCase))
            return new Result(false, "status_unknown", dto);

        if (string.IsNullOrWhiteSpace(dto.MefPublishedOn)
            || !DateOnly.TryParseExact(dto.MefPublishedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new Result(false, "mef_published_on_missing", dto);

        if (dto.Rates.Count == 0)
            return new Result(false, "rates_empty", dto);

        foreach (var rate in dto.Rates)
        {
            var method = rate.CalculationMethod ?? ComuneOfficialCalculationMethods.PerPersonPerNight;
            if (string.Equals(method, ComuneOfficialCalculationMethods.PercentOfNightlyPrice, StringComparison.OrdinalIgnoreCase))
            {
                if (rate.PercentOfNightlyPrice is null or <= 0)
                    return new Result(false, "percent_missing", dto);
            }
            else if (rate.RatePerPersonPerNight is null || rate.RatePerPersonPerNight < 0)
            {
                return new Result(false, "amount_invalid", dto);
            }

            if (rate.RatePerPersonPerNight < 0 || rate.PercentOfNightlyPrice < 0 || rate.CapPerPersonPerNight < 0
                || rate.ReducedRatePerPersonPerNight < 0)
                return new Result(false, "amount_negative", dto);

            if (rate.MinimumAge is { } age && (age < 0 || age > 18))
                return new Result(false, "minimum_age_out_of_range", dto);

            if (rate.MaxNights is { } nights && nights < 1)
                return new Result(false, "max_nights_invalid", dto);
        }

        return new Result(true, null, dto);
    }

    public static bool SameUrl(string left, string right)
    {
        if (!Uri.TryCreate(left.Trim(), UriKind.Absolute, out var a)
            || !Uri.TryCreate(right.Trim(), UriKind.Absolute, out var b))
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

        var pathA = a.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var pathB = b.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string StripFence(string json)
    {
        var trimmed = json.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;
        var firstNl = trimmed.IndexOf('\n');
        if (firstNl < 0)
            return trimmed;
        var body = trimmed[(firstNl + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return fence >= 0 ? body[..fence].Trim() : body.Trim();
    }

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ActNumberRegex();
}
