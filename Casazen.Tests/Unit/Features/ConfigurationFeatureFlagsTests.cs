using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Features;

public class ConfigurationFeatureFlagsTests
{
    [Fact]
    public void IsEnabled_FlagMissing_ReturnsFalse()
    {
        var flags = Flags(new Dictionary<string, string?>());

        Assert.False(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void IsEnabled_FlagTrue_ReturnsTrue(string value)
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = value });

        Assert.True(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    public void IsEnabled_FlagFalseOrNotBoolean_ReturnsFalse(string value)
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = value });

        Assert.False(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Fact]
    public void IsEnabled_OtherFlagOn_DoesNotEnableThisFlag()
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:SomethingElse"] = "true" });

        Assert.False(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Fact]
    public void All_ContainsOtaPartnerApi()
    {
        Assert.Contains(FeatureFlags.OtaPartnerApi, FeatureFlags.All);
    }

    [Fact]
    public void All_SupplierFlags_AreListedLastInTheOrderExposedToTheFrontend()
    {
        Assert.Equal("SupplierShowcaseBooking", FeatureFlags.SupplierShowcaseBooking);
        Assert.Equal("SupplierOnlinePayments", FeatureFlags.SupplierOnlinePayments);
        Assert.Equal("SupplierRequestAutoCancel", FeatureFlags.SupplierRequestAutoCancel);
        Assert.Equal(
            new[]
            {
                "OtaPartnerApi",
                "AiSupplierDiscovery",
                "RliProvider",
                "ESignProvider",
                "SupplierShowcaseBooking",
                "SupplierOnlinePayments",
                "SupplierRequestAutoCancel",
                "SeoSignupCta",
                "AgencyTier",
                "ConcordatoFacile",
                "SupplierRequestBackups",
                "CheckInIdentityVerification",
                "OctorateConnector",
                "CrmMarketing",
                "OctorateOnboarding",
                "FiscalDocumentIssuance",
                "OctorateBillingSync",
                "ComplianceTrafficLight",
                "MessageTranslationAi",
                "OctorateDocumentImport",
                "MessageAssistant",
                "OwnerInvoicing",
                "AccountantPackage",
                "TouristTaxOnlinePayment",
                "Routines",
                "PortalChatCheckInLink",
                "GuestContactInfo",
                // E17/F01/US02/T01: the submission of the Italian electronic invoices to the SDI through the provider and the correction of the rejected ones, off until the accountant confirms the ⚖ choices.
                "SdiSubmission",
                "TaxReportingExports",
                // E20/F02/US01/T02: the deletion of the guests' documents once the stay is covered by an archived Alloggiati receipt, off until the test environment has shown it.
                "PurgeDocumentsAfterReceipt",
                // E05/F07/US01/T01: the scheduled messages of the routines, off until the product owner confirms the send window and the retry times.
                "ScheduledMessages",
                "WhiteLabelHost",
                "Auth0HostSync",
                "SiteEmbed",
                "TicinoNotificationAlerts",
                "GuestRegistrationRegimes",
                "GuestPortal",
                "GuestPortalSupplierServices",
                "GuestPortalAutoReplies",
                "MarketingGuestInterestAi",
                "CheckInIdentitySelfHosted",
                "CheckInIdentityAutoApproval",
                "CheckInIdentityReviewEvidence",
            },
            FeatureFlags.All);
    }

    [Theory]
    [InlineData(FeatureFlags.CheckInIdentitySelfHosted)]
    [InlineData(FeatureFlags.CheckInIdentityAutoApproval)]
    [InlineData(FeatureFlags.CheckInIdentityReviewEvidence)]
    public void IsEnabled_SelfHostedIdentityFlags_AreOffByDefault_AndFollowOnlyTheirOwnKey(string flag)
    {
        string[] identityFlags =
        [
            FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityAutoApproval,
            FeatureFlags.CheckInIdentityReviewEvidence,
        ];
        var committed = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(flag));
        Assert.Equal("False", committed[$"{FeatureFlags.SectionName}:{flag}"]);

        var on = Flags(new Dictionary<string, string?> { [$"{FeatureFlags.SectionName}:{flag}"] = "true" });
        Assert.Equal([flag], identityFlags.Where(on.IsEnabled));
    }

    [Theory]
    [InlineData(FeatureFlags.SupplierShowcaseBooking)]
    [InlineData(FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierRequestAutoCancel)]
    public void IsEnabled_SupplierFlagNotConfigured_IsOff(string flag)
    {
        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(flag));
    }

    [Theory]
    [InlineData(FeatureFlags.SupplierShowcaseBooking, FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierOnlinePayments, FeatureFlags.SupplierShowcaseBooking)]
    [InlineData(FeatureFlags.SupplierRequestAutoCancel, FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierOnlinePayments, FeatureFlags.SupplierRequestAutoCancel)]
    public void IsEnabled_OneSupplierFlagOn_DoesNotTurnTheOtherOn(string on, string other)
    {
        var flags = Flags(new Dictionary<string, string?> { [$"Features:{on}"] = "true" });

        Assert.True(flags.IsEnabled(on));
        Assert.False(flags.IsEnabled(other));
    }

    [Fact]
    public void AppSettings_EveryFlagIsListedAndOffByDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();
        var flags = new ConfigurationFeatureFlags(configuration);

        Assert.All(FeatureFlags.All, flag =>
        {
            Assert.Equal("False", configuration[$"{FeatureFlags.SectionName}:{flag}"]);
            Assert.False(flags.IsEnabled(flag), $"{flag} must be off by default");
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    private static ConfigurationFeatureFlags Flags(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
