using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services.CfSimulator;

/// <summary>The outcome of verifying a link to the simulated module: good (with the lease it names) or not good, and why.</summary>
public sealed record CfSimulatorLinkCheck(bool Valid, string? Reason, Guid? LeaseId);

/// <summary>
/// Verifies the link to the module the way ConcordatoFacile says it does (specification v0.2 §2), for the simulated module (CF-SIM). It is
/// written from that text and shares no code with the signing of <see cref="ConcordatoFacileLinkService"/>, so that a mistake in the
/// signing is not repeated here and shows up as a link the simulator refuses:
/// <list type="bullet">
/// <item><c>partner</c> is <c>casazen</c>;</item>
/// <item><c>signature</c> is the hexadecimal HMAC-SHA256, with the link secret as the UTF-8 key, of <c>leaseId + "." + expiresAt</c>,
/// verified on the text exactly as it arrived, before any normalisation; upper or lower case is accepted for the GUID and the signature;</item>
/// <item><c>expiresAt</c> is ISO 8601 UTC with the <c>T</c> and the <c>Z</c> in capitals, to the second or with one to seven decimals; an
/// offset (<c>+00:00</c>), a space for the <c>T</c> or a lowercase <c>t</c>/<c>z</c> are refused;</item>
/// <item>a link already expired is refused with 2 minutes of margin for the clocks, one that expires more than 60 minutes after the
/// moment it is opened too.</item>
/// </list>
/// </summary>
public static partial class CfSimulatorLinkVerifier
{
    /// <summary>The only partner of the module.</summary>
    public const string Partner = "casazen";

    /// <summary>How long after its <c>expiresAt</c> a link is still accepted, for the clocks.</summary>
    public static readonly TimeSpan ClockMargin = TimeSpan.FromMinutes(2);

    /// <summary>How far from the moment it is opened a link may expire.</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromMinutes(60);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex ExpiresAtShape();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex SignatureShape();

    public static CfSimulatorLinkCheck Verify(CfSimulatorLinkParameters link, string? secret, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (string.IsNullOrWhiteSpace(link.Partner) || string.IsNullOrWhiteSpace(link.LeaseId)
            || string.IsNullOrWhiteSpace(link.ExpiresAt) || string.IsNullOrWhiteSpace(link.Signature))
        {
            return Refused("missing_parameters");
        }

        if (!string.Equals(link.Partner, Partner, StringComparison.Ordinal))
            return Refused("partner_invalid");

        if (link.LeaseId.Length != 36 || !Guid.TryParseExact(link.LeaseId, "D", out var leaseId))
            return Refused("lease_id_invalid");

        if (!ExpiresAtShape().IsMatch(link.ExpiresAt)
            || !DateTimeOffset.TryParse(link.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
        {
            return Refused("expires_at_invalid");
        }

        if (!SignatureShape().IsMatch(link.Signature))
            return Refused("signature_invalid");

        if (string.IsNullOrWhiteSpace(secret))
            return Refused("secret_not_configured");

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{link.LeaseId}.{link.ExpiresAt}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(link.Signature)))
            return Refused("signature_invalid");

        if (now > expiresAt + ClockMargin)
            return Refused("expired");

        if (expiresAt > now + MaxValidity)
            return Refused("too_far");

        return new CfSimulatorLinkCheck(true, null, leaseId);
    }

    private static CfSimulatorLinkCheck Refused(string reason) => new(false, reason, null);
}
