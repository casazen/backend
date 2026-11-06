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
            // The invoice data of a booking and the issuer of the documents (E06/F02/US01/T02, E06/F02/US02/T02).
            (typeof(IInvoiceDataService), typeof(InvoiceDataService), ServiceLifetime.Scoped),
            (typeof(IDocumentIssuerService), typeof(DocumentIssuerService), ServiceLifetime.Scoped),

            // The two consumers of the sync with Octorate (E06/F02/US01/T04): the pre-fill on BookingCreated, then the write-back on InvoiceDataSaved.
            (typeof(IEventHandler<DomainEvent>), typeof(InvoiceDataFromOctorateHandler), ServiceLifetime.Scoped),
            (typeof(IEventHandler<DomainEvent>), typeof(OctorateBillingWriteBackHandler), ServiceLifetime.Scoped),
        };

        Assert.Equal(expected, ours);
    }

    [Fact]
    public void AddCasazenFiscalDocuments_BindsTheSectionFiscalDocuments_ToTheOptionsOfTheRules()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FiscalDocuments:OctorateIssuesAllBookings"] = "true",
                    ["FiscalDocuments:StampDutyThreshold"] = "80.5",
                    ["FiscalDocuments:Rules:VatOrdinary:DocumentKind"] = "Invoice",
                    ["FiscalDocuments:Rules:VatOrdinary:VatRatePercent"] = "0",
                    ["FiscalDocuments:Rules:VatOrdinary:VatNatureCode"] = "N2.2",
                })
            .Build();
        var services = new ServiceCollection();
        services.AddCasazenFiscalDocuments(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FiscalDocumentRulesOptions>>().Value;

        Assert.True(options.OctorateIssuesAllBookings);
        Assert.Equal(80.5m, options.StampDutyThreshold);
        Assert.True(options.RuleOf(DocumentRegime.VatOrdinary)?.IsConfigured);
        Assert.Null(options.RuleOf(DocumentRegime.PrivatePerson));
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

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var handlers = scope.ServiceProvider.GetServices<IEventHandler<DomainEvent>>().Select(handler => handler.GetType()).ToList();

        Assert.Equal([typeof(InvoiceDataFromOctorateHandler), typeof(OctorateBillingWriteBackHandler)], handlers);
        Assert.IsType<InvoiceDataService>(scope.ServiceProvider.GetRequiredService<IInvoiceDataService>());
        Assert.IsType<DocumentIssuerService>(scope.ServiceProvider.GetRequiredService<IDocumentIssuerService>());
    }
}
