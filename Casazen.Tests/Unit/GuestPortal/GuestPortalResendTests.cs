using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Email;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public sealed class GuestPortalResendTests : IDisposable
{
    private const string OwnerId = "auth0|owner_portal";
    private const string GuestAddress = "anna.ospite@guestmail.it";

    private readonly GuestPortalTestWorld _world = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly Mock<IBookingService> _bookings = new();
    private readonly Mock<IHostResourceLookup> _resources = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Resend_OtaWithoutEmail_Returns409_AnotherOrg_Returns404()
    {
        var booking = await SetupAsync(guestEmail: string.Empty);
        var controller = Controller();

        var noAddress = await controller.Resend(booking.Id, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(noAddress.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(GuestPortalLinkController.NoGuestEmailCode, Code(problem));
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(await _world.Db.GuestPortalLinks.AsNoTracking().ToListAsync());

        var foreign = Guid.NewGuid();
        _bookings.Setup(b => b.GetBookingAsync(foreign)).ReturnsAsync((Booking?)null);

        var notFound = await controller.Resend(foreign, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(notFound.Result).StatusCode);
        Assert.Empty(_emails.Snapshot());

        await SetGuestEmailAsync(GuestAddress);
        var ok = await controller.Resend(booking.Id, CancellationToken.None);

        var dto = Assert.IsType<GuestPortalLinkDto>(Assert.IsType<OkObjectResult>(ok.Result).Value);
        var queued = Assert.Single(_emails.Snapshot());
        Assert.Equal(GuestAddress, queued.To);
        Assert.Equal(EmailTemplates.Names.GuestPrepareArrival, queued.Template);
        Assert.Equal("Prepara il tuo arrivo — Trullo Aurora", queued.Content.Subject);
        Assert.Equal(GuestPortalRules.StateActive, dto.State);
        Assert.NotNull(dto.Url);
        Assert.Contains($"<a href=\"{dto.Url}\" style=", queued.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("/checkin/", queued.Content.HtmlBody, StringComparison.Ordinal);

        var active = await _world.LinkService().GetActiveAsync(_world.Org.Id, booking.Id);
        Assert.Equal(dto.Url, _world.LinkService().BuildUrl(active!));
    }

    [Fact]
    public void Resend_TakesNoAddressFromTheCaller()
    {
        var action = typeof(GuestPortalLinkController).GetMethod(nameof(GuestPortalLinkController.Resend))!;

        Assert.Equal(new[] { typeof(Guid), typeof(CancellationToken) }, action.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal("POST", action.GetCustomAttribute<HttpPostAttribute>()!.HttpMethods.Single());
        Assert.Equal("{bookingId:guid}/guest-portal/resend", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingWrite, action.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void Resend_IsBehindTheGuestPortalFlag_AndItsAnswerIsPrivate()
    {
        var gate = typeof(GuestPortalLinkController).GetCustomAttribute<FeatureGateAttribute>();

        Assert.NotNull(gate);
        Assert.Equal(FeatureFlags.GuestPortal, gate.Flag);
        Assert.NotNull(typeof(GuestPortalLinkController).GetCustomAttribute<PrivateAnswerAttribute>());
    }

    [Fact]
    public async Task Resend_WithoutBookingWrite_IsForbidden_AndNothingIsSent()
    {
        var booking = await SetupAsync(GuestAddress);
        var reader = Controller(HostAuthorizationTestHarness.Create(_world.Org.Id, (_, permission) => permission != "booking.write"));

        var result = await reader.Resend(booking.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(await _world.Db.GuestPortalLinks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Resend_ForACancelledBooking_Returns409NotEligible()
    {
        var booking = await SetupAsync(GuestAddress);
        booking.Status = BookingStatus.Cancelled;

        var result = await Controller().Resend(booking.Id, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("guest_portal_booking_not_eligible", Code(problem));
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task Resend_WhenTheQueueRefuses_Returns409EmailNotSent()
    {
        var booking = await SetupAsync(GuestAddress);
        _emails.Refuse = true;

        var result = await Controller().Resend(booking.Id, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(GuestPortalLinkController.EmailNotSentCode, Code(problem));
    }

    [Theory]
    [InlineData("guest@example.com")]
    [InlineData("anna@guests.test")]
    [InlineData("not an address")]
    public async Task Resend_ToAnAddressThatCannotBeWrittenTo_Returns409NoGuestEmail(string address)
    {
        var booking = await SetupAsync(address);

        var result = await Controller().Resend(booking.Id, CancellationToken.None);

        Assert.Equal(GuestPortalLinkController.NoGuestEmailCode, Code(Assert.IsType<ObjectResult>(result.Result)));
        Assert.Empty(_emails.Snapshot());
    }

    private async Task SetGuestEmailAsync(string address)
    {
        var guest = await _world.Db.Guests.SingleAsync();
        guest.Email = address;
        await _world.Db.SaveChangesAsync();
        _world.Db.ChangeTracker.Clear();
    }

    private async Task<Booking> SetupAsync(string guestEmail)
    {
        await SetGuestEmailAsync(guestEmail);
        var booking = _world.Booking;
        _bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        _resources
            .Setup(r => r.ForPropertyAsync(booking.PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(_world.Org.Id, OwnerId));
        return booking;
    }

    private GuestPortalLinkController Controller(IAuthorizationService? authorization = null) =>
        new(
            _bookings.Object,
            _resources.Object,
            authorization ?? HostAuthorizationTestHarness.Create(_world.Org.Id),
            _world.LinkService(),
            _world.SiteLinks,
            NullLogger<GuestPortalLinkController>.Instance,
            _world.Clock,
            new GuestPortalEmailService(_world.Db, _world.LinkService(), _emails, _world.Clock, NullLogger<GuestPortalEmailService>.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(OwnerId, "PropertyOwner"),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

    private static string? Code(ObjectResult result) =>
        Assert.IsType<ProblemDetails>(result.Value).Extensions.TryGetValue("code", out var code) ? code?.ToString() : null;
}
