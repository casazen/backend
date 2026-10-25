using Casazen.Core.Services;
using Casazen.Infrastructure.Services.CfSimulator;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-SIM: the simulated module verifies the signed link the way ConcordatoFacile says it does (specification v0.2 §2), on the test vector of
/// that specification, so a link signed by CasaZen's own code is accepted exactly when the real module would accept it: it is also how a
/// mistake in the signing would show up before the real module is tried. A link that is not good must never bind a practice to a contract.
/// </summary>
public sealed class CfSimulatorLinkVerifierTests
{
    // The test vector of the specification (§2) and of ConcordatoFacileLinkServiceTests.
    private const string Secret = "test_link_secret_0123456789abcdef0123456789abcdef";
    private const string LeaseId = "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11";
    private const string ExpiresAt = "2026-10-13T10:00:00Z";
    private const string Signature = "fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea";

    private static CfSimulatorLinkParameters Link(
        string? partner = "casazen",
        string? leaseId = LeaseId,
        string? expiresAt = ExpiresAt,
        string? signature = Signature) => new(partner, leaseId, expiresAt, signature, "Monza", "MB");

    private static CfSimulatorLinkCheck Verify(CfSimulatorLinkParameters link, string openedAt = "2026-10-13T09:50:00Z") =>
        CfSimulatorLinkVerifier.Verify(link, Secret, DateTimeOffset.Parse(openedAt, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Verify_TheVectorOfTheSpecification_IsGoodAndNamesTheLease()
    {
        var check = Verify(Link());

        Assert.True(check.Valid);
        Assert.Equal(Guid.Parse(LeaseId), check.LeaseId);
    }

    [Theory]
    [InlineData("2026-10-13T10:01:59Z", true, null)] // after the expiry, within the 2 minutes of margin
    [InlineData("2026-10-13T10:02:01Z", false, "expired")]
    [InlineData("2026-10-13T09:00:00Z", true, null)] // it expires exactly 60 minutes after it is opened
    [InlineData("2026-10-13T08:59:00Z", false, "too_far")] // 61 minutes
    public void Verify_TheExpiry_HasTwoMinutesOfMarginAndSixtyMinutesAhead(string openedAt, bool valid, string? reason)
    {
        var check = Verify(Link(), openedAt);

        Assert.Equal(valid, check.Valid);
        Assert.Equal(reason, check.Reason);
        Assert.Equal(valid, check.LeaseId is not null);
    }

    [Theory]
    [InlineData("casazen", LeaseId, ExpiresAt, "fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eeb", "signature_invalid")] // one digit changed
    [InlineData("casazen", "1b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11", ExpiresAt, Signature, "signature_invalid")] // another lease, same signature
    [InlineData("casazen", LeaseId, "2026-10-13T10:00:01Z", Signature, "signature_invalid")] // another expiry, same signature
    [InlineData("other", LeaseId, ExpiresAt, Signature, "partner_invalid")]
    [InlineData("casazen", LeaseId, "2026-10-13T10:00:00+00:00", Signature, "expires_at_invalid")] // an offset instead of the Z
    [InlineData("casazen", LeaseId, "2026-10-13 10:00:00Z", Signature, "expires_at_invalid")] // a space instead of the T
    [InlineData("casazen", LeaseId, "2026-10-13t10:00:00z", Signature, "expires_at_invalid")] // lowercase t and z
    [InlineData("casazen", "not-a-guid", ExpiresAt, Signature, "lease_id_invalid")]
    [InlineData("casazen", LeaseId, ExpiresAt, "abc", "signature_invalid")]
    [InlineData("casazen", LeaseId, ExpiresAt, null, "missing_parameters")]
    public void Verify_ALinkThatIsNotGood_IsRefusedAndNamesNoLease(
        string partner, string leaseId, string expiresAt, string? signature, string reason)
    {
        var check = Verify(Link(partner, leaseId, expiresAt, signature));

        Assert.False(check.Valid);
        Assert.Equal(reason, check.Reason);
        Assert.Null(check.LeaseId);
    }

    [Fact]
    public void Verify_WithoutALinkSecret_RefusesEveryLink()
    {
        var check = CfSimulatorLinkVerifier.Verify(Link(), null, DateTimeOffset.Parse("2026-10-13T09:50:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.False(check.Valid);
        Assert.Equal("secret_not_configured", check.Reason);
    }
}
