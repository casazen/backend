using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E05/F07/US01/T02, T03 (NF-71): the endpoints of the scheduled messages. The controller is behind its flag (404 before authentication while it is off), each action asks the policy it needs, a booking or a
/// message that the caller does not reach, or that it may not write, is a 404 and the service is never asked, and the answer that carries the words of a message is private.
/// </summary>
public class ScheduledMessagesControllerTests
{
    private const string UserId = "auth0|owner_messages";
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid BookingId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private readonly Mock<IScheduledMessageService> _messages = new();
    private readonly Mock<IHostResourceLookup> _resources = new();
    private readonly Mock<IOrgContextResolver> _orgs = new();

    private static ScheduledMessageView ViewOf(ScheduledMessageStatus status = ScheduledMessageStatus.Scheduled) => new(
        MessageId,
        BookingId,
        Guid.NewGuid(),
        "Messaggio di arrivo (TEST)",
        0,
        new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc),
        "Europe/Rome",
        status,
        null,
        null,
        false,
        false,
        null,
        null,
        MessageContactChannel.Direct,
        null,
        "Ciao Giulia! Check-in: [check_in_link] (TEST)",
        [],
        true,
        true,
        true);

    private ScheduledMessagesController CreateController(Func<string, string, bool>? hasPermission = null, string propertyOwner = UserId, bool bookingExists = true)
    {
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _resources
            .Setup(r => r.ForBookingAsync(BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bookingExists ? new HostResource(OrgId, propertyOwner) : null);
        _messages.Setup(m => m.FindBookingIdAsync(It.IsAny<HostScope>(), MessageId, It.IsAny<CancellationToken>())).ReturnsAsync(BookingId);
        return new ScheduledMessagesController(
            _messages.Object,
            _resources.Object,
            HostAuthorizationTestHarness.Create(OrgId, hasPermission),
            _orgs.Object,
            HostAuthorizationTestHarness.ScopeResolver())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(UserId, "PropertyOwner"),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    [Fact]
    public void TheController_IsBehindItsFlag_AndEveryAnswerIsPrivate_AndEachActionAsksItsPolicy()
    {
        var type = typeof(ScheduledMessagesController);

        Assert.Equal(FeatureFlags.ScheduledMessages, Assert.Single(type.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal(CasazenPolicies.BookingRead, type.GetMethod(nameof(ScheduledMessagesController.List))!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        foreach (var write in new[] { nameof(ScheduledMessagesController.Edit), nameof(ScheduledMessagesController.Skip), nameof(ScheduledMessagesController.SendNow) })
            Assert.Equal(CasazenPolicies.BookingWrite, type.GetMethod(write)!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);

        // «Send now» writes to a guest: it has the limit of the other sends of the host.
        Assert.Equal(RateLimitPolicies.HostMessageSend, type.GetMethod(nameof(ScheduledMessagesController.SendNow))!.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
    }

    [Fact]
    public async Task TheTimeline_IsReadByWhoReachesTheBooking_AsDtos_WithNoLinkAndNoCode()
    {
        _messages.Setup(m => m.ListForBookingAsync(It.IsAny<HostScope>(), BookingId, It.IsAny<CancellationToken>())).ReturnsAsync([ViewOf()]);
        var controller = CreateController();

        var result = await controller.List(BookingId, CancellationToken.None);

        var dto = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ScheduledMessageDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal((MessageId, ScheduledMessageStatus.Scheduled, MessageContactChannel.Direct), (dto.Id, dto.Status, dto.Channel));
        Assert.Contains("[check_in_link]", dto.Text, StringComparison.Ordinal);
        _messages.Verify(m => m.ListForBookingAsync(It.Is<HostScope>(s => s.OrgId == OrgId), BookingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: permission rule. A booking that does not exist for the caller (another org's, or one of a property it does not reach) is a 404 on the timeline, and the service that reads the messages is never asked.
    /// </summary>
    [Theory]
    [InlineData(false, UserId)]
    [InlineData(true, "auth0|somebody_else")]
    public async Task TheTimeline_OfABookingTheCallerDoesNotReach_Is404_AndNothingIsRead(bool bookingExists, string propertyOwner)
    {
        var controller = CreateController(propertyOwner: propertyOwner, bookingExists: bookingExists);

        var result = await controller.List(BookingId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _messages.Verify(m => m.ListForBookingAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// PROOF TEST: permission rule. A message the caller does not reach (the service finds no booking for it in its scope) or may not write (its role lacks <c>booking.write</c>) is a 404 for the edit, the skip and
    /// «Send now», and the service that changes a message is never asked.
    /// </summary>
    [Theory]
    [InlineData("out-of-reach")]
    [InlineData("cannot-write")]
    public async Task EditSkipAndSendNow_OfAMessageTheCallerCannotChange_Are404_AndTheServiceIsNeverAsked(string why)
    {
        var controller = CreateController(why == "cannot-write" ? (_, permission) => permission != "booking.write" : null);
        if (why == "out-of-reach")
            _messages.Setup(m => m.FindBookingIdAsync(It.IsAny<HostScope>(), MessageId, It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        Assert.IsType<NotFoundResult>((await controller.Edit(MessageId, new EditScheduledMessageRequest { Text = "Altro (TEST)" }, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.Skip(MessageId, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.SendNow(MessageId, CancellationToken.None)).Result);

        _messages.Verify(m => m.EditAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _messages.Verify(m => m.SkipAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _messages.Verify(m => m.SendNowAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheHostWhoMayWrite_EditsSkipsAndSendsNow_AsItself_InItsOwnScope()
    {
        _messages.Setup(m => m.EditAsync(It.IsAny<HostScope>(), MessageId, "Solo per te (TEST)", UserId, It.IsAny<CancellationToken>())).ReturnsAsync(ViewOf());
        _messages.Setup(m => m.SkipAsync(It.IsAny<HostScope>(), MessageId, UserId, It.IsAny<CancellationToken>())).ReturnsAsync(ViewOf(ScheduledMessageStatus.Skipped));
        _messages.Setup(m => m.SendNowAsync(It.IsAny<HostScope>(), MessageId, UserId, It.IsAny<CancellationToken>())).ReturnsAsync(ViewOf(ScheduledMessageStatus.Sent));
        var controller = CreateController();

        var edited = Assert.IsType<ScheduledMessageDto>(Assert.IsType<OkObjectResult>((await controller.Edit(MessageId, new EditScheduledMessageRequest { Text = "Solo per te (TEST)" }, CancellationToken.None)).Result).Value);
        var skipped = Assert.IsType<ScheduledMessageDto>(Assert.IsType<OkObjectResult>((await controller.Skip(MessageId, CancellationToken.None)).Result).Value);
        var sent = Assert.IsType<ScheduledMessageDto>(Assert.IsType<OkObjectResult>((await controller.SendNow(MessageId, CancellationToken.None)).Result).Value);

        Assert.Equal(ScheduledMessageStatus.Scheduled, edited.Status);
        Assert.Equal(ScheduledMessageStatus.Skipped, skipped.Status);
        Assert.Equal(ScheduledMessageStatus.Sent, sent.Status);
        _messages.Verify(m => m.SendNowAsync(It.Is<HostScope>(s => s.OrgId == OrgId), MessageId, UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TheAnswerThatCarriesTheWordsOfAMessage_IsNeverStored()
    {
        var response = new DefaultHttpContext().Response;

        PrivateAnswerAttribute.Apply(response);

        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.Equal("noindex", response.Headers["X-Robots-Tag"].ToString());
    }
}
