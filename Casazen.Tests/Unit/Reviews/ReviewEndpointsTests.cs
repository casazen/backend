using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The endpoints of the reviews that a host reaches (E05/F06/US01, NF-69), proved where their permission rules live: the shape of each controller (route, policy, flag, rate limit,
/// private answer), and what it does before it asks the service. The reply and the host's own review are authorized by the controller, as a row of the property of the review: the real
/// handler of the resource decides (the org of the caller, the permission, the reach of a collaborator «Solo alcuni»), over the real services and a database, so a review that is out of
/// the reach, of another org or not there is the same 404, and nothing reaches the channel manager. The siblings of these endpoints are pinned the same way
/// (<c>MessageHubProofTests</c>, <c>MessageReplyEndpointsTests</c>, <c>AssistantDraftsControllerTests</c>).
/// </summary>
public class ReviewEndpointsTests
{
    private const string Anna = "auth0|anna";
    private const string KeyOne = "reply-key-0001";
    private const string ReplyText = "Grazie di cuore, speriamo di rivedervi presto!";
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly MessageThreadKey DraftKey = MessageThreadKey.ForOtaReview(Guid.NewGuid());

    // ─── The shape ───

    [Fact]
    public void OtaReviewsController_IsForWhoMayRead_TheTwoSendsForWhoMayWrite_RateLimited_WithTheKeyInAHeader_AndPrivate()
    {
        var type = typeof(OtaReviewsController);
        Assert.Equal("api/reviews/ota", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal(
            [nameof(OtaReviewsController.Get), nameof(OtaReviewsController.Reply), nameof(OtaReviewsController.SendHostReview)],
            ActionsOf(type).Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.All(ActionsOf(type), action => Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>()));

        var get = type.GetMethod(nameof(OtaReviewsController.Get))!;
        Assert.Equal("{id:guid}", get.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(get.GetCustomAttributes<AuthorizeAttribute>());

        foreach (var (name, template) in new[] { (nameof(OtaReviewsController.Reply), "{id:guid}/reply"), (nameof(OtaReviewsController.SendHostReview), "{id:guid}/host-review") })
        {
            var send = type.GetMethod(name)!;
            Assert.Equal(template, send.GetCustomAttribute<HttpPostAttribute>()!.Template);
            Assert.Equal(CasazenPolicies.BookingWrite, Assert.Single(send.GetCustomAttributes<AuthorizeAttribute>()).Policy);
            Assert.Equal(RateLimitPolicies.HostMessageSend, send.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
            Assert.NotNull(send.GetCustomAttribute<RequestSizeLimitAttribute>());
            Assert.Equal("Idempotency-Key", send.GetParameters().Single(p => p.Name == "idempotencyKey").GetCustomAttribute<FromHeaderAttribute>()!.Name);
        }
    }

    [Fact]
    public void BookingReviewsController_IsForWhoMayRead_ItsReadAndItsRefreshIncluded_AndPrivate()
    {
        var type = typeof(BookingReviewsController);
        Assert.Equal("api/bookings/{id:guid}/reviews", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal(
            [nameof(BookingReviewsController.Get), nameof(BookingReviewsController.Refresh)],
            ActionsOf(type).Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.All(ActionsOf(type), action => Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>()));
        Assert.All(ActionsOf(type), action => Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>()));

        Assert.NotNull(type.GetMethod(nameof(BookingReviewsController.Get))!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("refresh", type.GetMethod(nameof(BookingReviewsController.Refresh))!.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    [Fact]
    public void ReviewDraftsController_IsBehindTheAssistantFlag_Private_ForWhoMayWrite_AndUnderTheLimitsOfTheAi()
    {
        var type = typeof(ReviewDraftsController);
        Assert.Equal("api/reviews", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(FeatureFlags.MessageAssistant, Assert.Single(type.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal([nameof(ReviewDraftsController.Draft)], ActionsOf(type).Select(a => a.Name));
        Assert.All(ActionsOf(type), action => Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>()));

        var draft = type.GetMethod(nameof(ReviewDraftsController.Draft))!;
        Assert.Equal("{reviewKey}/draft", draft.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingWrite, Assert.Single(draft.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(draft.GetCustomAttribute<AiRateLimitAttribute>());
    }

    [Fact]
    public void TheDetailOfAReviewOfTheHub_IsAGetUnderTheReviews_ForWhoMayRead_AndNeverAnonymous()
    {
        var type = typeof(MessageThreadsController);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());

        var detail = type.GetMethod(nameof(MessageThreadsController.GetReview))!;
        Assert.Equal("reviews/{threadKey}", detail.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(detail.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Empty(detail.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    /// <summary>The endpoint of the drafts answers like a route that does not exist, before authentication, while the assistant is off: the flag of the controller is the one the middleware reads.</summary>
    [Fact]
    public async Task TheDraftEndpoint_AnswersLikeARouteThatDoesNotExist_WhileTheAssistantIsOff_AndRunsWhenItIsOn()
    {
        var flags = new TestFeatureFlags();
        var reached = 0;
        var middleware = new FeatureGateMiddleware(
            _ =>
            {
                reached++;
                return Task.CompletedTask;
            },
            flags,
            NullLogger<FeatureGateMiddleware>.Instance);
        var gates = typeof(ReviewDraftsController).GetCustomAttributes<FeatureGateAttribute>().Cast<object>().ToArray();

        var off = new DefaultHttpContext();
        off.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(gates), "POST api/reviews/{reviewKey}/draft"));
        await middleware.InvokeAsync(off);

        flags.Set(FeatureFlags.MessageAssistant, true);
        var on = new DefaultHttpContext();
        on.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(gates), "POST api/reviews/{reviewKey}/draft"));
        await middleware.InvokeAsync(on);

        Assert.Equal(StatusCodes.Status404NotFound, off.Response.StatusCode);
        Assert.Equal(1, reached);
        Assert.Equal(StatusCodes.Status200OK, on.Response.StatusCode);
    }

    // ─── What a controller does before it asks the service ───

    [Fact]
    public async Task TheReviewEndpoints_ACallerWithNoScopeIs403_AMalformedKeyIs400_AndNoServiceIsAsked()
    {
        var reviews = new Mock<IOtaReviewService>(MockBehavior.Strict);
        var hub = new Mock<IMessageHubService>(MockBehavior.Strict);
        var drafter = new Mock<IReviewReplyDrafter>(MockBehavior.Strict);
        var lookup = new Mock<IHostResourceLookup>(MockBehavior.Strict);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        var noScope = Scopes(null);
        var withScope = Scopes(new HostScope(OrgId));

        var review = new OtaReviewsController(reviews.Object, Orgs(), noScope, lookup.Object, authorization.Object).WithCaller(HostAuthorizationTestHarness.User(Anna));
        var booking = new BookingReviewsController(reviews.Object, Orgs(), noScope, lookup.Object, authorization.Object).WithCaller(HostAuthorizationTestHarness.User(Anna));
        var detail = new MessageThreadsController(hub.Object, Orgs(), noScope, authorization.Object).WithCaller(HostAuthorizationTestHarness.User(Anna));
        var drafts = new ReviewDraftsController(drafter.Object, Orgs(), noScope).WithCaller(HostAuthorizationTestHarness.User(Anna));
        var detailWithScope = new MessageThreadsController(hub.Object, Orgs(), withScope, authorization.Object).WithCaller(HostAuthorizationTestHarness.User(Anna));
        var draftsWithScope = new ReviewDraftsController(drafter.Object, Orgs(), withScope).WithCaller(HostAuthorizationTestHarness.User(Anna));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>((await review.Get(Guid.NewGuid(), CancellationToken.None)).Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>((await booking.Get(Guid.NewGuid(), CancellationToken.None)).Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>((await detail.GetReview(DraftKey.ToString(), CancellationToken.None)).Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>((await drafts.Draft(DraftKey.ToString(), CancellationToken.None)).Result).StatusCode);

        // A key that is not one is a 400 with its code, and the hub and the drafter never hear of it.
        foreach (var key in new[] { string.Empty, "nope", "review:", "otareview:not-a-guid", "otareview:00000000-0000-0000-0000-000000000000" })
        {
            var malformedDetail = Assert.IsType<ObjectResult>((await detailWithScope.GetReview(key, CancellationToken.None)).Result);
            var malformedDraft = Assert.IsType<ObjectResult>((await draftsWithScope.Draft(key, CancellationToken.None)).Result);
            Assert.Equal(StatusCodes.Status400BadRequest, malformedDetail.StatusCode);
            Assert.Equal(StatusCodes.Status400BadRequest, malformedDraft.StatusCode);
            Assert.Equal(MessageThreadKey.InvalidCode, CodeOf(malformedDetail));
            Assert.Equal(MessageThreadKey.InvalidCode, CodeOf(malformedDraft));
        }

        reviews.VerifyNoOtherCalls();
        hub.VerifyNoOtherCalls();
        drafter.VerifyNoOtherCalls();
    }

    // ─── The reach of a collaborator «Solo alcuni», with the real handler of the resource ───

    /// <summary>
    /// PROOF TEST: permission rule (NF-69, T02). A collaborator «Solo alcuni» reads, answers and reviews the reviews of the house it was given, and for any other review (a house it was not
    /// given, another org, one that is not there) the answer is the same 404 as for a missing one, for the read and for the two sends alike, with nothing asked of the channel manager.
    /// </summary>
    [Fact]
    public async Task OtaReviewsController_ACollaboratorSoloAlcuni_ReadsAndAnswersTheReviewsOfItsHouseOnly_TheOthersAreA404_AndNothingLeaves()
    {
        var s = await SeedAsync();
        s.H.ChannelHasReview(s.H.Other, "Un segreto dell'altra organizzazione");
        var other = await s.H.SyncAsync(s.H.Other);
        s.H.Client.Invocations.Clear();
        await using var rig = Collaborator(s.H);
        var controller = rig.Reviews();

        foreach (var id in new[] { s.Hidden.Id, other.Id, Guid.NewGuid() })
        {
            Assert.IsType<NotFoundResult>((await controller.Get(id, CancellationToken.None)).Result);
            Assert.IsType<NotFoundResult>((await controller.Reply(id, new ReplyOtaReviewRequest { Text = ReplyText }, KeyOne, CancellationToken.None)).Result);
            Assert.IsType<NotFoundResult>((await controller.SendHostReview(id, GoodReview(), KeyOne, CancellationToken.None)).Result);
        }

        NothingWasSent(s.H);
        var untouched = await s.H.ReviewOfAsync(s.H.Hidden);
        Assert.Equal(OtaReviewActionState.None, untouched.HostReplyState);
        Assert.Equal(OtaReviewActionState.None, untouched.HostReviewState);
        Assert.Null(untouched.HostReplyText);

        // The house it was given: the review is read, answered once and the host's own review is written once.
        var read = Assert.IsType<OkObjectResult>((await controller.Get(s.Granted.Id, CancellationToken.None)).Result);
        Assert.Equal(s.Granted.Id, Assert.IsType<OtaReviewDto>(read.Value).Id);
        var replied = Assert.IsType<OkObjectResult>((await controller.Reply(s.Granted.Id, new ReplyOtaReviewRequest { Text = ReplyText }, KeyOne, CancellationToken.None)).Result);
        Assert.Equal(OtaReviewActionState.Sent, Assert.IsType<OtaReviewActionDto>(replied.Value).State);
        var reviewed = Assert.IsType<OkObjectResult>((await controller.SendHostReview(s.Granted.Id, GoodReview(), KeyOne, CancellationToken.None)).Result);
        Assert.Equal(OtaReviewActionState.Sent, Assert.IsType<OtaReviewActionDto>(reviewed.Value).State);
        s.H.Client.Verify(c => c.SendHostReplyAsync(OtaReviewHarness.GrantedAccommodation, OtaReviewHarness.GrantedRefer, ReplyText, It.IsAny<CancellationToken>()), Times.Once);
        s.H.Client.Verify(c => c.SendHostReviewAsync(OtaReviewHarness.GrantedAccommodation, OtaReviewHarness.GrantedRefer, It.IsAny<OctorateHostReviewCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // The owner of the org reaches the review of the other house: the setup is sound.
        await using var owner = Owner(s.H);
        Assert.IsType<OkObjectResult>((await owner.Reviews().Get(s.Hidden.Id, CancellationToken.None)).Result);
    }

    /// <summary>Permission rule: the two sends need <c>booking.write</c> on the row, and a caller who may only read reads and sends nothing (the 404 of a row it may not write).</summary>
    [Fact]
    public async Task OtaReviewsController_ACallerWhoMayReadButNotWrite_ReadsTheReview_AndTheTwoSendsAreRefused_WithNothingSent()
    {
        var s = await SeedAsync();
        s.H.Client.Invocations.Clear();
        await using var rig = Owner(s.H, hasPermission: (_, permission) => permission != BookingOperations.Write.PermissionKey);
        var controller = rig.Reviews();

        Assert.IsType<OkObjectResult>((await controller.Get(s.Granted.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.Reply(s.Granted.Id, new ReplyOtaReviewRequest { Text = ReplyText }, KeyOne, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.SendHostReview(s.Granted.Id, GoodReview(), KeyOne, CancellationToken.None)).Result);

        NothingWasSent(s.H);
        Assert.Equal(OtaReviewActionState.None, (await s.H.ReviewOfAsync(s.H.Granted)).HostReplyState);
    }

    /// <summary>The answer of a send: 200 with its state, 202 when the outcome is not known (checked on the channel, never sent again by the call), the code of a refusal in the body.</summary>
    [Theory]
    [InlineData(OtaReviewActionState.Sent, StatusCodes.Status200OK)]
    [InlineData(OtaReviewActionState.Error, StatusCodes.Status200OK)]
    [InlineData(OtaReviewActionState.ManualVerify, StatusCodes.Status202Accepted)]
    public async Task OtaReviewsController_Reply_AnOutcomeNobodyKnowsIs202_TheOthersAre200_WithTheStateAndTheCodeInTheBody(OtaReviewActionState state, int expectedStatus)
    {
        var s = await SeedAsync();
        if (state == OtaReviewActionState.Error)
        {
            s.H.Client
                .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(OctorateAuthException.ReviewNotAvailable(403));
        }
        else if (state == OtaReviewActionState.ManualVerify)
        {
            s.H.Client
                .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OctorateServerException(503));
        }

        await using var rig = Collaborator(s.H);
        var answer = await rig.Reviews().Reply(s.Granted.Id, new ReplyOtaReviewRequest { Text = ReplyText }, KeyOne, CancellationToken.None);

        var result = answer.Result switch
        {
            OkObjectResult ok => (Status: StatusCodes.Status200OK, Body: ok.Value),
            ObjectResult other => (Status: other.StatusCode ?? 0, Body: other.Value),
            _ => throw new InvalidOperationException("Unexpected result."),
        };
        Assert.Equal(expectedStatus, result.Status);
        var dto = Assert.IsType<OtaReviewActionDto>(result.Body);
        Assert.Equal(state, dto.State);
        Assert.Equal(state == OtaReviewActionState.Error ? OtaReviewErrorCodes.ReviewNotAvailable : null, dto.ErrorCode);
    }

    /// <summary>
    /// PROOF TEST: permission rule (NF-69, T02). The reviews of a booking and the read of the channel manager asked now follow the reach of the caller: a collaborator «Solo alcuni» gets the 404 of a
    /// booking that does not exist for a house it was not given, for another org and for a missing one, and nothing is read; its own house is listed and read (once every ten minutes).
    /// </summary>
    [Fact]
    public async Task BookingReviewsController_ACollaboratorSoloAlcuni_ListsAndRefreshesTheBookingsOfItsHouseOnly_TheOthersAreA404_AndNothingIsRead()
    {
        var s = await SeedAsync();
        s.H.Client.Invocations.Clear();
        await using var rig = Collaborator(s.H);
        var controller = rig.Bookings();

        foreach (var bookingId in new[] { s.H.Hidden.BookingId, s.H.Other.BookingId, Guid.NewGuid() })
        {
            Assert.IsType<NotFoundResult>((await controller.Get(bookingId, CancellationToken.None)).Result);
            Assert.IsType<NotFoundResult>((await controller.Refresh(bookingId, CancellationToken.None)).Result);
        }

        s.H.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var listed = Assert.IsType<OkObjectResult>((await controller.Get(s.H.Granted.BookingId, CancellationToken.None)).Result);
        Assert.Equal(s.H.Granted.BookingId, Assert.IsType<BookingReviewsDto>(listed.Value).BookingId);

        s.H.Clock.Advance(TimeSpan.FromMinutes(11));
        var refreshed = Assert.IsType<OkObjectResult>((await controller.Refresh(s.H.Granted.BookingId, CancellationToken.None)).Result);
        Assert.Equal(s.H.Granted.BookingId, Assert.IsType<BookingReviewsDto>(refreshed.Value).BookingId);
        s.H.Client.Verify(c => c.GetReviewAsync(OtaReviewHarness.GrantedAccommodation, OtaReviewHarness.GrantedRefer, It.IsAny<CancellationToken>()), Times.Once);
        s.H.Client.Verify(c => c.GetReviewAsync(OtaReviewHarness.HiddenAccommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The answer of the refresh through the real error middleware: a read the channel manager refused is a 409 with its code, a channel manager that does not answer a 502, a stay that did not happen a
    /// 422 with its own code, a house whose listing nobody confirmed a 409 <c>mapping_not_confirmed</c>, and a failed read counts for the ten minutes (429); only a read that worked is a 200.
    /// </summary>
    [Fact]
    public async Task BookingReviewsController_Refresh_AReadThatDidNotWorkIsAnErrorWithItsCode_AndOnlyAReadThatWorkedIsA200()
    {
        var s = await SeedAsync();
        var h = s.H;
        var cancelled = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, OtaReviewHarness.GrantedAccommodation, "HMCANCELED", status: BookingStatus.Cancelled);
        h.WithdrawListing(h.Hidden.OrgId, OtaReviewHarness.HiddenAccommodation);
        await using var rig = Owner(h);
        var controller = rig.Bookings();
        h.Clock.Advance(TimeSpan.FromMinutes(11));

        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ReviewNotAvailable(403));
        var refused = await AnswerOfAsync(() => controller.Refresh(h.Granted.BookingId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, refused.Code);

        var tooSoon = await AnswerOfAsync(() => controller.Refresh(h.Granted.BookingId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooSoon.Status);
        Assert.Equal(OtaReviewErrorCodes.RefreshTooSoon, tooSoon.Code);

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        var down = await AnswerOfAsync(() => controller.Refresh(h.Granted.BookingId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status502BadGateway, down.Status);
        Assert.Equal("octorate_unavailable", down.Code);

        var notReviewable = await AnswerOfAsync(() => controller.Refresh(cancelled.BookingId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, notReviewable.Status);
        Assert.Equal(OtaReviewErrorCodes.BookingNotReviewable, notReviewable.Code);

        var noListing = await AnswerOfAsync(() => controller.Refresh(h.Hidden.BookingId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, noListing.Status);
        Assert.Equal(OtaReviewErrorCodes.MappingNotConfirmed, noListing.Code);

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        h.ChannelHasReview(h.Granted);
        var worked = await AnswerOfAsync(() => controller.Refresh(h.Granted.BookingId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, worked.Status);
        Assert.Null(worked.Code);
    }

    /// <summary>
    /// PROOF TEST: permission rule (NF-69, T03). The detail of a review of the hub is read in the reach of the caller: a collaborator «Solo alcuni» opens the reviews of its house (of a channel and of the
    /// booking site) and gets the 404 <c>review_not_found</c> for the others, for a review of another org and for a missing one.
    /// </summary>
    [Fact]
    public async Task MessageThreadsController_GetReview_ACollaboratorSoloAlcuni_OpensTheReviewsOfItsHouseOnly_TheOthersAreA404()
    {
        var s = await SeedAsync();
        var orgId = s.H.Granted.OrgId;
        var grantedSite = await s.H.AddSiteReviewAsync(await s.H.AddStayAsync(orgId, s.H.Granted.PropertyId, "585009", "HMDIRECT01", BookingSource.Direct));
        var hiddenSite = await s.H.AddSiteReviewAsync(await s.H.AddStayAsync(orgId, s.H.Hidden.PropertyId, "585010", "HMDIRECT02", BookingSource.Direct));
        s.H.ChannelHasReview(s.H.Other, "Un segreto dell'altra organizzazione");
        var other = await s.H.SyncAsync(s.H.Other);
        await using var rig = Collaborator(s.H);
        var controller = rig.Hub();

        foreach (var key in new[] { MessageThreadKey.ForOtaReview(s.Hidden.Id), MessageThreadKey.ForReview(hiddenSite.Id), MessageThreadKey.ForOtaReview(other.Id), MessageThreadKey.ForOtaReview(Guid.NewGuid()) })
        {
            var denied = await Assert.ThrowsAsync<NotFoundException>(() => controller.GetReview(key.ToString(), CancellationToken.None));
            Assert.Equal(MessageHubService.ReviewNotFoundCode, denied.Code);
        }

        foreach (var key in new[] { MessageThreadKey.ForOtaReview(s.Granted.Id), MessageThreadKey.ForReview(grantedSite.Id) })
        {
            var opened = Assert.IsType<OkObjectResult>((await controller.GetReview(key.ToString(), CancellationToken.None)).Result);
            Assert.Equal(key.ToString(), Assert.IsType<ReviewThreadDto>(opened.Value).ThreadKey);
        }

        // The key of a conversation is not the key of a review.
        await Assert.ThrowsAsync<NotFoundException>(() => controller.GetReview(MessageThreadKey.ForOta(s.Granted.Id).ToString(), CancellationToken.None));
    }

    /// <summary>
    /// PROOF TEST: permission rule (NF-69, T03). A draft is asked for in the reach of the caller: the controller hands the real scope of a collaborator «Solo alcuni» to the drafter, which answers the review of
    /// a house it was not given, of another org or a missing one as «not found» (the 404 <c>review_not_found</c>) without asking the model or the queue (every other service is strict).
    /// </summary>
    [Fact]
    public async Task ReviewDraftsController_ACollaboratorSoloAlcuni_AsksForADraftOfItsHouseOnly_TheOthersAreA404_AndTheModelIsNeverAsked()
    {
        var s = await SeedAsync();
        var hiddenSite = await s.H.AddSiteReviewAsync(await s.H.AddStayAsync(s.H.Granted.OrgId, s.H.Hidden.PropertyId, "585010", "HMDIRECT02", BookingSource.Direct));
        s.H.ChannelHasReview(s.H.Other, "Un segreto dell'altra organizzazione");
        var other = await s.H.SyncAsync(s.H.Other);
        await using var rig = Collaborator(s.H);
        var controller = rig.Drafts();

        foreach (var key in new[] { MessageThreadKey.ForOtaReview(s.Hidden.Id), MessageThreadKey.ForReview(hiddenSite.Id), MessageThreadKey.ForOtaReview(other.Id), MessageThreadKey.ForOtaReview(Guid.NewGuid()) })
        {
            var problem = Assert.IsType<ObjectResult>((await controller.Draft(key.ToString(), CancellationToken.None)).Result);
            Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
            Assert.Equal("review_not_found", CodeOf(problem));
        }

        // The key of a conversation is no review either.
        var conversation = Assert.IsType<ObjectResult>((await controller.Draft(MessageThreadKey.ForOta(s.Granted.Id).ToString(), CancellationToken.None)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, conversation.StatusCode);
    }

    [Fact]
    public async Task ReviewDraftsController_AsksTheDrafterInTheReachAndInTheNameOfTheCaller_AndAnswersWhatItDid()
    {
        var scope = new HostScope(OrgId, GrantedToUserId: Anna);
        var item = Guid.NewGuid();
        var drafter = new Mock<IReviewReplyDrafter>();
        drafter
            .SetupSequence(d => d.DraftAsync(OrgId, DraftKey, It.Is<ReviewDraftOptions?>(o => o != null && o.Requested && o.RequestedBy == scope && o.RequestedByUserId == Anna), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviewDraftOutcome.Drafted(item))
            .ReturnsAsync(ReviewDraftOutcome.NotDrafted(ReviewDraftReasons.AssistantDisabled));
        var controller = new ReviewDraftsController(drafter.Object, Orgs(), Scopes(scope)).WithCaller(HostAuthorizationTestHarness.User(Anna));

        var drafted = Assert.IsType<OkObjectResult>((await controller.Draft(DraftKey.ToString(), CancellationToken.None)).Result);
        var notDrafted = Assert.IsType<OkObjectResult>((await controller.Draft(DraftKey.ToString(), CancellationToken.None)).Result);

        var first = Assert.IsType<ReviewDraftOutcomeDto>(drafted.Value);
        Assert.Equal(ReviewDraftStatus.Drafted, first.Status);
        Assert.Equal(item, first.ApprovalItemId);
        Assert.Null(first.Reason);
        var second = Assert.IsType<ReviewDraftOutcomeDto>(notDrafted.Value);
        Assert.Equal(ReviewDraftStatus.NotDrafted, second.Status);
        Assert.Null(second.ApprovalItemId);
        Assert.Equal(ReviewDraftReasons.AssistantDisabled, second.Reason);
    }

    // ─── The rig ───

    private static IEnumerable<MethodInfo> ActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName);

    private static string? CodeOf(ObjectResult problem)
    {
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        if (!details.Extensions.TryGetValue("code", out var code))
            return null;

        return code is JsonElement json ? json.GetString() : code?.ToString();
    }

    /// <summary>What a call becomes through the real error middleware: the status of the answer and the <c>code</c> of its problem (<c>null</c> for an answer that is not a problem).</summary>
    private static async Task<(int Status, string? Code)> AnswerOfAsync(Func<Task> call)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/bookings/booking/reviews/refresh";
        context.Response.Body = new MemoryStream();

        await new ErrorHandlingMiddleware(_ => call(), NullLogger<ErrorHandlingMiddleware>.Instance).InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
            return (context.Response.StatusCode, null);

        using var json = JsonDocument.Parse(body);
        return (context.Response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static IOrgContextResolver Orgs()
    {
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        return orgs.Object;
    }

    private static IHostScopeResolver Scopes(HostScope? scope)
    {
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
        return scopes.Object;
    }

    private sealed record Seeded(OtaReviewHarness H, OtaReview Granted, OtaReview Hidden);

    /// <summary>The world of the reviews with the Airbnb review of the house the collaborator was given and of the one it was not, both published.</summary>
    private static async Task<Seeded> SeedAsync()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasReview(h.Granted);
        var granted = await h.SyncAsync(h.Granted);
        h.ChannelHasReview(h.Hidden, "Casa pulita ma rumorosa.", 3m);
        var hidden = await h.SyncAsync(h.Hidden);
        return new Seeded(h, granted, hidden);
    }

    private static Rig Collaborator(OtaReviewHarness h) =>
        new(h, Member(h.World.OrgId, OrgRole.Collaborator, PropertyScope.Selected, h.World.Granted.Id), h.World.CollaboratorId);

    private static Rig Owner(OtaReviewHarness h, Func<string, string, bool>? hasPermission = null) =>
        new(h, Member(h.World.OrgId, OrgRole.Owner), h.World.OwnerUserId, hasPermission);

    private static UserAuthorizationSnapshot Member(Guid orgId, OrgRole role, PropertyScope scope = PropertyScope.All, params Guid[] granted) => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(orgId, role, OrgMemberStatus.Active, scope, granted.Length == 0 ? null : granted.ToHashSet()));

    private static SendHostReviewRequest GoodReview() => new()
    {
        Ratings = [new HostReviewRatingRequest { Category = "cleanliness", Value = 5m }, new HostReviewRatingRequest { Category = "communication", Value = 4m }],
        Text = "Ospiti ordinati e puntuali.",
        PrivateText = "Grazie per aver lasciato la casa in ordine.",
    };

    private static void NothingWasSent(OtaReviewHarness h)
    {
        h.Client.Verify(
            c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        h.Client.Verify(
            c => c.SendHostReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateHostReviewCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The controllers of the reviews as a request of one caller builds them: the services are the real ones over the database of the world (the tenant filter on), the authorization is the real
    /// handler of the resource over the membership of the caller, and the reach of the caller is resolved from the same membership.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly OtaReviewHarness _h;
        private readonly ReviewRequestRig _request;
        private readonly ClaimsPrincipal _user;
        private readonly IHostScopeResolver _scopes;
        private readonly IAuthorizationService _authorization;

        public Rig(OtaReviewHarness h, UserAuthorizationSnapshot membership, string userId, Func<string, string, bool>? hasPermission = null)
        {
            _h = h;
            _request = h.AsRequestOf(h.World.OrgId);
            _user = HostAuthorizationTestHarness.User(userId);
            _scopes = HostAuthorizationTestHarness.ScopeResolver(membership);
            _authorization = HostAuthorizationTestHarness.Create(h.World.OrgId, hasPermission, _scopes);
        }

        public OtaReviewsController Reviews() =>
            new OtaReviewsController(_request.Service, OrgsOfTheCaller(), _scopes, new HostResourceLookup(_request.Db), _authorization).WithCaller(_user);

        public BookingReviewsController Bookings() =>
            new BookingReviewsController(_request.Service, OrgsOfTheCaller(), _scopes, new HostResourceLookup(_request.Db), _authorization).WithCaller(_user);

        public MessageThreadsController Hub() =>
            new MessageThreadsController(
                new MessageHubService(
                    _request.Db,
                    Mock.Of<IGuestMessageService>(),
                    _h.Mappings.Object,
                    Mock.Of<IOtaAttachmentFetcher>(),
                    NullLogger<MessageHubService>.Instance,
                    _h.Clock,
                    _request.Service),
                OrgsOfTheCaller(),
                _scopes,
                _authorization).WithCaller(_user);

        /// <summary>The real drafter: what it needs to write a draft (the rules, the model, the queue) is strict, so a draft that is asked for a review the caller does not reach fails the test if it goes further.</summary>
        public ReviewDraftsController Drafts() =>
            new ReviewDraftsController(
                new ReviewReplyDrafter(
                    _request.Db,
                    Mock.Of<IAssistantRulesService>(MockBehavior.Strict),
                    Mock.Of<IAiRuntimeSettings>(MockBehavior.Strict),
                    Mock.Of<IAiProvider>(MockBehavior.Strict),
                    Mock.Of<IApprovalQueueService>(MockBehavior.Strict),
                    _request.Service,
                    NullLogger<ReviewReplyDrafter>.Instance),
                OrgsOfTheCaller(),
                _scopes).WithCaller(_user);

        public ValueTask DisposeAsync() => _request.DisposeAsync();

        private IOrgContextResolver OrgsOfTheCaller()
        {
            var orgs = new Mock<IOrgContextResolver>();
            orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_h.World.OrgId);
            return orgs.Object;
        }
    }
}

internal static class ReviewControllerTestExtensions
{
    /// <summary>Gives a controller the signed-in caller and the services the error answers need (the localizer of the messages).</summary>
    public static T WithCaller<T>(this T controller, ClaimsPrincipal user)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user,
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
