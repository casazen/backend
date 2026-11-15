using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E06/F01/US01/T01, E06/F03/US02/T01, E06/F04/US01/T03 (NF-83, NF-84, NF-85): the controllers of the fiscal documents, of the PDF of a document Octorate issued
/// and of the payment ledger answer 404 for what the caller may not reach (a document of another org, a booking out of the caller's scope, a document without a
/// booking for a caller restricted to some properties) before anything is read, issued or asked of Octorate; a caller who reaches the whole org is served.
/// </summary>
public class FiscalControllersReachTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private const string UserId = "auth0|collaborator-solo-alcuni";

    [Fact]
    public async Task FiscalDocuments_DocumentOfAnotherOrgOrOutOfScope_IsNotFound_Test()
    {
        var bookingId = Guid.NewGuid();
        var onBooking = Document(OrgId, bookingId);
        var withoutBooking = Document(OrgId, null);
        var foreign = Document(OtherOrgId, null);
        var documents = new Mock<IFiscalDocumentService>();
        foreach (var document in new[] { onBooking, withoutBooking, foreign })
            documents.Setup(d => d.GetAsync(document.Id, It.IsAny<CancellationToken>())).ReturnsAsync(document);
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", Guid.NewGuid()));

        // A caller restricted to some properties: the booking is of a property it does not reach (the authorization says no), and a document that belongs to no
        // booking is for the whole org only.
        var restricted = Controller(documents.Object, resources.Object, Authorization(allowed: false), new HostScope(OrgId, GrantedToUserId: UserId));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf((await restricted.Get(onBooking.Id)).Result));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf((await restricted.Get(withoutBooking.Id)).Result));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf((await restricted.Get(foreign.Id)).Result));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf((await restricted.Get(Guid.NewGuid())).Result));

        // The whole org: its own document without a booking is served, the document of another org is a 404 like a missing one.
        var wholeOrg = Controller(documents.Object, resources.Object, Authorization(allowed: false), new HostScope(OrgId));
        Assert.IsType<OkObjectResult>((await wholeOrg.Get(withoutBooking.Id)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf((await wholeOrg.Get(foreign.Id)).Result));

        // A booking the caller may read: the document of the booking is served.
        var allowed = Controller(documents.Object, resources.Object, Authorization(allowed: true), new HostScope(OrgId, GrantedToUserId: UserId));
        Assert.IsType<OkObjectResult>((await allowed.Get(onBooking.Id)).Result);
    }

    /// <summary>
    /// PROOF TEST: privacy and tenant rule (FIX/RES-DOCS). The detail of a document gives the drawer the type, the VAT rate and the VAT of every line, who collected the money and the issuer as the document keeps it
    /// (name, address, country, tax ids), under the names the drawer reads (the JSON, not the C# names) and in the spellings of its enums, and nothing more of the issuer (no bank account, no agency, no co-owner).
    /// It is served only to a caller who reaches the document: another organization's document, and a document of a booking the caller may not read, are a 404 that says nothing of the issuer. The rows of the list
    /// carry no issuer.
    /// </summary>
    [Fact]
    public async Task Document_ReadModel_ExposesLinesAndIssuerOnlyToTheOwnOrg_Test()
    {
        var own = Detailed(OrgId, "TEST Casa Lago Sagl");
        var foreign = Detailed(OtherOrgId, "TEST Altra Agenzia Srl");
        var bookingId = Guid.NewGuid();
        var onBooking = Detailed(OrgId, "TEST Casa Lago Sagl");
        onBooking.BookingId = bookingId;
        var documents = new Mock<IFiscalDocumentService>();
        foreach (var document in new[] { own, foreign, onBooking })
            documents.Setup(d => d.GetAsync(document.Id, It.IsAny<CancellationToken>())).ReturnsAsync(document);
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", Guid.NewGuid()));
        var wholeOrg = Controller(documents.Object, resources.Object, Authorization(allowed: false), new HostScope(OrgId));

        // Its own document: every member the drawer reads, with the name and the spelling it reads, the lines in order.
        var served = Assert.IsType<FiscalDocumentDto>(Assert.IsType<OkObjectResult>((await wholeOrg.Get(own.Id)).Result).Value);
        var json = JsonSerializer.SerializeToElement(served, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var lines = json.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Accommodation", "TouristTax", "Cleaning"], lines.Select(l => l.GetProperty("lineType").GetString() ?? string.Empty).ToArray());
        Assert.Equal((10m, 30m), (lines[0].GetProperty("vatRatePercent").GetDecimal(), lines[0].GetProperty("vatAmount").GetDecimal()));
        Assert.Equal((0m, 0m, "N1"), (lines[1].GetProperty("vatRatePercent").GetDecimal(), lines[1].GetProperty("vatAmount").GetDecimal(), lines[1].GetProperty("vatNatureCode").GetString()));

        // A line written before its VAT was stored says so (null): nothing is made up.
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("vatAmount").ValueKind);
        Assert.Equal("Portal", json.GetProperty("paymentChannel").GetString());
        var issuer = json.GetProperty("issuer");
        Assert.Equal(
            ["legalName", "addressStreet", "addressCivic", "postalCode", "city", "province", "countryCode", "vatNumber", "fiscalCode", "swissVatNumber"],
            issuer.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            ("TEST Casa Lago Sagl", "Via del Lago", "12", "22100", "Como", "CO", "IT", "01234567897", "01234567897"),
            (
                issuer.GetProperty("legalName").GetString(),
                issuer.GetProperty("addressStreet").GetString(),
                issuer.GetProperty("addressCivic").GetString(),
                issuer.GetProperty("postalCode").GetString(),
                issuer.GetProperty("city").GetString(),
                issuer.GetProperty("province").GetString(),
                issuer.GetProperty("countryCode").GetString(),
                issuer.GetProperty("vatNumber").GetString(),
                issuer.GetProperty("fiscalCode").GetString()));
        Assert.Equal(JsonValueKind.Null, issuer.GetProperty("swissVatNumber").ValueKind);
        foreach (var kept in new[] { "IBAN-MASKED-TEST", "QRIBAN-TEST", "TEST Agenzia Intermediaria" })
            Assert.DoesNotContain(kept, issuer.GetRawText(), StringComparison.Ordinal);

        // The spellings the drawer knows are names of the enums it reads.
        string[] drawerLineTypes = ["Accommodation", "AdvancedExpense", "Cleaning", "Commission", "ExtraService", "Other", "Recharge", "TouristTax"];
        Assert.All(drawerLineTypes, name => Assert.Contains(name, Enum.GetNames<FiscalLineType>()));
        Assert.Contains("Portal", Enum.GetNames<FiscalPaymentChannel>());

        // The issuer leaves with one document at a time: the rows of the list carry none.
        Assert.Null(FiscalDocumentDto.ForList(own).Issuer);
        Assert.Equal("Portal", FiscalDocumentDto.ForList(own).PaymentChannel);

        // Another organization's document: a 404 that says nothing of it.
        var notFound = Assert.IsType<ObjectResult>((await wholeOrg.Get(foreign.Id)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.DoesNotContain("TEST Altra Agenzia", JsonSerializer.Serialize(notFound.Value), StringComparison.Ordinal);

        // A document of a booking the caller may not read: the same 404; one it may read is served with its issuer.
        var restricted = Controller(documents.Object, resources.Object, Authorization(allowed: false), new HostScope(OrgId, GrantedToUserId: UserId));
        var hidden = Assert.IsType<ObjectResult>((await restricted.Get(onBooking.Id)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, hidden.StatusCode);
        Assert.DoesNotContain("TEST Casa Lago", JsonSerializer.Serialize(hidden.Value), StringComparison.Ordinal);
        var reader = Controller(documents.Object, resources.Object, Authorization(allowed: true), new HostScope(OrgId, GrantedToUserId: UserId));
        var readable = Assert.IsType<FiscalDocumentDto>(Assert.IsType<OkObjectResult>((await reader.Get(onBooking.Id)).Result).Value);
        Assert.Equal("TEST Casa Lago Sagl", readable.Issuer?.LegalName);
    }

    [Fact]
    public async Task PaymentLedger_BookingTheCallerMayNotRead_IsNotFound_AndTheLedgerIsNeverAsked_Test()
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var ledger = new Mock<IBookingPaymentLedgerService>(MockBehavior.Strict);
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", propertyId));
        resources.Setup(r => r.ForPropertyAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", propertyId));
        var controller = new PaymentLedgerController(ledger.Object, resources.Object, Authorization(allowed: false), Org(), Scope(new HostScope(OrgId, GrantedToUserId: UserId)))
        {
            ControllerContext = Context(),
        };

        Assert.IsType<NotFoundResult>((await controller.ForBooking(bookingId)).Result);
        Assert.IsType<NotFoundResult>((await controller.ForBooking(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundResult>((await controller.List(bookingId: bookingId)).Result);
        Assert.IsType<NotFoundResult>((await controller.List(propertyId: propertyId)).Result);
        ledger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OctorateDocuments_DocumentOfAnotherOrgOrOutOfScope_IsNotFound_AndOctorateIsNeverCalled_Test()
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OctorateDocumentsReach_{Guid.NewGuid()}").Options,
            new ReachTenant(),
            new EphemeralDataProtectionProvider());
        var foreign = Imported(OtherOrgId, "D-foreign");
        var own = Imported(OrgId, "D-own");
        db.FiscalDocuments.AddRange(foreign, own);
        await db.SaveChangesAsync();
        var octorate = new Mock<IOctorateApiClient>(MockBehavior.Strict);

        // Another org's document: 404 for the whole org too. A document of the org without a booking: 404 for a caller restricted to some properties.
        foreach (var scope in new[] { new HostScope(OrgId), new HostScope(OrgId, GrantedToUserId: UserId) })
        {
            var controller = OctorateController(db, octorate.Object, scope);
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.GetOctoratePdf(foreign.Id)));
        }

        var restricted = OctorateController(db, octorate.Object, new HostScope(OrgId, GrantedToUserId: UserId));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await restricted.GetOctoratePdf(own.Id)));
        octorate.VerifyNoOtherCalls();
    }

    /// <summary>A document without a booking (an imported one whose reservation CasaZen does not hold) is served from the accommodation it was read from.</summary>
    [Fact]
    public async Task OctorateDocuments_DocumentWithoutABooking_IsServedFromItsOwnAccommodation_Test()
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OctorateDocumentsOwn_{Guid.NewGuid()}").Options,
            new ReachTenant(),
            new EphemeralDataProtectionProvider());
        var own = Imported(OrgId, "D-own");
        own.OctorateAccommodationId = "ACC-9";
        db.FiscalDocuments.Add(own);
        await db.SaveChangesAsync();
        var octorate = new Mock<IOctorateApiClient>();
        octorate.Setup(c => c.GetDocumentPdfAsync("ACC-9", "D-own", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream([1, 2, 3]));

        var served = await OctorateController(db, octorate.Object, new HostScope(OrgId)).GetOctoratePdf(own.Id);

        Assert.IsType<FileStreamResult>(served);
        octorate.Verify(c => c.GetDocumentPdfAsync("ACC-9", "D-own", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static OctorateDocumentsController OctorateController(AppDbContext db, IOctorateApiClient octorate, HostScope scope) => new(
        db,
        octorate,
        Mock.Of<IOctorateOrgScope>(),
        Mock.Of<IHostResourceLookup>(),
        Authorization(allowed: false),
        Org(),
        Scope(scope),
        NullLogger<OctorateDocumentsController>.Instance)
    {
        ControllerContext = Context(),
    };

    private static FiscalDocumentsController Controller(IFiscalDocumentService documents, IHostResourceLookup resources, IAuthorizationService authorization, HostScope scope) =>
        new(documents, resources, authorization, Org(), Scope(scope), NullLogger<FiscalDocumentsController>.Instance)
        {
            ControllerContext = Context(),
        };

    private static FiscalDocument Document(Guid orgId, Guid? bookingId) => new()
    {
        OrgId = orgId,
        BookingId = bookingId,
        Source = FiscalDocumentSource.CasaZen,
        Kind = Casazen.Core.Entities.Enums.FiscalDocumentKind.Invoice,
        Status = FiscalDocumentStatus.Issued,
        Currency = "EUR",
        SdiStatus = FiscalDocumentSdiStatuses.ToSend,
    };

    /// <summary>
    /// An issued invoice with its lines (out of order, one written before its VAT was stored), the portal as who collected the money and the snapshot of its issuer, with what the drawer must never get
    /// (a masked bank account, the account of a QR-bill, the agency that issued on behalf of the owner).
    /// </summary>
    private static FiscalDocument Detailed(Guid orgId, string issuerName) => new()
    {
        OrgId = orgId,
        Source = FiscalDocumentSource.CasaZen,
        Kind = Casazen.Core.Entities.Enums.FiscalDocumentKind.Invoice,
        Status = FiscalDocumentStatus.Issued,
        Series = "2026",
        SequenceNumber = 7,
        Number = "2026/7",
        Currency = "EUR",
        PaymentChannel = FiscalPaymentChannel.Portal,
        SdiStatus = FiscalDocumentSdiStatuses.ToSend,
        IssuerSnapshot = new FiscalIssuerSnapshot
        {
            LegalName = issuerName,
            AddressStreet = "Via del Lago",
            AddressCivic = "12",
            PostalCode = "22100",
            City = "Como",
            Province = "CO",
            CountryCode = "IT",
            VatNumber = "01234567897",
            FiscalCode = "01234567897",
            IbanMasked = "IBAN-MASKED-TEST",
            QrBillAccount = "QRIBAN-TEST",
            Intermediary = new FiscalIssuerIntermediary("TEST Agenzia Intermediaria", "09876543217", null, "IT"),
        }.ToJson(),
        Lines =
        {
            new FiscalDocumentLine { OrgId = orgId, LineNumber = 3, LineType = FiscalLineType.Cleaning, Description = "Pulizie finali", Quantity = 1m, UnitPrice = 50m, TotalPrice = 50m, VatRatePercent = 10m },
            new FiscalDocumentLine { OrgId = orgId, LineNumber = 1, LineType = FiscalLineType.Accommodation, Description = "Soggiorno", Quantity = 1m, UnitPrice = 300m, TotalPrice = 300m, VatRatePercent = 10m, VatAmount = 30m },
            new FiscalDocumentLine { OrgId = orgId, LineNumber = 2, LineType = FiscalLineType.TouristTax, Description = "Tassa di soggiorno", Quantity = 6m, UnitPrice = 2m, TotalPrice = 12m, VatRatePercent = 0m, VatNatureCode = "N1", VatAmount = 0m },
        },
    };

    private static FiscalDocument Imported(Guid orgId, string octorateId) => new()
    {
        OrgId = orgId,
        Source = FiscalDocumentSource.Octorate,
        Kind = Casazen.Core.Entities.Enums.FiscalDocumentKind.Invoice,
        Status = FiscalDocumentStatus.Issued,
        Currency = "EUR",
        OctorateDocumentId = octorateId,
        OctoratePdfAvailable = true,
        SdiStatus = FiscalDocumentSdiStatuses.Unknown,
    };

    private static int? StatusOf(IActionResult? result) => (result as IStatusCodeActionResult)?.StatusCode;

    private static IAuthorizationService Authorization(bool allowed)
    {
        var service = new Mock<IAuthorizationService>();
        service
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service.Object;
    }

    private static IOrgContextResolver Org()
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        return resolver.Object;
    }

    private static IHostScopeResolver Scope(HostScope scope)
    {
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return resolver.Object;
    }

    /// <summary>A signed-in caller, and the services the problem answer of a 404 needs.</summary>
    private static ControllerContext Context()
    {
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", UserId), new Claim(ClaimTypes.NameIdentifier, UserId)], "test");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services },
        };
    }
}

file sealed class ReachTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
