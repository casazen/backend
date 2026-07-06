using System.Security.Cryptography;
using System.Text;
using Casazen.Infrastructure.ICalSpike;

namespace Casazen.Infrastructure.Services;

public sealed record ParsedCalendarBlock(
    string ExternalUid,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Summary);

/// <summary>
/// RFC 5545 import for property OTA calendar sync (#294).
/// </summary>
public class ICalImportService
{
    public IReadOnlyList<ParsedCalendarBlock> Parse(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return [];

        try
        {
            var slices = ICalImportSpike.ParseImport(icsContent);
            return slices
                .Select(s => new ParsedCalendarBlock(
                    ResolveExternalUid(s.ExternalUid, s.StartUtc, s.EndUtc, s.Summary),
                    s.StartUtc,
                    s.EndUtc,
                    s.Summary))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static string ResolveExternalUid(string? uid, DateTime startUtc, DateTime endUtc, string? summary)
    {
        if (!string.IsNullOrWhiteSpace(uid))
            return uid.Trim();

        var input = $"{startUtc:O}|{endUtc:O}|{summary ?? string.Empty}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
