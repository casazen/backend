using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Features;

/// <summary>FD-20: a feature flag is on only when <c>Features:{flag}</c> is explicitly <c>true</c>.</summary>
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

    // ─── SP-02: the two flags of the supplier showcase and payments ──────────────

    [Fact]
    public void All_SupplierFlags_AreListedLastInTheOrderExposedToTheFrontend()
    {
        // The supplier flags keep their order; a flag added after them is appended at the end.
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
                // SEO-INVITE: the call to action to sign up on the public SEO pages, off while CasaZen is on invitation.
                "SeoSignupCta",
                // E01/F01/US01: the B2B agency tier features, off until deployed.
                "AgencyTier",
                // CF-FIX: the ConcordatoFacile service, on in the test environment and off in production until the trial run is confirmed.
                "ConcordatoFacile",
                // SP-BKP: the backup suppliers of a host's request, off until the product owner turns it on in the test environment.
                "SupplierRequestBackups",
                // DEB-01: the document reading and the selfie check of the guest check-in, placeholders, off until real models exist.
                "CheckInIdentityVerification",
                // E02/F01/US02/T01: the Octorate connector (connection, mapping, webhook), off until the sandbox is validated.
                "OctorateConnector",
                // E07/F06/US01/T01: the unfinished marketing module (CRM profiles, segments, campaigns), off until it is safe to send.
                "CrmMarketing",
                // E10/F01/US01/T02: the activation of CasaZen with Octorate (flow status, plan, panel steps), off until it is ready to be shown.
                "OctorateOnboarding",
                // E06/F02/US02/T02: the issuance of fiscal documents by CasaZen (and the mode «CasaZen issues» of the issuer setting), off until the accountant approves the rules.
                "FiscalDocumentIssuance",
                // E06/F02/US01/T04: the sync of the billing data with Octorate (pre-fill of the invoice data, write-back), off until the customer endpoints are proven.
                "OctorateBillingSync",
                // E03/F02/US01/T03: the compliance traffic light of the cockpit and the feed of its items into the «Da fare» list, off until the proofs can be archived.
                "ComplianceTrafficLight",
                // E05/F02/US02/T02: the translation of a message by CasaZen's AI (the text of a guest goes to the provider), off until the product owner decides the transfer.
                "MessageTranslationAi",
                // E06/F03/US02/T01: the import of the documents Octorate issued, off until the documents list is proven on a structure with the invoice module.
                "OctorateDocumentImport",
                // E05/F05/US01/T01: the assistant that answers the guests' messages (knowledge base, rules, situations, drafts), off until the privacy notice and the processing agreement exist.
                "MessageAssistant",
                // E14/F01/US01/T01: the invoices of the property manager to the owners made from the statement versions, off until the accountant confirms the ⚖ choices.
                "OwnerInvoicing",
            },
            FeatureFlags.All);
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
        // The documented default of each flag (a missing value is off anyway): appsettings.json of the web project.
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
