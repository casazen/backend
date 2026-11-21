using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Email;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.GuestPortal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

/// <summary>
/// E50/F01/US03/T03 (NF-152, section 5.1): the host sends «Prepara il tuo arrivo» again from the booking (<c>POST api/bookings/{bookingId}/guest-portal/resend</c>). It goes to the e-mail address
/// of the booking's guest and to no other (the request carries none), only for a booking the caller may write, never for a booking of another org, and with the link the link service made.
/// </summary>
public sealed class GuestPortalResendTests : IDisposable
{
    private const string OwnerId = "auth0|owner_portal";
    private const string GuestAddress = "anna.ospite@guestmail.it";

    private readonly GuestPortalTestWorld _world = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly Mock<IBookingService> _bookings = new();
    private readonly Mock<IHostResourceLookup> _resources = new();

    public void Dispose() => _world.Dispose();

    /// <summary>
    /// PROOF TEST (permission and privacy: who can be written to, and for whom): an OTA booking whose guest has no e-mail address answers 409 <c>guest_portal_no_guest_email</c> and nothing is
    /// queued and no link is made (the host copies the link); a booking of another org is a 404 whatever its id, and nothing is queued; and with an address the e-mail goes to that address only.
    /// </summary>
    [Fact]
    public async Task Resend_OtaWithoutEmail_Returns409_AnotherOrg_Returns404()
    {
        var booking = await SetupAsync(guestEmail: string.Empty);
        var controller = Controller();

        // An OTA booking (the stay of the test world is an Airbnb one) with no address for its guest.
        var noAddress = await controller.Resend(booking.Id, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(noAddress.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(GuestPortalLinkController.NoGuestEmailCode, Code(problem));
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(await _world.Db.GuestPortalLinks.AsNoTracking().ToListAsync());

        // A booking of another org: the tenant filter hides it, so it is a 404 and nothing is queued.
        var foreign = Guid.NewGuid();
        _bookings.Setup(b => b.GetBookingAsync(foreign)).ReturnsAsync((Booking?)null);

        var notFound = await controller.Resend(foreign, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(notFound.Result).StatusCode);
        Assert.Empty(_emails.Snapshot());

        // With an address the e-mail is queued, to that address and to no other.
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

        // The link in the e-mail is the one the link service made: one active link, the one the host copies.
        var active = await _world.LinkService().GetActiveAsync(_world.Org.Id, booking.Id);
        Assert.Equal(dto.Url, _world.LinkService().BuildUrl(active!));
    }

    /// <summary>The action takes the booking in the address and nothing else: there is no way for a caller to name an address to write to.</summary>
    [Fact]
    public void Resend_TakesNoAddressFromTheCaller()
    {
        var action = typeof(GuestPortalLinkController).GetMethod(nameof(GuestPortalLinkController.Resend))!;

        Assert.Equal(new[] { typeof(Guid), typeof(CancellationToken) }, action.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal("POST", action.GetCustomAttribute<HttpPostAttribute>()!.HttpMethods.Single());
        Assert.Equal("{bookingId:guid}/guest-portal/resend", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingWrite, action.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    /// <summary>A caller who cannot write the booking (booking.write on its property) is refused, and nothing is queued or made.</summary>
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

    /// <summary>A booking that may not have a link (cancelled) is the 409 of the other endpoints, and nothing is queued.</summary>
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

    /// <summary>A queue that does not take the e-mail (no provider) is a 409 with its own code, never a success.</summary>
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

    /// <summary>A guest of the demo or an erased guest has no address to write to either.</summary>
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

    // ---- The data every test runs on ---------------------------------------------------------------------------------------------------

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
