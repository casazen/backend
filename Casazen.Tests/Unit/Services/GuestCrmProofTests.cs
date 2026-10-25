using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof tests for CRM guest matching (ADR-021): proves money / data / permission / privacy rules.
/// </summary>
public class GuestCrmProofTests
{
    /// <summary>
    /// ADR-021-R5: GuestCrmProfile MUST exclude Alloggiati submission data (document type, number, issue place).
    /// Proves that the CRM profile entity has no fields for personal identification documents,
    /// preventing compliance data from entering marketing channels.
    /// </summary>
    [Fact]
    public void GuestCrm_NoAlloggiatiData()
    {
        // Arrange: create a CRM profile
        var profile = new GuestCrmProfile
        {
            OrgId = Guid.NewGuid(),
            CompositeKeyHash = "test-hash",
            ConfirmedMarketingEmail = "test@example.com",
            MarketingConsentEmail = true,
            MarketingConsentWhatsApp = false,
            Language = "it",
            Country = "IT",
            TotalStays = 5,
            TotalSpend = 1500.00m,
            PropertyIdsJson = "[]",
            TagsJson = "[]",
            FirstStayAt = DateTime.UtcNow.AddMonths(-6),
            LastStayAt = DateTime.UtcNow,
        };

        // Assert: GuestCrmProfile type does NOT have Alloggiati fields
        var profileType = typeof(GuestCrmProfile);

        // ADR-021-R5: No document type (Alloggiati requirement)
        Assert.Null(profileType.GetProperty("DocumentType"));

        // ADR-021-R5: No document number (Alloggiati PII)
        Assert.Null(profileType.GetProperty("DocumentNumber"));

        // ADR-021-R5: No document issue place (Alloggiati PII)
        Assert.Null(profileType.GetProperty("DocumentIssuingCountry"));
        Assert.Null(profileType.GetProperty("PlaceOfBirth"));

        // ADR-021-R5: No document scan URL (Alloggiati biometric data)
        Assert.Null(profileType.GetProperty("DocumentScanUrl"));

        // ADR-021-R5: No date of birth (Alloggiati personal data)
        Assert.Null(profileType.GetProperty("DateOfBirth"));

        // ADR-021-R5: No nationality (Alloggiati compliance field)
        Assert.Null(profileType.GetProperty("Nationality"));

        // Verify that the profile HAS the marketing-appropriate fields
        Assert.NotNull(profileType.GetProperty("ConfirmedMarketingEmail"));
        Assert.NotNull(profileType.GetProperty("MarketingConsentEmail"));
        Assert.NotNull(profileType.GetProperty("MarketingConsentWhatsApp"));
        Assert.NotNull(profileType.GetProperty("Country")); // Country is allowed (not PII like nationality)
        Assert.NotNull(profileType.GetProperty("TotalStays"));
        Assert.NotNull(profileType.GetProperty("TotalSpend"));

        // Verify the profile can be instantiated and used without Alloggiati data
        Assert.NotNull(profile);
        Assert.Equal("test@example.com", profile.ConfirmedMarketingEmail);
        Assert.True(profile.MarketingConsentEmail);
        Assert.Equal(5, profile.TotalStays);
        Assert.Equal(1500.00m, profile.TotalSpend);
    }
}
