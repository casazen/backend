using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The read of the license of an accommodation (E09/F02/US02/T01, NF-56, D14, D21, S25) in <see cref="IOctorateApiClient"/>: one GET of the card of the
/// accommodation and the <c>license</c> object of it. No method of the client writes the license: it is read-only for the partner, and a write
/// answers 400 «read only mode» (S25). The same HTTPS client, 30 s timeout, tenant isolation, rate-limit gate, backoff and typed errors as the rest.
/// </summary>
/// <remarks>
/// The card holds a lot more than the license (the address, the fiscal data, the police account): only the <c>license</c> object is read, and nothing of
/// the answer is ever logged (the log lines of <see cref="OctorateApiClient"/> carry the operation, the org, the status and the time).
/// </remarks>
public sealed partial class OctorateApiClient
{
    /// <summary>The whole card of the accommodation: the short one has no <c>license</c> (S25).</summary>
    private const string LicenseCardFields = "all";

    /// <summary>The longest plan name kept: anything longer is not the name of a plan of the channel manager (<c>FREE</c>, <c>PREMIUM</c>...).</summary>
    private const int MaxLicenseTierLength = 40;

    /// <summary>The earliest day taken for the expiry of a license: a smaller number is a count, not a date, and is never read as one.</summary>
    private static readonly DateOnly EarliestLicenseExpiry = new(2000, 1, 1);

    public async Task<OctorateLicense> GetAccommodationLicenseAsync(string accommodationCode, CancellationToken ct)
    {
        var id = CheckedAccommodationId(accommodationCode);
        using var document = await GetJsonAsync(
            $"/rest/v1/accommodation/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(LicenseCardFields)}", "license", ct);

        // An answer with no license object is one CasaZen cannot read, not a license with nothing in it.
        return ReadLicense(Unwrap(document.RootElement)) ?? throw new OctorateServerException(null);
    }

    /// <summary>
    /// The license of an accommodation of an answer, or <c>null</c> when the answer is not an accommodation or has no <c>license</c> object. The plan is
    /// <c>channelManager</c>; a module is in the result only when the card states its flag (a JSON boolean, a <c>"true"</c> text or a number): a flag
    /// it does not send is absent, never «off». The room limit is the number <c>roomsLimit</c> (never negative) and the expiry the day of
    /// <c>expireDay</c>; either is <c>null</c> when it is missing or is not what it should be: never a guess.
    /// </summary>
    internal static OctorateLicense? ReadLicense(JsonElement accommodation)
    {
        if (accommodation.ValueKind != JsonValueKind.Object
            || !accommodation.TryGetProperty("license", out var license)
            || license.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var modules = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var code in MarketplaceLicensedFunctions.Codes)
        {
            if (FlagOf(license, code) is { } on)
                modules[code] = on;
        }

        var tier = OctorateHttp.StringOf(license, "channelManager");
        var roomsLimit = IntOf(license, "roomsLimit");
        return new OctorateLicense(
            tier is { Length: <= MaxLicenseTierLength } ? tier : null,
            modules,
            roomsLimit is >= 0 ? roomsLimit : null,
            LicenseExpiryOf(license));
    }

    /// <summary>The day the license expires (<c>expireDay</c>, an ISO date or a moment), or <c>null</c> when it is missing or is not a plausible date.</summary>
    private static DateOnly? LicenseExpiryOf(JsonElement license)
    {
        if (DateTimeOf(license, "expireDay") is not { } moment)
            return null;

        var day = DateOnly.FromDateTime(moment);
        return day >= EarliestLicenseExpiry ? day : null;
    }
}
