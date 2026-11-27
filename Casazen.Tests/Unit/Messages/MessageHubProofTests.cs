using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US01/T01: the two proofs the brief asks for, on the real service over a database, and the shape of the endpoints. Money, data, permission
/// and privacy rules: a collaborator «Solo alcuni» reaches only the conversations of the houses it was given and never an unassigned one; the address
/// of an attachment on the channel manager never reaches a client, and another org's conversation is a 404.
/// </summary>
public class MessageHubProofTests : IAsyncLifetime
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static MessageThreadKey Ota(OtaMessageThread thread) => MessageThreadKey.ForOta(thread.Id);

    /// <summary>
    /// PROOF TEST: permission rule. A collaborator limited to some properties lists and opens the conversations of the houses it was given, of both
    /// sources, and never one of another house, never one with no property (OCT-04) and never one of another org: not in the list, not by its key, not
    /// by a filter, not in the badges, not to mark it read, not for its files. Whoever reaches every property of the org sees the unassigned one.
    /// </summary>
    [Fact]
    public async Task MessageHub_Collaborator_SeesOnlyGrantedProperties_Test()
    {
        var guestMessages = new Mock<IGuestMessageService>();
        var fetcher = new Mock<IOtaAttachmentFetcher>();
        var service = MessageHubTestKit.Service(_db, guestMessages.Object, attachments: fetcher.Object);
        var collaborator = _world.Scope.Restricted;

        // The list: the three conversations of the granted house, and nothing else.
        var page = await service.ListAsync(collaborator, new MessageHubFilter(), 1, 50);
        Assert.Equal(
            [MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), Ota(_world.GrantedRequest), Ota(_world.GrantedReservation)],
            page.Items.Select(i => i.Key));
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, i => Assert.Equal(_world.Scope.Granted.Id, i.PropertyId));
        Assert.DoesNotContain(page.Items, i => i.Unassigned);

        // A filter cannot widen it: the hidden house, the unassigned channel and the guest of the hidden stay find nothing.
        Assert.Empty((await service.ListAsync(collaborator, new MessageHubFilter(PropertyId: _world.Scope.Hidden.Id), 1, 50)).Items);
        Assert.Empty((await service.ListAsync(collaborator, new MessageHubFilter(Channel: MessageHubChannel.Expedia), 1, 50)).Items);
        Assert.Empty((await service.ListAsync(collaborator, new MessageHubFilter(Query: "pietro"), 1, 50)).Items);
        Assert.Empty((await service.ListAsync(collaborator, new MessageHubFilter(BookingId: _world.HiddenDirectBooking.Id), 1, 50)).Items);

        // The thread: the granted ones open; the hidden one, the unassigned one and the other org's are a 404, whichever the source.
        foreach (var granted in new[] { Ota(_world.GrantedRequest), Ota(_world.GrantedReservation) })
            Assert.Equal(granted, (await service.GetThreadAsync(collaborator, granted, includeAmounts: true)).Key);

        var notFound = new[]
        {
            Ota(_world.HiddenReservation),
            Ota(_world.Unassigned),
            Ota(_world.OtherOrg),
            MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id),
        };
        foreach (var key in notFound)
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => service.GetThreadAsync(collaborator, key, includeAmounts: true));
            Assert.Equal("thread_not_found", error.Code);
            await Assert.ThrowsAsync<NotFoundException>(() => service.MarkReadAsync(collaborator, key, "auth0|collab"));
        }

        // Nothing was marked read, and the existing service was never asked about a stay the collaborator does not reach.
        Assert.Equal(2, (await _db.OtaMessageThreads.AsNoTracking().FirstAsync(t => t.Id == _world.HiddenReservation.Id)).UnreadCount);
        Assert.Equal(1, (await _db.OtaMessageThreads.AsNoTracking().FirstAsync(t => t.Id == _world.Unassigned.Id)).UnreadCount);
        guestMessages.Verify(g => g.GetForHostAsync(_world.HiddenDirectBooking.Id, It.IsAny<CancellationToken>()), Times.Never);
        guestMessages.Verify(g => g.MarkReadAsync(_world.HiddenDirectBooking.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // The files of the conversations it does not reach are as missing as the conversations: the address is not even fetched.
        foreach (var message in new[] { _world.HiddenAttachment, _world.OtherOrgAttachment })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAttachmentAsync(collaborator, message.Id));
            Assert.Equal("message_not_found", error.Code);
        }

        fetcher.Verify(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The badges: the OTA counters are narrowed by the same scope.
        guestMessages.Setup(g => g.ListUnreadForHostAsync(collaborator, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var unread = await service.ListUnreadAsync(collaborator, includeOta: true);
        Assert.Equal([Ota(_world.GrantedRequest)], unread.Select(t => t.Key));

        // Who reaches every property of the org sees the unassigned conversation too, marked as such, and can read it.
        var orgWide = (await service.ListAsync(_world.Scope.OrgWide, new MessageHubFilter(), 1, 50)).Items;
        var unassigned = Assert.Single(orgWide, i => i.Key == Ota(_world.Unassigned));
        Assert.True(unassigned.Unassigned);
        Assert.Equal(Ota(_world.Unassigned), (await service.GetThreadAsync(_world.Scope.OrgWide, Ota(_world.Unassigned), true)).Key);
    }

    /// <summary>
    /// PROOF TEST: permission rule, after a remap. A conversation moved to a house the collaborator was given (the mapping of its listing changed) may
    /// still be linked to a stay at the house it came from, which the collaborator was not given: that stay is in no row of the list, in no filter by
    /// booking, in no search by the name of its guest and in no badge, as it is in no card of the detail. A conversation that was not moved keeps its stay.
    /// </summary>
    [Fact]
    public async Task MessageHub_Collaborator_AfterARemap_SeesNothingOfTheStayOfAHouseItWasNotGiven_Test()
    {
        var thread = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedRequest.Id);
        thread.BookingId = _world.HiddenDirectBooking.Id;
        thread.GuestDisplayName = null;
        await _db.SaveChangesAsync();
        var guestMessages = new Mock<IGuestMessageService>();
        guestMessages.Setup(g => g.ListUnreadForHostAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = MessageHubTestKit.Service(_db, guestMessages.Object);
        var collaborator = _world.Scope.Restricted;
        var key = Ota(_world.GrantedRequest);

        // The list: the conversation is there, at a granted house, and carries nothing of the other stay; the one that was not moved keeps its own.
        var page = await service.ListAsync(collaborator, new MessageHubFilter(), 1, 50);
        var moved = Assert.Single(page.Items, i => i.Key == key);
        Assert.Null(moved.BookingId);
        Assert.Null(moved.BookingCode);
        Assert.Null(moved.GuestName);
        Assert.Null(moved.CheckInDate);
        Assert.Null(moved.CheckOutDate);
        Assert.Equal(_world.GrantedChannelBooking.Id, Assert.Single(page.Items, i => i.Key == Ota(_world.GrantedReservation)).BookingId);

        // That stay cannot be found by its booking, nor by the name of its guest.
        Assert.Empty((await service.ListAsync(collaborator, new MessageHubFilter(BookingId: _world.HiddenDirectBooking.Id), 1, 50)).Items);
        Assert.Empty((await service.ListAsync(collaborator, new MessageHubFilter(Query: "gallo"), 1, 50)).Items);

        // The badge names no booking, and the detail has no card and no customer.
        var unread = Assert.Single(await service.ListUnreadAsync(collaborator, includeOta: true), t => t.Key == key);
        Assert.Null(unread.BookingId);
        var view = await service.GetThreadAsync(collaborator, key, includeAmounts: true);
        Assert.Null(view.Booking);
        Assert.Null(view.CustomerGuestId);
    }

    /// <summary>
    /// PROOF TEST: privacy and tenant rule. The address the channel manager gave for a file is fetched by the server and appears nowhere in what a
    /// client gets: not in the answer of the attachment endpoint, not in its headers, not in the thread detail nor the list, not in the error of a file
    /// that expired. A message of another org answers 404 and the address is not fetched. The file is served private, not cached, not sniffed, and
    /// anything but an image is a download.
    /// </summary>
    [Fact]
    public async Task MessageHub_Attachment_NeverExposesOctorateUrl_Test()
    {
        var fetcher = new Mock<IOtaAttachmentFetcher>();
        fetcher.Setup(f => f.FetchAsync(MessageHubWorld.SecretAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtaAttachmentContent(Photo, OtaAttachmentRules.Jpeg));
        var service = MessageHubTestKit.Service(_db, attachments: fetcher.Object);
        var controller = Controller(service, _world.Scope.OrgWide);

        // The attachment endpoint: the bytes, the type, the headers of a private download, and not a trace of the address.
        var result = await controller.Attachment(_world.GrantedAttachment.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(Photo, file.FileContents);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.True(string.IsNullOrEmpty(file.FileDownloadName), "an image is shown, not downloaded");
        var headers = controller.Response.Headers;
        Assert.Equal("private, no-store", headers.CacheControl.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Contains("sandbox", headers["Content-Security-Policy"].ToString(), StringComparison.Ordinal);
        Assert.All(headers, header => AssertNoAddress(header.Key + ": " + header.Value));
        AssertNoAddress(file.ContentType);
        AssertNoAddress(System.Text.Encoding.Latin1.GetString(file.FileContents));
        fetcher.Verify(f => f.FetchAsync(MessageHubWorld.SecretAddress, It.IsAny<CancellationToken>()), Times.Once);

        // Anything that is not an image is offered as a download.
        fetcher.Setup(f => f.FetchAsync(MessageHubWorld.SecretAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtaAttachmentContent([1, 2], OtaAttachmentRules.OctetStream));
        var download = Assert.IsType<FileContentResult>(await controller.Attachment(_world.GrantedAttachment.Id, CancellationToken.None));
        Assert.Equal("application/octet-stream", download.ContentType);
        Assert.Equal("attachment", download.FileDownloadName);

        // The thread and the list say the message has a file and of what type, never where it is.
        var thread = await controller.Get(Ota(_world.GrantedReservation).ToString(), CancellationToken.None);
        var threadJson = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(thread.Result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"contentType\":\"image/jpeg\"", threadJson, StringComparison.Ordinal);
        AssertNoAddress(threadJson);
        var page = await new HostMessagesController(Mock.Of<IGuestMessageService>(), Orgs(), Scopes(_world.Scope.OrgWide), service)
            .WithUser()
            .List(null, null, includeOta: true);
        AssertNoAddress(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(page.Result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        // A file that expired: a 404 with its own code, and the error carries nothing of the address.
        fetcher.Setup(f => f.FetchAsync(MessageHubWorld.SecretAddress, It.IsAny<CancellationToken>())).ReturnsAsync((OtaAttachmentContent?)null);
        var expired = await Assert.ThrowsAsync<NotFoundException>(() => controller.Attachment(_world.GrantedAttachment.Id, CancellationToken.None));
        Assert.Equal("attachment_unavailable", expired.Code);
        Assert.Equal("AttachmentUnavailable", expired.MessageKey);
        AssertNoAddress(expired.Message);
        AssertNoAddress(expired.Code ?? string.Empty);
        AssertNoAddress(expired.MessageKey ?? string.Empty);

        // A message of another org is a 404 as if it did not exist, and its address is never fetched.
        fetcher.Invocations.Clear();
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => controller.Attachment(_world.OtherOrgAttachment.Id, CancellationToken.None));
        Assert.Equal("message_not_found", foreign.Code);
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => controller.Attachment(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("message_not_found", missing.Code);
        fetcher.Verify(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Attachment_OfAMessageWithoutAFile_IsUnavailable_AndNothingIsFetched()
    {
        var fetcher = new Mock<IOtaAttachmentFetcher>();
        var service = MessageHubTestKit.Service(_db, attachments: fetcher.Object);
        var textOnly = await _db.OtaMessages.AsNoTracking().FirstAsync(m => m.ThreadId == _world.GrantedRequest.Id);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAttachmentAsync(_world.Scope.OrgWide, textOnly.Id));

        Assert.Equal("attachment_unavailable", error.Code);
        fetcher.Verify(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- The endpoints --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheEndpoints_AreUnderApiMessages_WithThePoliciesOfTheBrief_AndPrivateAnswers()
    {
        var controller = typeof(MessageThreadsController);
        Assert.Equal("api/messages", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());

        var get = controller.GetMethod(nameof(MessageThreadsController.Get))!;
        Assert.Equal("threads/{threadKey}", get.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(get.GetCustomAttributes<AuthorizeAttribute>());

        var read = controller.GetMethod(nameof(MessageThreadsController.MarkRead))!;
        Assert.Equal("threads/{threadKey}/read", read.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingWrite, Assert.Single(read.GetCustomAttributes<AuthorizeAttribute>()).Policy);

        var attachment = controller.GetMethod(nameof(MessageThreadsController.Attachment))!;
        Assert.Equal("{messageId:guid}/attachment", attachment.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(attachment.GetCustomAttributes<AuthorizeAttribute>());
        Assert.All(controller.GetMethods().Where(m => m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any()), m =>
            Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ota:")]
    [InlineData("42")]
    [InlineData("direct:not-a-guid")]
    [InlineData("ota:00000000-0000-0000-0000-000000000000")]
    public async Task AMalformedKey_IsA400_WithItsCode_AndTheServiceIsNotAsked(string key)
    {
        var hub = new Mock<IMessageHubService>(MockBehavior.Strict);
        var controller = Controller(hub.Object, _world.Scope.OrgWide);

        var get = await controller.Get(key, CancellationToken.None);
        var read = await controller.MarkRead(key, CancellationToken.None);

        foreach (var result in new[] { get.Result, read.Result })
        {
            var problem = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
            Assert.Equal("thread_key_invalid", Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(problem.Value).Extensions["code"]);
        }

        hub.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoScope_IsA403_AndNothingIsRead()
    {
        var hub = new Mock<IMessageHubService>(MockBehavior.Strict);
        var orgs = Orgs();
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync((HostScope?)null);
        var controller = new MessageThreadsController(hub.Object, orgs, scopes.Object, AllowAll()).WithUser();

        var get = await controller.Get(Ota(_world.GrantedRequest).ToString(), CancellationToken.None);
        var attachment = await controller.Attachment(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(get.Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(attachment).StatusCode);
        hub.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheMoneyOfTheCard_IsAskedFor_OnlyWhenTheCallerMayReadPayments()
    {
        var hub = new Mock<IMessageHubService>();
        hub.Setup(h => h.GetThreadAsync(It.IsAny<HostScope>(), It.IsAny<MessageThreadKey>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope _, MessageThreadKey key, bool _, CancellationToken _) => View(key));
        var key = Ota(_world.GrantedReservation);

        await Controller(hub.Object, _world.Scope.OrgWide, canReadPayments: true).Get(key.ToString(), CancellationToken.None);
        await Controller(hub.Object, _world.Scope.OrgWide, canReadPayments: false).Get(key.ToString(), CancellationToken.None);

        hub.Verify(h => h.GetThreadAsync(It.IsAny<HostScope>(), key, true, It.IsAny<CancellationToken>()), Times.Once);
        hub.Verify(h => h.GetThreadAsync(It.IsAny<HostScope>(), key, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MessageThreadView View(MessageThreadKey key) => new(
        key, MessageThreadSource.Ota, MessageHubChannel.Booking, MessageHubThreadKind.Reservation, MessageHubThreadState.Open,
        null, null, true, null, 0, false, false, null, null, MessageThreadActions.Blocked("mapping_not_confirmed"), [], false);

    private static void AssertNoAddress(string text)
    {
        Assert.DoesNotContain("SECRET", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("octorate.example", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attachments/", text, StringComparison.OrdinalIgnoreCase);
    }

    private static IOrgContextResolver Orgs()
    {
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        return orgs.Object;
    }

    private static IHostScopeResolver Scopes(HostScope scope)
    {
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return scopes.Object;
    }

    private static IAuthorizationService AllowAll() => Authorization(canReadPayments: true);

    private static IAuthorizationService Authorization(bool canReadPayments)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.PaymentRead))
            .ReturnsAsync(canReadPayments ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization.Object;
    }

    private MessageThreadsController Controller(IMessageHubService hub, HostScope scope, bool canReadPayments = true) =>
        new MessageThreadsController(hub, Orgs(), Scopes(scope), Authorization(canReadPayments)).WithUser();
}

internal static class ControllerTestExtensions
{
    /// <summary>Gives a controller a signed-in caller and the services the error answers need (the localizer of the messages).</summary>
    public static T WithUser<T>(this T controller)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|anna")], "test")),
                RequestServices = new ServiceCollection()
                    .AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer())
                    .BuildServiceProvider(),
            },
        };
        return controller;
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
