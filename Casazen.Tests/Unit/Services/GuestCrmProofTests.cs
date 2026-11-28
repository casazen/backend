using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

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

        var profileType = typeof(GuestCrmProfile);

        Assert.Null(profileType.GetProperty("DocumentType"));

        Assert.Null(profileType.GetProperty("DocumentNumber"));

        Assert.Null(profileType.GetProperty("DocumentIssuingCountry"));
        Assert.Null(profileType.GetProperty("PlaceOfBirth"));

        Assert.Null(profileType.GetProperty("DocumentScanUrl"));

        Assert.Null(profileType.GetProperty("DateOfBirth"));

        Assert.Null(profileType.GetProperty("Nationality"));

        Assert.NotNull(profileType.GetProperty("ConfirmedMarketingEmail"));
        Assert.NotNull(profileType.GetProperty("MarketingConsentEmail"));
        Assert.NotNull(profileType.GetProperty("MarketingConsentWhatsApp"));
        Assert.NotNull(profileType.GetProperty("Country"));
        Assert.NotNull(profileType.GetProperty("TotalStays"));
        Assert.NotNull(profileType.GetProperty("TotalSpend"));

        Assert.NotNull(profile);
        Assert.Equal("test@example.com", profile.ConfirmedMarketingEmail);
        Assert.True(profile.MarketingConsentEmail);
        Assert.Equal(5, profile.TotalStays);
        Assert.Equal(1500.00m, profile.TotalSpend);
    }
}
