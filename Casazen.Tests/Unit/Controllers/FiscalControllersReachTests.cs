using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Controllers;
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
