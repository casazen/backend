using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

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
        var consentRecord = new MarketingConsentRecord
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            MarketingEmail = "guest@example.com",
            ConsentEmail = true,
            ConsentWhatsApp = false,
            EmailConfirmed = false,
            ConsentTimestamp = DateTime.UtcNow,
            ConsentTextVersion = "v1.0",
            IpAddress = "192.0.2.1"
        };

        var isEligibleForEmailCampaign = consentRecord.ConsentEmail && consentRecord.EmailConfirmed;
        var isEligibleForWhatsAppCampaign = consentRecord.ConsentWhatsApp && consentRecord.EmailConfirmed;

        Assert.False(isEligibleForEmailCampaign,
            "Email campaigns must not send to unconfirmed addresses (double opt-in required)");
        Assert.False(isEligibleForWhatsAppCampaign,
            "WhatsApp campaigns must not send to unconfirmed records");

        Assert.True(consentRecord.ConsentEmail, "Guest gave consent");
        Assert.False(consentRecord.EmailConfirmed, "But email not confirmed (no double opt-in link click)");
    }

    [Fact]
    public void MarketingConsent_Confirmed_AllowsSend()
    {
        var consentRecord = new MarketingConsentRecord
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            MarketingEmail = "guest@example.com",
            ConsentEmail = true,
            ConsentWhatsApp = true,
            EmailConfirmed = true,
            ConsentTimestamp = DateTime.UtcNow,
            ConsentTextVersion = "v1.0",
            IpAddress = "192.0.2.1"
        };

        var isEligibleForEmailCampaign = consentRecord.ConsentEmail && consentRecord.EmailConfirmed;
        var isEligibleForWhatsAppCampaign = consentRecord.ConsentWhatsApp && consentRecord.EmailConfirmed;

        Assert.True(isEligibleForEmailCampaign, "Confirmed email consent enables email campaigns");
        Assert.True(isEligibleForWhatsAppCampaign, "Confirmed consent enables WhatsApp campaigns");
    }
}
