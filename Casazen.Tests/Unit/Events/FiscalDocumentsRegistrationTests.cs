using Casazen.Core.Documents;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
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

        // The exact list, in the order of registration, of what is CasaZen's (the machinery of the options registers its own services, which are not).
        var ours = services
            .Where(d => d.ServiceType.Namespace?.StartsWith("Casazen", StringComparison.Ordinal) == true)
            .Select(d => (d.ServiceType, d.ImplementationType, d.Lifetime))
            .ToList();
        var expected = new List<(Type ServiceType, Type? ImplementationType, ServiceLifetime Lifetime)>
        {
            // The invoice data of a booking, the catalog of the tax rules (E11/F01/US01/T02) and the issuer of the documents (E06/F02/US01/T02, E06/F02/US02/T02).
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

            // The two consumers of the sync with Octorate (E06/F02/US01/T04): the pre-fill on BookingCreated, then the write-back on InvoiceDataSaved.
            (typeof(IEventHandler<DomainEvent>), typeof(InvoiceDataFromOctorateHandler), ServiceLifetime.Scoped),
            (typeof(IEventHandler<DomainEvent>), typeof(OctorateBillingWriteBackHandler), ServiceLifetime.Scoped),

            // The draft of the document of a stay at its check-out (E12/F01/US01/T02): the consumer of CheckOutCompleted.
            (typeof(IEventHandler<DomainEvent>), typeof(FiscalDocumentCheckOutHandler), ServiceLifetime.Scoped),

            // The correction of the documents of a stay when its booking changes (E12/F01/US01/T03): the consumer of the booking changes and of the refunds.
            (typeof(IEventHandler<DomainEvent>), typeof(FiscalDocumentBookingChangeHandler), ServiceLifetime.Scoped),

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
            (typeof(IFiscalDocumentService), typeof(FiscalDocumentService), ServiceLifetime.Scoped),
            (typeof(IBookingPaymentLedgerService), typeof(BookingPaymentLedgerService), ServiceLifetime.Scoped),

            // The owner accounting of the agencies (E06/F05/US02): the fiscal profiles and rule sets (T01), the ledger, the versions and the close (T02), the payouts
            // with their withholding evidence and the filing states (T03).
            (typeof(IOwnerFiscalProfileService), typeof(OwnerFiscalProfileService), ServiceLifetime.Scoped),
            (typeof(IOwnerAccountingService), typeof(OwnerAccountingService), ServiceLifetime.Scoped),
            (typeof(IOwnerPayoutService), typeof(OwnerPayoutService), ServiceLifetime.Scoped),

            // The mandates of the owners, their billing address and the channel commission rules (E13/F01/US01/T01).
            (typeof(IOwnerMandateService), typeof(OwnerMandateService), ServiceLifetime.Scoped),
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

        // The bucket of calls to Octorate is registered by AddCasazenOctorate, as a singleton: the pre-fill needs it.
        services.AddSingleton(new OctorateRateLimiter(TimeProvider.System));
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"fiscal-registration-{Guid.NewGuid():N}"));
        services.AddCasazenFiscalDocuments(new ConfigurationBuilder().Build());

        // The consumers of the documents take the service of the documents, which has many dependencies of its own: this test is about what the container builds, not about it.
        services.AddScoped(_ => Mock.Of<IFiscalDocumentService>());

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var handlers = scope.ServiceProvider.GetServices<IEventHandler<DomainEvent>>().Select(handler => handler.GetType()).ToList();

        Assert.Equal(
            [typeof(InvoiceDataFromOctorateHandler), typeof(OctorateBillingWriteBackHandler), typeof(FiscalDocumentCheckOutHandler), typeof(FiscalDocumentBookingChangeHandler)],
            handlers);
        Assert.IsType<InvoiceDataService>(scope.ServiceProvider.GetRequiredService<IInvoiceDataService>());
        Assert.IsType<DocumentIssuerService>(scope.ServiceProvider.GetRequiredService<IDocumentIssuerService>());
    }
}
