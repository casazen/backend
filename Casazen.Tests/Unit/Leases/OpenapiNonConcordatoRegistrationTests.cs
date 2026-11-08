using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// Openapi DocuEngine registration of a lease that is not canone concordato: the contract type goes in the request,
/// the filing fee is the existing registration-tax percentage, and an unpaid fee never reaches Openapi.
/// </summary>
public class OpenapiNonConcordatoRegistrationTests
{
    private const string OwnerId = "auth0|openapi-owner";
    private static readonly RegistroTaxOptions Registro = new()
    {
        Rate = 0.02m,
        FirstYearMinimumEur = 67m,
        ConcordatoAtaBaseShare = 0.70m,
    };

    [Fact]
    public void Quote_UsesTheRegistrationTaxPercentage_AndTheMinimum()
    {
        var full = OpenapiRegistrationFee.Quote(1_200m, Registro);
        Assert.Equal(0.02m, full.Percent);
        Assert.Equal(28_800, full.AmountCents);

        var minimum = OpenapiRegistrationFee.Quote(100m, Registro);
        Assert.Equal(6_700, minimum.AmountCents);
        Assert.False(OpenapiRegistrationFee.Applies(LeaseContractType.Concordato));
        Assert.True(OpenapiRegistrationFee.Applies(LeaseContractType.Libero));
        Assert.True(OpenapiRegistrationFee.Applies(LeaseContractType.Transitorio));
    }

