using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US02: the shape of the endpoints of the write side of the hub and of the composer's helpers: routes, policies, rate limits, the header of the
/// key, and what a controller does before it asks the service (a malformed key is a 400, a caller with no scope a 403, a booking the caller does not reach a
/// 404 with the service never asked).
/// </summary>
public class MessageReplyEndpointsTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid BookingId = Guid.NewGuid();
    private static readonly MessageThreadKey ThreadKey = MessageThreadKey.ForOta(Guid.NewGuid());

    // ---- The shape ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheReply_IsAPostUnderTheThread_ForWhoMayWrite_RateLimited_WithTheKeyInAHeader()
    {
        var reply = typeof(MessageThreadsController).GetMethod(nameof(MessageThreadsController.Reply))!;

        Assert.Equal("threads/{threadKey}/reply", reply.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingWrite, Assert.Single(reply.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Equal(RateLimitPolicies.HostMessageSend, reply.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.NotNull(reply.GetCustomAttribute<RequestSizeLimitAttribute>());
        var header = reply.GetParameters().Single(p => p.Name == "idempotencyKey").GetCustomAttribute<FromHeaderAttribute>();
        Assert.Equal("Idempotency-Key", header!.Name);
        Assert.Empty(reply.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void TheEndpointsOfTheBooking_AreForWhoMayRead_AndForWhoMayWrite_AndAreRateLimitedWhenTheyWrite()
    {
        var controller = typeof(BookingGuestContactController);
        Assert.Equal("api/bookings/{id:guid}", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());

        var options = controller.GetMethod(nameof(BookingGuestContactController.ContactOptions))!;
        Assert.Equal("contact-options", options.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(options.GetCustomAttributes<AuthorizeAttribute>());

        var start = controller.GetMethod(nameof(BookingGuestContactController.StartConversation))!;
        Assert.Equal("messages/new", start.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingWrite, Assert.Single(start.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Equal(RateLimitPolicies.HostMessageSend, start.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Equal("Idempotency-Key", start.GetParameters().Single(p => p.Name == "idempotencyKey").GetCustomAttribute<FromHeaderAttribute>()!.Name);
    }

    [Fact]
    public void TheTemplates_AreReadByWhoMayRead_ChangedByWhoMayWrite_AndTheTranslationIsUnderTheLimitsOfTheAi()
    {
        var controller = typeof(MessageTemplatesController);
        Assert.Equal("api/message-templates", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());

        foreach (var name in new[] { nameof(MessageTemplatesController.Create), nameof(MessageTemplatesController.Update), nameof(MessageTemplatesController.Delete) })
        {
            var write = controller.GetMethod(name)!;
            Assert.Equal(CasazenPolicies.BookingWrite, Assert.Single(write.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        }

        foreach (var name in new[]
                 {
                     nameof(MessageTemplatesController.List),
                     nameof(MessageTemplatesController.Render),
                     nameof(MessageTemplatesController.ChannelTemplates),
                     nameof(MessageTemplatesController.Translate),
                 })
        {
            Assert.Empty(controller.GetMethod(name)!.GetCustomAttributes<AuthorizeAttribute>());
        }

        var channelTemplates = controller.GetMethod(nameof(MessageTemplatesController.ChannelTemplates))!;
        Assert.Equal("~/api/messages/threads/{threadKey}/channel-templates", channelTemplates.GetCustomAttribute<HttpGetAttribute>()!.Template);
        var translate = controller.GetMethod(nameof(MessageTemplatesController.Translate))!;
        Assert.Equal("~/api/messages/translate", translate.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(translate.GetCustomAttribute<AiRateLimitAttribute>());
        Assert.Equal("{id:guid}/render", controller.GetMethod(nameof(MessageTemplatesController.Render))!.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.All(
            controller.GetMethods().Where(m => m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any()),
            m => Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>()));
    }

    // ---- What a controller does before it asks the service -----------------------------------------------------------------------------

    [Fact]
    public async Task Reply_AMalformedKey_Is400_ACallerWithNoScopeIs403_AndTheServiceIsNotAsked()
    {
        var replies = new Mock<IMessageReplyService>(MockBehavior.Strict);

        var malformed = await Threads(replies.Object, scope: Scope()).Reply("nope", new ReplyRequest { Text = "Ciao" }, "key-1234abcd", CancellationToken.None);
        var forbidden = await Threads(replies.Object, scope: null).Reply(ThreadKey.ToString(), new ReplyRequest { Text = "Ciao" }, "key-1234abcd", CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(malformed.Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(forbidden.Result).StatusCode);
        replies.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OtaMessageDeliveryState.Sent, StatusCodes.Status200OK)]
    [InlineData(OtaMessageDeliveryState.WaitingAuthorization, StatusCodes.Status200OK)]
    [InlineData(OtaMessageDeliveryState.Error, StatusCodes.Status200OK)]
    [InlineData(OtaMessageDeliveryState.ManualVerify, StatusCodes.Status202Accepted)]
    public async Task Reply_AnAnswerThatIsNotKnownToHaveGone_Is202_TheOthersAre200_WithTheStateInTheBody(OtaMessageDeliveryState state, int expectedStatus)
    {
        var replies = new Mock<IMessageReplyService>();
        replies
            .Setup(r => r.ReplyAsync(It.IsAny<HostScope>(), ThreadKey, "Ciao", "key-1234abcd", "auth0|anna", MessageOrigin.Person, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageReplyResult(MessageContactChannel.Ota, ThreadKey, null, state, false, state == OtaMessageDeliveryState.Error ? "chat_not_licensed" : null));

        var answer = await Threads(replies.Object, Scope()).Reply(ThreadKey.ToString(), new ReplyRequest { Text = "Ciao" }, "key-1234abcd", CancellationToken.None);

        var result = answer.Result switch
        {
            OkObjectResult ok => (Status: StatusCodes.Status200OK, Body: ok.Value),
            ObjectResult other => (Status: other.StatusCode ?? 0, Body: other.Value),
            _ => throw new InvalidOperationException("Unexpected result."),
        };
        Assert.Equal(expectedStatus, result.Status);
        var dto = Assert.IsType<MessageReplyDto>(result.Body);
        Assert.Equal(state, dto.State);
        Assert.Equal(MessageContactChannel.Ota, dto.Channel);
        Assert.Equal(ThreadKey.ToString(), dto.ThreadKey);
        Assert.Equal(state == OtaMessageDeliveryState.Error ? "chat_not_licensed" : null, dto.ErrorCode);
    }

    /// <summary>A booking the caller does not reach is a 404 before anything is asked of the service, for the options and for the first message alike.</summary>
    [Fact]
    public async Task TheBookingEndpoints_ABookingTheCallerDoesNotReach_Is404_AndTheServiceIsNotAsked()
    {
        var replies = new Mock<IMessageReplyService>(MockBehavior.Strict);

        var denied = Booking(replies.Object, resource: new HostResource(OrgId, "auth0|owner", Guid.NewGuid()), allowed: false);
        var missing = Booking(replies.Object, resource: null, allowed: true);

        Assert.IsType<NotFoundResult>((await denied.ContactOptions(BookingId, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await denied.StartConversation(BookingId, new ReplyRequest { Text = "Ciao" }, "key-1234abcd", CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await missing.ContactOptions(BookingId, CancellationToken.None)).Result);
        replies.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheBookingEndpoints_AnAuthorizedCaller_GetsTheOptionsAndTheOutcome_WithNoAddressInThem()
    {
        var replies = new Mock<IMessageReplyService>();
        replies
            .Setup(r => r.ChooseChannelAsync(It.IsAny<HostScope>(), BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageContactOptions(MessageContactChannel.Email, null, null, true, false));
        replies
            .Setup(r => r.StartConversationAsync(It.IsAny<HostScope>(), BookingId, "Ciao", "key-1234abcd", "auth0|anna", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageReplyResult(MessageContactChannel.Email, null, null, OtaMessageDeliveryState.Sent, false, null));
        var controller = Booking(replies.Object, new HostResource(OrgId, "auth0|owner", Guid.NewGuid()), allowed: true);

        var options = Assert.IsType<OkObjectResult>((await controller.ContactOptions(BookingId, CancellationToken.None)).Result);
        var started = Assert.IsType<OkObjectResult>((await controller.StartConversation(BookingId, new ReplyRequest { Text = "Ciao" }, "key-1234abcd", CancellationToken.None)).Result);

        var optionsDto = Assert.IsType<MessageContactOptionsDto>(options.Value);
        Assert.Equal((MessageContactChannel.Email, true, false), (optionsDto.Channel, optionsDto.EmailAvailable, optionsDto.PhoneAvailable));
        Assert.Equal(OtaMessageDeliveryState.Sent, Assert.IsType<MessageReplyDto>(started.Value).State);
        // The shape of the answers has no member that could hold an address or a number.
        Assert.DoesNotContain(typeof(MessageContactOptionsDto).GetProperties(), p => p.PropertyType == typeof(string) && p.Name is "Email" or "Phone" or "Address");
        Assert.DoesNotContain(typeof(MessageReplyDto).GetProperties(), p => p.Name is "Email" or "Phone" or "Address" or "To");
    }

    // ---- The rig ------------------------------------------------------------------------------------------------------------------------

    private static HostScope Scope() => new(OrgId);

    private static MessageThreadsController Threads(IMessageReplyService replies, HostScope? scope)
    {
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
        return new MessageThreadsController(Mock.Of<IMessageHubService>(), orgs.Object, scopes.Object, Mock.Of<IAuthorizationService>(), replies).WithUser();
    }

    private static BookingGuestContactController Booking(IMessageReplyService replies, HostResource? resource, bool allowed)
    {
        var lookup = new Mock<IHostResourceLookup>();
        lookup.Setup(l => l.ForBookingAsync(BookingId, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scope());
        return new BookingGuestContactController(replies, lookup.Object, authorization.Object, orgs.Object, scopes.Object).WithUser();
    }
}
