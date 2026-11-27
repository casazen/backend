using Casazen.Core.Documents;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External.Sdi;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// What <c>AddCasazenFiscalDocuments</c> registers (epic E06, NF-81, NF-82): the one place the fiscal documents of the epic are wired, so the exact list
/// is pinned here, as <c>AddCasazenBookingOperations_RegistersTheConsumersOnTheBus</c> pins its consumers. The proofs of the sync with Octorate build the
/// two handlers by hand, so without this test taking a registration out, or adding one nobody meant, would fail nothing: the sync would simply never run.
/// </summary>
public class FiscalDocumentsRegistrationTests
{
    [Fact]
    public void AddCasazenFiscalDocuments_RegistersTheServicesAndTheTwoConsumersOnTheBus_AndNothingElse()
    {
        var services = new ServiceCollection();

        services.AddCasazenFiscalDocuments(new ConfigurationBuilder().Build());

        var ours = services
            .Where(d => d.ServiceType.Namespace?.StartsWith("Casazen", StringComparison.Ordinal) == true)
            .Select(d => (d.ServiceType, d.ImplementationType, d.Lifetime))
            .ToList();
        var expected = new List<(Type ServiceType, Type? ImplementationType, ServiceLifetime Lifetime)>
        {
            (typeof(IInvoiceDataService), typeof(InvoiceDataService), ServiceLifetime.Scoped),
            (typeof(IFiscalTaxRuleResolver), typeof(FiscalTaxRuleResolver), ServiceLifetime.Singleton),
            (typeof(IDocumentIssuerService), typeof(DocumentIssuerService), ServiceLifetime.Scoped),

            // The whole invoicing setup (the wizard «Fatturazione») and the contributors of its missing items, in the order of the wizard (E11/F01/US01/T04): the
            // epics that own another piece register theirs next to these (channels and mandates, the Italian owner issuers).
            (typeof(IBillingSetupService), typeof(BillingSetupService), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(IssuerSectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(VatSectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(NumberingSectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(TouristTaxSectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(AutomationSectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(ItalySectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(ChannelCommissionsSectionContributor), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(OwnerMandatesSectionContributor), ServiceLifetime.Scoped),

            (typeof(IBillingSetupContributor), typeof(OwnerIssuerSetupContributor), ServiceLifetime.Scoped),

            (typeof(IEventHandler<DomainEvent>), typeof(InvoiceDataFromOctorateHandler), ServiceLifetime.Scoped),
            (typeof(IEventHandler<DomainEvent>), typeof(OctorateBillingWriteBackHandler), ServiceLifetime.Scoped),

            (typeof(IEventHandler<DomainEvent>), typeof(FiscalDocumentCheckOutHandler), ServiceLifetime.Scoped),

            (typeof(IEventHandler<DomainEvent>), typeof(FiscalDocumentBookingChangeHandler), ServiceLifetime.Scoped),

            (typeof(FiscalDocumentLinkTokens), typeof(FiscalDocumentLinkTokens), ServiceLifetime.Singleton),
            (typeof(IFiscalDocumentDeliveryService), typeof(FiscalDocumentDeliveryService), ServiceLifetime.Scoped),
            (typeof(IEventHandler<DomainEvent>), typeof(FiscalDocumentEmailHandler), ServiceLifetime.Scoped),

            // What the other tasks of the epic register, in the order they were added: the labels of the documents and the statement files (E06/F01/US01/T02),
            // the fees of the card payments (E06/F04/US01/T02), the gap-free numbers (E06/F01/US02/T02), the FatturaPA XML and its schema set (E06/F01/US02/T03),
            // the import of the documents Octorate issued (E06/F03/US02/T01), the fiscal documents (E06/F01/US01/T01) and the payment ledger (E06/F04/US01/T03).
            (typeof(IDocumentTexts), typeof(ResxDocumentTexts), ServiceLifetime.Singleton),
            (typeof(IStatementExportService), typeof(StatementExportService), ServiceLifetime.Scoped),
            (typeof(IPaymentProviderFeeService), typeof(PaymentProviderFeeService), ServiceLifetime.Scoped),
            (typeof(IFiscalNumberingService), typeof(FiscalNumberingService), ServiceLifetime.Scoped),
            (typeof(FatturaPaSchemaProvider), typeof(FatturaPaSchemaProvider), ServiceLifetime.Singleton),
            (typeof(IFatturaPaXmlBuilder), typeof(FatturaPaXmlBuilder), ServiceLifetime.Singleton),
            (typeof(IOctorateDocumentImportService), typeof(OctorateDocumentImportService), ServiceLifetime.Scoped),
            (typeof(IGuestDocumentLineBuilder), typeof(GuestDocumentLineBuilder), ServiceLifetime.Scoped),

            (typeof(IItalianTouristTaxSetup), typeof(ItalianTouristTaxSetupService), ServiceLifetime.Scoped),
            (typeof(IDocumentPlanService), typeof(DocumentPlanService), ServiceLifetime.Scoped),
            (typeof(IFiscalDocumentService), typeof(FiscalDocumentService), ServiceLifetime.Scoped),
            (typeof(IBookingPaymentLedgerService), typeof(BookingPaymentLedgerService), ServiceLifetime.Scoped),

            (typeof(IOwnerFiscalProfileService), typeof(OwnerFiscalProfileService), ServiceLifetime.Scoped),
            (typeof(IOwnerAccountingService), typeof(OwnerAccountingService), ServiceLifetime.Scoped),

            (typeof(IOwnerBulkJobs), typeof(HangfireOwnerBulkJobs), ServiceLifetime.Scoped),
            (typeof(OwnerBulkCloseJob), typeof(OwnerBulkCloseJob), ServiceLifetime.Scoped),
            (typeof(IOwnerReconciliationService), typeof(OwnerReconciliationService), ServiceLifetime.Scoped),
            (typeof(IOwnerStatementDocuments), typeof(OwnerStatementDocuments), ServiceLifetime.Scoped),
            (typeof(IOwnerPayoutService), typeof(OwnerPayoutService), ServiceLifetime.Scoped),

            (typeof(OwnerPayoutFacts), typeof(OwnerPayoutFacts), ServiceLifetime.Scoped),

            (typeof(IOwnerMandateService), typeof(OwnerMandateService), ServiceLifetime.Scoped),
            (typeof(OwnerMandateDocuments), typeof(OwnerMandateDocuments), ServiceLifetime.Scoped),

            (typeof(IOwnerDirectoryService), typeof(OwnerDirectoryService), ServiceLifetime.Scoped),

            (typeof(IPortalAccountHolderResolver), typeof(PortalAccountHolderResolver), ServiceLifetime.Scoped),
            (typeof(IPortalAccountSettingsService), typeof(PortalAccountSettingsService), ServiceLifetime.Scoped),
            (typeof(IOwnerInvoiceService), typeof(OwnerInvoiceService), ServiceLifetime.Scoped),

            (typeof(IOwnerIssuerProfileService), typeof(OwnerIssuerProfileService), ServiceLifetime.Scoped),

            // The channel to the SDI (E17/F01/US02): the typed client of the provider's adapter, the verifier of its webhook, the port (the adapter when Sdi:Provider is configured), the SDI states of the
            // electronic invoices, the correction of a rejected one, the consumer of the rejection (the «To do») and the item of the billing setup for the issuers that wait for their VAT number.
            (typeof(RestSdiEInvoiceProvider), null, ServiceLifetime.Transient),
            (typeof(SdiWebhookVerifier), typeof(SdiWebhookVerifier), ServiceLifetime.Singleton),
            (typeof(ISdiEInvoiceProvider), null, ServiceLifetime.Scoped),
            (typeof(IFiscalDocumentSdiService), typeof(FiscalDocumentSdiService), ServiceLifetime.Scoped),
            (typeof(IFiscalDocumentCorrectionService), typeof(FiscalDocumentCorrectionService), ServiceLifetime.Scoped),
            (typeof(IEventHandler<DomainEvent>), typeof(FiscalDocumentRejectionHandler), ServiceLifetime.Scoped),
            (typeof(IBillingSetupContributor), typeof(SdiIssuerRegistrationContributor), ServiceLifetime.Scoped),

            (typeof(IOwnerIssuedDocumentsExport), typeof(OwnerIssuedDocumentsExport), ServiceLifetime.Scoped),

            (typeof(ISelfBillingService), typeof(SelfBillingService), ServiceLifetime.Scoped),

            (typeof(IOwnerTaxChecks), typeof(OwnerTaxChecks), ServiceLifetime.Scoped),
            (typeof(IEventHandler<DomainEvent>), typeof(CompanyTenantDecisionHandler), ServiceLifetime.Scoped),
        };

        Assert.Equal(expected, ours);
    }

    [Fact]
    public void AddCasazenFiscalDocuments_BindsTheSectionFiscalDocuments_ToTheSwitchesAndTheCatalogOfTheTaxRules()
    {
        var settings = new Dictionary<string, string?>
        {
            ["FiscalDocuments:OctorateIssuesAllBookings"] = "true",
            ["FiscalDocuments:ImportOctorateDocuments"] = "true",
            ["FiscalDocuments:TaxRules:0:Jurisdiction"] = "CH",
            ["FiscalDocuments:TaxRules:0:Regime"] = "SwissNotRegistered",
            ["FiscalDocuments:TaxRules:0:ValidFrom"] = "2024-01-01",
            ["FiscalDocuments:TaxRules:0:Version"] = "ch-novat-test.1",
            ["FiscalDocuments:TaxRules:0:DocumentKind"] = "Receipt",
        };
        foreach (var type in Enum.GetValues<FiscalLineType>())
            settings[$"FiscalDocuments:TaxRules:0:Rates:{type}:Percent"] = "0";
        var services = new ServiceCollection();
        services.AddCasazenFiscalDocuments(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FiscalDocumentRulesOptions>>().Value;
        Assert.True(options.OctorateIssuesAllBookings);
        Assert.True(options.ImportOctorateDocuments);

        var rule = provider.GetRequiredService<IFiscalTaxRuleResolver>()
            .Resolve("CH", DocumentRegime.SwissNotRegistered, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2026, 11, 18));
        Assert.Equal(("ch-novat-test.1", Casazen.Core.Options.FiscalDocumentKind.Receipt), (rule.Version, rule.DocumentKind));
        Assert.Null(provider.GetRequiredService<IFiscalTaxRuleResolver>()
            .TryResolve("IT", DocumentRegime.VatOrdinary, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2026, 11, 18)));
    }

    [Fact]
    public async Task TheTwoConsumers_AreResolvedFromTheContainer_InTheOrderOfRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFeatureFlags>(new TestFeatureFlags());
        services.AddSingleton(Mock.Of<IHostEnvironment>());
        services.AddSingleton(Mock.Of<IOctorateBookingTargets>());
        services.AddSingleton(Mock.Of<IOctorateApiClient>());
        services.AddSingleton(Mock.Of<IOctorateOrgScope>());

        services.AddSingleton(new OctorateRateLimiter(TimeProvider.System));
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"fiscal-registration-{Guid.NewGuid():N}"));
        services.AddCasazenFiscalDocuments(new ConfigurationBuilder().Build());

        services.AddScoped(_ => Mock.Of<IFiscalDocumentService>());

        services.AddScoped(_ => Mock.Of<IFiscalDocumentDeliveryService>());

        services.AddScoped(_ => Mock.Of<IInternalActivityService>());

        services.AddScoped(_ => Mock.Of<IOwnerTaxChecks>());

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var handlers = scope.ServiceProvider.GetServices<IEventHandler<DomainEvent>>().Select(handler => handler.GetType()).ToList();

        Assert.Equal(
            [
                typeof(InvoiceDataFromOctorateHandler), typeof(OctorateBillingWriteBackHandler), typeof(FiscalDocumentCheckOutHandler), typeof(FiscalDocumentBookingChangeHandler),
                typeof(FiscalDocumentEmailHandler), typeof(FiscalDocumentRejectionHandler), typeof(CompanyTenantDecisionHandler),
            ],
            handlers);
        Assert.IsType<InvoiceDataService>(scope.ServiceProvider.GetRequiredService<IInvoiceDataService>());
        Assert.IsType<DocumentIssuerService>(scope.ServiceProvider.GetRequiredService<IDocumentIssuerService>());
    }
}
