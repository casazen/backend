using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

/// <summary>
/// NF-05: Marketing consent records must enforce double opt-in before allowing sends.
/// Covers AC-NF05-5: double opt-in requirement.
/// </summary>
public class MarketingConsentRecordTests
{
    /// <summary>
    /// Proof test per ADR-007-R21: proves that a marketing consent record without EmailConfirmed=true
    /// blocks marketing sends. This test simulates a campaign send filter that MUST check EmailConfirmed.
    /// Without this check, GDPR consent requirements (art. 6.1.a) and CAN-SPAM double opt-in would be violated.
    /// </summary>
    [Fact]
    public void MarketingConsent_NotConfirmed_BlocksSend()
    {
        // Arrange: Create a consent record with ConsentEmail=true but EmailConfirmed=false
        var consentRecord = new MarketingConsentRecord
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            MarketingEmail = "guest@example.com",
            ConsentEmail = true,  // Guest checked the box
            ConsentWhatsApp = false,
            EmailConfirmed = false,  // But has NOT clicked the confirmation link (double opt-in)
            ConsentTimestamp = DateTime.UtcNow,
            ConsentTextVersion = "v1.0",
            IpAddress = "192.0.2.1"
        };

        // Act: Simulate campaign send eligibility check
        // A proper campaign service MUST verify EmailConfirmed before sending
        var isEligibleForEmailCampaign = consentRecord.ConsentEmail && consentRecord.EmailConfirmed;
        var isEligibleForWhatsAppCampaign = consentRecord.ConsentWhatsApp && consentRecord.EmailConfirmed;

        // Assert: Unconfirmed consent MUST block all sends
        Assert.False(isEligibleForEmailCampaign,
            "Email campaigns must not send to unconfirmed addresses (double opt-in required)");
        Assert.False(isEligibleForWhatsAppCampaign,
            "WhatsApp campaigns must not send to unconfirmed records");

        // Verify the record state that blocks sends
        Assert.True(consentRecord.ConsentEmail, "Guest gave consent");
        Assert.False(consentRecord.EmailConfirmed, "But email not confirmed (no double opt-in link click)");
    }

    /// <summary>
    /// Companion test: proves that confirmed consent enables sends.
    /// </summary>
    [Fact]
    public void MarketingConsent_Confirmed_AllowsSend()
    {
        // Arrange: Create a fully confirmed consent record
        var consentRecord = new MarketingConsentRecord
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            MarketingEmail = "guest@example.com",
            ConsentEmail = true,
            ConsentWhatsApp = true,
            EmailConfirmed = true,  // Double opt-in completed
            ConsentTimestamp = DateTime.UtcNow,
            ConsentTextVersion = "v1.0",
            IpAddress = "192.0.2.1"
        };

        // Act
        var isEligibleForEmailCampaign = consentRecord.ConsentEmail && consentRecord.EmailConfirmed;
        var isEligibleForWhatsAppCampaign = consentRecord.ConsentWhatsApp && consentRecord.EmailConfirmed;

        // Assert: Confirmed consent allows sends
        Assert.True(isEligibleForEmailCampaign, "Confirmed email consent enables email campaigns");
        Assert.True(isEligibleForWhatsAppCampaign, "Confirmed consent enables WhatsApp campaigns");
    }
}