    [Theory]
    [InlineData(LeaseContractType.Libero)]
    [InlineData(LeaseContractType.Transitorio)]
    public async Task Submit_SendsTheContractType_AsTheFirstSearchField(LeaseContractType contractType)
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"data":{"id":"66c881896de559cb65055392","state":"WAIT"}}"""),
            };
        });
        var client = NewClient(handler);
        var lease = Lease(contractType);

        var id = await client.SubmitLeaseRegistrationAsync(lease, "%PDF-1.4\n"u8.ToArray());

        Assert.Equal("66c881896de559cb65055392", id);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(contractType.ToString(), json.RootElement.GetProperty("search").GetProperty("field0").GetString());
        Assert.Equal("doc-lease", json.RootElement.GetProperty("documentId").GetString());
    }

    [Fact]
    public void Build_UnknownFieldOrderName_StillOccupiesItsIndex()
    {
        var search = OpenapiLeaseSearch.Build(
            Lease(LeaseContractType.Libero),
            default,
            ["contractType", "not-a-casazen-field", "taxRegime"]);

        Assert.Equal("Libero", search["field0"]);
        Assert.Equal(string.Empty, search["field1"]);
        Assert.Equal(LeaseTaxRegime.Ordinario.ToString(), search["field2"]);
    }

    [Fact]
    public async Task Submit_ReadsTheOAuthTokenFromTheDataEnvelope()
    {
        string? bearer = null;
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/tokens", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"success":true,"data":{"token":"minted-token"}}"""),
                };
            }

            bearer = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"data":{"id":"66c881896de559cb65055392","state":"WAIT"}}"""),
            };
        });
        var client = NewClient(handler, token: null);

        var id = await client.SubmitLeaseRegistrationAsync(Lease(LeaseContractType.Libero), default);

        Assert.Equal("66c881896de559cb65055392", id);
        Assert.Equal("minted-token", bearer);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("""{"token":"root-token"}""")]
    [InlineData("""{"success":true,"data":{"token":""}}""")]
    public async Task Submit_RejectsATokenThatIsNotInTheDataEnvelope(string oauthBody)
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(oauthBody) };
        });
        var client = NewClient(handler, token: null);

        await Assert.ThrowsAsync<LeaseRegistrationProviderException>(() =>
            client.SubmitLeaseRegistrationAsync(Lease(LeaseContractType.Libero), default));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Gate_Unpaid_Refuses_And_PaidQuote_Passes()
    {
        await using var db = NewDb();
        var lease = Lease(LeaseContractType.Libero);
        lease.OrgId = Guid.NewGuid();
        db.OpenapiLeaseRegistrationPayments.Add(new OpenapiLeaseRegistrationPayment
        {
            OrgId = lease.OrgId,
            LeaseContractId = lease.Id,
            FeePercent = 0.02m,
            AmountCents = 28_800,
            Status = OpenapiRegistrationPaymentStatus.Pending,
            StripeCheckoutSessionId = "cs_pending",
        });
        await db.SaveChangesAsync();
        var gate = new OpenapiRegistrationPaymentGate(db, Options.Create(Advisory()), NullLogger<OpenapiRegistrationPaymentGate>.Instance);

        var unpaid = await Assert.ThrowsAsync<DomainConflictException>(() => gate.EnsurePaidAsync(lease));
        Assert.Equal(OpenapiRegistrationFee.UnpaidCode, unpaid.Code);

        var row = await db.OpenapiLeaseRegistrationPayments.SingleAsync();
        row.Status = OpenapiRegistrationPaymentStatus.Completed;
        await db.SaveChangesAsync();

        await gate.EnsurePaidAsync(lease);
    }

    [Fact]
    public async Task SubmitToProvider_UnpaidLibero_DoesNotCallOpenapi()
    {
        var (service, openapi, provider, _) = await ReadyServiceAsync(LeaseContractType.Libero, paid: false);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => service.SubmitToProviderAsync(LeaseId, OwnerId, Delega));

        Assert.Equal(OpenapiRegistrationFee.UnpaidCode, error.Code);
        openapi.Verify(
            c => c.SubmitLeaseRegistrationAsync(It.IsAny<LeaseContract>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        provider.Verify(p => p.SubmitAsync(It.IsAny<LeaseContract>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitToProvider_PaidLibero_CallsOpenapi_NotTheGenericProvider()
    {
        var (service, openapi, provider, _) = await ReadyServiceAsync(LeaseContractType.Libero, paid: true);

        await service.SubmitToProviderAsync(LeaseId, OwnerId, Delega);

        openapi.Verify(
            c => c.SubmitLeaseRegistrationAsync(It.IsAny<LeaseContract>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(p => p.SubmitAsync(It.IsAny<LeaseContract>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitToProvider_Concordato_DoesNotUseOpenapi()
    {
        var (service, openapi, provider, _) = await ReadyServiceAsync(LeaseContractType.Concordato, paid: false);
        provider.Setup(p => p.SubmitAsync(It.IsAny<LeaseContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PROVIDER-1");

        await service.SubmitToProviderAsync(LeaseId, OwnerId, Delega);

        openapi.Verify(
            c => c.SubmitLeaseRegistrationAsync(It.IsAny<LeaseContract>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        provider.Verify(p => p.SubmitAsync(It.IsAny<LeaseContract>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static readonly Guid LeaseId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly RegistrationAuthorizationRequest Delega = new(new RliOptions().TosVersion, true);

    private static async Task<(RliRegistrationService Service, Mock<IOpenapiDocuEngineClient> Openapi, Mock<ILeaseRegistrationProvider> Provider, AppDbContext Db)>
        ReadyServiceAsync(LeaseContractType contractType, bool paid)
    {
        var db = NewDb();
        var org = AddOrg(db);
        AddUser(db, OwnerId, org.Id, UserRole.PropertyOwner);
        AddMember(db, OwnerId, org.Id, OrgRole.Owner);
        var property = HostScopeScenario.NewProperty(org.Id, OwnerId, "Casa");
        db.Properties.Add(property);
        var lease = new LeaseContract
        {
            Id = LeaseId,
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Signed,
            ContractType = contractType,
            FiscalRegime = FiscalRegime.CedolareSecca,
            TaxRegime = LeaseTaxRegime.Ordinario,
            StartDate = HostScopeScenario.Day(2027, 1, 1),
            EndDate = HostScopeScenario.Day(2030, 12, 31),
            MonthlyRent = 1_200m,
            SignedPdfStoragePath = "leases/signed.pdf",
        };
        db.LeaseContracts.Add(lease);
        if (paid)
        {
            db.OpenapiLeaseRegistrationPayments.Add(new OpenapiLeaseRegistrationPayment
            {
                OrgId = org.Id,
                LeaseContractId = lease.Id,
                FeePercent = 0.02m,
                AmountCents = 28_800,
                Status = OpenapiRegistrationPaymentStatus.Completed,
                StripeCheckoutSessionId = "cs_paid",
            });
        }

        await db.SaveChangesAsync();

        var provider = new Mock<ILeaseRegistrationProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.RliProvider)).Returns(true);
        var storage = new Mock<IFileStorage>();
        storage.Setup(s => s.OpenReadAsync(StorageBucket.Private, "leases/signed.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("%PDF-1.4\n"u8.ToArray()));
        var ape = new Mock<IApeComplianceService>();
        ape.Setup(a => a.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        var openapi = new Mock<IOpenapiDocuEngineClient>();
        openapi.SetupGet(c => c.IsConfigured).Returns(true);
        openapi.Setup(c => c.SubmitLeaseRegistrationAsync(It.IsAny<LeaseContract>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("66c881896de559cb65055392");

        var service = new RliRegistrationService(
            db,
            provider.Object,
            flags.Object,
            storage.Object,
            ape.Object,
            Options.Create(new RliOptions()),
            new OrgHolderService(db),
            NullLogger<RliRegistrationService>.Instance,
            openapi: openapi.Object,
            registrationPayment: new OpenapiRegistrationPaymentGate(db, Options.Create(Advisory()), NullLogger<OpenapiRegistrationPaymentGate>.Instance));
        return (service, openapi, provider, db);
    }

    private static OpenapiDocuEngineClient NewClient(HttpMessageHandler handler, string? token = "token")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.docuengine.openapi.com/") };
        return new OpenapiDocuEngineClient(
            http,
            Options.Create(new OpenapiOptions
            {
                BaseUrl = "https://test.docuengine.openapi.com",
                OAuthUrl = "https://test.oauth.openapi.com",
                Email = "host@example.com",
                ApiKey = "key",
                Token = token,
                LeaseDocumentId = "doc-lease",
            }),
            NullLogger<OpenapiDocuEngineClient>.Instance);
    }

    private static CedolareAdvisoryOptions Advisory() => new() { Registro = Registro };

    private static LeaseContract Lease(LeaseContractType contractType) => new()
    {
        ContractType = contractType,
        TaxRegime = LeaseTaxRegime.Ordinario,
        FiscalRegime = FiscalRegime.RegimeOrdinario,
        StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent = 1_200m,
        Property = new Property { City = "Bari", Address = "Via Sparano 1" },
        Parties =
        [
            new Party { Role = PartyRole.Landlord, FiscalCode = "RSSMRA80A01H501U", FirstName = "Mario", LastName = "Rossi" },
            new Party { Role = PartyRole.Tenant, FiscalCode = "VRDGLI85B41F205X", FirstName = "Giulia", LastName = "Verdi" },
        ],
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
