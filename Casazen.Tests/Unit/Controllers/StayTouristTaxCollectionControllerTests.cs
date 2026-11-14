using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.DTOs.TouristTax;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The host side of the tourist tax collection (E25/F01/US01/T01): the change of the tax of a stay evaluates the collection at once (a lower confirmed amount raises
/// the «To do», AC2), «Pagata in struttura» needs <c>payment.write</c> on the booking's property, and the setting of the organization is for its billing admins.
/// </summary>
public class StayTouristTaxCollectionControllerTests
{
    private static readonly Guid OrgId = Guid.Parse("5b2c8d1e-7f3a-4c6b-9d0e-1a2b3c4d5e61");
    private static readonly Guid PropertyId = Guid.Parse("6c3d9e2f-8a4b-4d7c-8e1f-2b3c4d5e6f72");
    private static readonly Guid BookingId = Guid.Parse("7d4eaf30-9b5c-4e8d-9f20-3c4d5e6f7a83");

    [Fact]
    public async Task TouristTaxChange_ConfirmExemptionsAndCollector_EvaluateTheCollectionRightAfterTheChange_Test()
    {
        var order = new List<string>();
        var tax = new Mock<IStayTouristTaxService>();
        tax.Setup(t => t.ConfirmAsync(BookingId, "auth0|host", It.IsAny<CancellationToken>())).Callback(() => order.Add("confirm")).ReturnsAsync(Tax());
        tax.Setup(t => t.SetExemptionsAsync(BookingId, It.IsAny<IReadOnlyList<GuestExemption>>(), "auth0|host", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("exemptions"))
            .ReturnsAsync(Tax());
        tax.Setup(t => t.SetCollectorAsync(BookingId, TouristTaxCollector.Host, "auth0|host", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("collector"))
            .ReturnsAsync(Tax());
        var collection = new Mock<ITouristTaxCollectionService>();
        collection.Setup(c => c.ReconcileAfterChangeAsync(BookingId, It.IsAny<CancellationToken>())).Callback(() => order.Add("reconcile")).Returns(Task.CompletedTask);
        var controller = Controller(tax.Object, collection.Object, allowed: true);

        Assert.IsType<OkObjectResult>((await controller.Confirm(BookingId, CancellationToken.None)).Result);
        var request = new SetTouristTaxExemptionsRequest { Exemptions = [new GuestExemptionDto { Position = 1, Code = null }] };
        Assert.IsType<OkObjectResult>((await controller.SetExemptions(BookingId, request, CancellationToken.None)).Result);
        var declared = new SetTouristTaxCollectorRequest { Collector = TouristTaxCollector.Host };
        Assert.IsType<OkObjectResult>((await controller.SetCollector(BookingId, declared, CancellationToken.None)).Result);

        // The change is saved first, the collection is evaluated right after it (never before, never skipped).
        Assert.Equal(["confirm", "reconcile", "exemptions", "reconcile", "collector", "reconcile"], order);
    }

    [Fact]
    public async Task TouristTaxChange_OfABookingTheCallerCannotUse_IsNotFound_AndChangesNothing()
    {
        var tax = new Mock<IStayTouristTaxService>();
        var collection = new Mock<ITouristTaxCollectionService>();
        var controller = Controller(tax.Object, collection.Object, allowed: false);

        var result = await controller.Confirm(BookingId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        tax.VerifyNoOtherCalls();
        collection.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TouristTaxPaidAtProperty_NeedsPaymentWriteOnTheBooking_Test()
    {
        var tax = new Mock<IStayTouristTaxService>();
        var collection = new Mock<ITouristTaxCollectionService>();
        collection
            .Setup(c => c.RecordPaidAtPropertyAsync(BookingId, "auth0|host", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxCollectionView(
                TouristTaxCollectionStatus.PaidAtProperty, TouristTaxNotPayableReason.PaidAtProperty, 0m, 0m, 0m, 15m, 0m, 0m, 0m, "EUR", null, true));

        // A caller with payment.write on the property records it; one without it (booking.read only, the accountant role) is told the booking does not exist.
        var denied = Controller(tax.Object, collection.Object, allowed: true, deniedOperation: PaymentOperations.Write);
        var refused = await denied.RecordPaidAtProperty(BookingId, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(refused.Result).StatusCode);
        collection.Verify(c => c.RecordPaidAtPropertyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var allowed = Controller(tax.Object, collection.Object, allowed: true);
        var done = await allowed.RecordPaidAtProperty(BookingId, CancellationToken.None);
        var dto = Assert.IsType<TouristTaxCollectionDto>(Assert.IsType<OkObjectResult>(done.Result).Value);
        Assert.Equal(TouristTaxCollectionStatus.PaidAtProperty, dto.Status);
        collection.Verify(c => c.RecordPaidAtPropertyAsync(BookingId, "auth0|host", It.IsAny<CancellationToken>()), Times.Once);

        // The policy of the action is payment.write, and every new route is behind the flag (404 before authentication when it is off).
        var type = typeof(StayTouristTaxController);
        var action = type.GetMethod(nameof(StayTouristTaxController.RecordPaidAtProperty))!;
        Assert.Equal(CasazenPolicies.PaymentWrite, Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Equal(FeatureFlags.TouristTaxOnlinePayment, Assert.Single(action.GetCustomAttributes<FeatureGateAttribute>()).Flag);
    }

    [Fact]
    public void TouristTaxSettings_AreForTheBillingAdminsOfTheOrg_AndBehindTheFlag_Test()
    {
        var type = typeof(StayTouristTaxController);
        var actions = new[] { nameof(StayTouristTaxController.GetSettings), nameof(StayTouristTaxController.SaveSettings) };

        Assert.All(
            actions,
            name =>
            {
                var action = type.GetMethod(name)!;
                Assert.Equal(CasazenPolicies.OrgBillingAdmin, Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>()).Policy);
                Assert.Equal(FeatureFlags.TouristTaxOnlinePayment, Assert.Single(action.GetCustomAttributes<FeatureGateAttribute>()).Flag);
            });
    }

    private static StayTouristTaxController Controller(
        IStayTouristTaxService tax,
        ITouristTaxCollectionService collection,
        bool allowed,
        HostOperationRequirement? deniedOperation = null)
    {
        var lookup = new Mock<IHostResourceLookup>();
        lookup.Setup(l => l.ForBookingAsync(BookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|host", PropertyId));
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, IEnumerable<IAuthorizationRequirement> requirements) =>
                allowed && (deniedOperation is null || !requirements.Contains(deniedOperation)) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|host")], "Test")),
            RequestServices = new ServiceCollection().AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer()).BuildServiceProvider(),
        };
        return new StayTouristTaxController(
            tax,
            lookup.Object,
            authorization.Object,
            NullLogger<StayTouristTaxController>.Instance,
            collection,
            new TestFeatureFlags(FeatureFlags.TouristTaxOnlinePayment),
            Mock.Of<IOrgContextResolver>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static StayTouristTaxView Tax() => new(
        BookingId,
        TouristTaxQuoteStatus.Calculated,
        TouristTaxRateSource.Official,
        false,
        3,
        3,
        false,
        [],
        15m,
        15m,
        TouristTaxCollector.Host,
        TouristTaxCollectorSource.Default,
        false,
        [],
        [],
        null,
        false);

    /// <summary>The key itself: the tests read which sentence was chosen, not its words.</summary>
    private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
