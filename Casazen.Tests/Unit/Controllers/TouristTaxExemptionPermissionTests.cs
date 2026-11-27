using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.DTOs.TouristTax;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// NF-46, art. 9 GDPR: the reason of a tourist tax exemption can reveal a health condition. The wizard and the tax endpoints are read with
/// <c>booking.read</c> (the accountant role holds it), but the reason, like the full document number, needs <c>guest.read</c>: a caller without it
/// gets the reason as <c>null</c> on the guests and on the tax, while the kind of the exemption and the amounts stay.
/// </summary>
public class TouristTaxExemptionPermissionTests
{
    private static readonly Guid OrgId = Guid.Parse("5b2c8d1e-7f3a-4c6b-9d0e-1a2b3c4d5e61");
    private static readonly Guid PropertyId = Guid.Parse("6c3d9e2f-8a4b-4d7c-8e1f-2b3c4d5e6f72");
    private static readonly Guid BookingId = Guid.Parse("7d4eaf30-9b5c-4e8d-9f20-3c4d5e6f7a83");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckInWizard_State_ShowsTheExemptionReasonOnlyToWhoHoldsGuestRead(bool holdsGuestRead)
    {
        var wizard = new Mock<ICheckInWizardService>();
        wizard.Setup(w => w.GetStateAsync(BookingId, It.IsAny<CancellationToken>())).ReturnsAsync(State());
        var controller = WizardController(wizard.Object, holdsGuestRead);

        var result = await controller.GetState(BookingId, CancellationToken.None);

        var dto = Assert.IsType<CheckInWizardStateDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        AssertReason(holdsGuestRead, Assert.Single(dto.Guests).TaxExemptionCode);
        var line = Assert.Single(dto.Tax!.Guests);
        AssertReason(holdsGuestRead, line.ExemptionCode);
        Assert.Equal(TouristTaxExemptionKind.Host, line.ExemptionKind);
        Assert.Equal(0m, line.Amount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckInWizard_Confirmation_ShowsTheExemptionReasonOnlyToWhoHoldsGuestRead(bool holdsGuestRead)
    {
        var wizard = new Mock<ICheckInWizardService>();
        wizard
            .Setup(w => w.ConfirmAsync(BookingId, "auth0|host", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckInConfirmation(
                new CheckInTaxOutcome(true, null),
                new OctorateCheckInSendResult(OctorateCheckInSendState.NotMirrored, "connector_off", false, [], false),
                new CheckInAlloggiatiNext(CheckInAlloggiatiNextStep.Manual, ComplianceSenderType.Manual),
                State()));
        var controller = WizardController(wizard.Object, holdsGuestRead);

        var result = await controller.Confirm(BookingId, CancellationToken.None);

        var dto = Assert.IsType<CheckInConfirmationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        AssertReason(holdsGuestRead, Assert.Single(dto.State.Guests).TaxExemptionCode);
        AssertReason(holdsGuestRead, Assert.Single(dto.State.Tax!.Guests).ExemptionCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StayTouristTax_Get_ShowsTheExemptionReasonOnlyToWhoHoldsGuestRead(bool holdsGuestRead)
    {
        var tax = new Mock<IStayTouristTaxService>();
        tax.Setup(t => t.GetAsync(BookingId, It.IsAny<CancellationToken>())).ReturnsAsync(Tax());
        var controller = new StayTouristTaxController(
            tax.Object, Lookup(), Authorization(holdsGuestRead), NullLogger<StayTouristTaxController>.Instance)
        {
            ControllerContext = Context(),
        };

        var result = await controller.Get(BookingId, CancellationToken.None);

        var dto = Assert.IsType<StayTouristTaxDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var line = Assert.Single(dto.Guests);
        AssertReason(holdsGuestRead, line.ExemptionCode);
        Assert.Equal(TouristTaxExemptionKind.Host, line.ExemptionKind);
        Assert.Equal(0m, line.Amount);
    }

    private static void AssertReason(bool shown, string? reason) =>
        Assert.Equal(shown ? TouristTaxExemptionReason.HealthCareCode : null, reason);

    private static CheckInWizardController WizardController(ICheckInWizardService wizard, bool holdsGuestRead) =>
        new(
            wizard,
            Mock.Of<IDocumentReadingService>(),
            Mock.Of<IStayLifecycleService>(),
            Lookup(),
            Authorization(holdsGuestRead),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<CheckInWizardController>.Instance)
        {
            ControllerContext = Context(),
        };

    private static ControllerContext Context() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|host")], "Test")),
        },
    };

    /// <summary>The booking of the caller's org on a property the caller reaches.</summary>
    private static IHostResourceLookup Lookup()
    {
        var lookup = new Mock<IHostResourceLookup>();
        lookup
            .Setup(l => l.ForBookingAsync(BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, "auth0|host", PropertyId));
        return lookup.Object;
    }

    /// <summary>The caller holds every permission asked, except <c>guest.read</c> when <paramref name="holdsGuestRead"/> is false.</summary>
    private static IAuthorizationService Authorization(bool holdsGuestRead)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, IEnumerable<IAuthorizationRequirement> requirements) =>
                !holdsGuestRead && requirements.Contains(GuestOperations.Read) ? AuthorizationResult.Failed() : AuthorizationResult.Success());
        return authorization.Object;
    }

    /// <summary>The tax of a stay of one guest, exempt for a health reason the host chose (nothing to pay).</summary>
    private static StayTouristTaxView Tax() => new(
        BookingId,
        TouristTaxQuoteStatus.Calculated,
        TouristTaxRateSource.Official,
        false,
        3,
        0,
        false,
        [],
        0m,
        0m,
        TouristTaxCollector.Host,
        TouristTaxCollectorSource.Default,
        false,
        [new StayTouristTaxGuestView(1, 3, 0, 0m, TouristTaxExemptionKind.Host, TouristTaxExemptionReason.HealthCareCode)],
        [],
        null,
        false);

    /// <summary>A stay of one guest, exempt from the tax for a health reason the host chose.</summary>
    private static CheckInState State()
    {
        var exempt = new StayGuest
        {
            Id = Guid.NewGuid(),
            BookingId = BookingId,
            OrgId = OrgId,
            Position = 1,
            Type = StayGuestType.SingleGuest,
            FirstName = "Ospite",
            LastName = "Esente",
            TaxExemptionCode = TouristTaxExemptionReason.HealthCareCode,
        };
        return new CheckInState(
            new CheckInBookingView(
                BookingId, PropertyId, "Casa Test", new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), 3, BookingSource.Direct, 2,
                BookingStatus.Confirmed, null),
            StayAttendance.Expected,
            [exempt],
            [],
            [],
            Tax(),
            new PropertySenderInfo(
                PropertyId, OrgId, ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.DefaultNotConnected, true, null, null),
            new OctorateCheckInView(false, [], []),
            new AlloggiatiStatusInfo(
                BookingId, AlloggiatiWebStatus.DaInviare, null, null, null, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), false, 24, false, false),
            new CheckInCapabilities(false, true, true, false),
            CheckInWizardSteps.Guests);
    }
}
