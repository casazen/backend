using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services.ApprovalActions;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The reply to a review leaves only when a person approves the draft (E05/F06/US01/T03, NF-69, AI-06): <see cref="ReviewReplyApprovalAction"/> is the one place that sends it, once, to the channel
/// through the channel manager (Airbnb) or to the booking site; a refusal comes back as a failed action the host can edit and retry; an item that does not name the review it answers sends nothing; a
/// rejection does nothing. The queue itself is the real one, with the checks of the key of a review.
/// </summary>
public class ReviewReplyApprovalTests
{
    private const string User = "auth0|titolare";
    private const string Text = "Grazie di cuore, speriamo di rivedervi presto!";

    private static ApprovalQueueItem ItemOf(Guid orgId, Guid reviewId, string? threadKey) => new()
    {
        OrgId = orgId,
        ItemType = ApprovalItemTypes.ReviewReply,
        ItemId = reviewId,
        ContentText = "Bozza",
        ThreadKey = threadKey,
        Status = ApprovalStatus.Pending,
        OriginAction = ApprovalOriginActions.SendReviewReply,
    };

    private static ReviewReplyApprovalAction ActionOf(IOtaReviewService channel, IHostReviewService? site = null) =>
        new(channel, site ?? Mock.Of<IHostReviewService>(), NullLogger<ReviewReplyApprovalAction>.Instance);

    private static async Task<(OtaReviewHarness H, OtaReview Review, ApprovalQueueItem Item)> ChannelReviewAsync()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasReview(h.Granted);
        var review = await h.SyncAsync(h.Granted);
        return (h, review, ItemOf(review.OrgId, review.Id, MessageThreadKey.ForOtaReview(review.Id).ToString()));
    }

    private static async Task<ApprovalActionResult> ExecuteAsync(OtaReviewHarness h, ApprovalQueueItem item, string text, AppDbContext? shared = null)
    {
        await using var own = shared is null ? h.NewDb() : null;
        var db = shared ?? own!;
        using (h.Scope.Use(item.OrgId))
            return await ActionOf(h.Service(db)).ExecuteAsync(item, text, User, CancellationToken.None);
    }

    private static void TheReplyWasSent(OtaReviewHarness h, Times times) =>
        h.Client.Verify(
            c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            times);

    // ─── The action ───

    [Fact]
    public async Task Approve_AReplyToAReviewOfAChannel_SendsTheFinalTextOnce_WithTheKeyOfTheItem_AndAnotherApprovalSendsNothing()
    {
        var (h, review, item) = await ChannelReviewAsync();

        var first = await ExecuteAsync(h, item, Text);
        var again = await ExecuteAsync(h, item, Text);

        Assert.True(first.Succeeded);
        Assert.Equal(nameof(OtaReviewActionState.Sent), first.State);
        Assert.True(again.Succeeded);
        h.Client.Verify(
            c => c.SendHostReplyAsync(OtaReviewHarness.GrantedAccommodation, OtaReviewHarness.GrantedRefer, Text, It.IsAny<CancellationToken>()),
            Times.Once);
        TheReplyWasSent(h, Times.Once());
        var row = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(review.Id, row.Id);
        Assert.Equal(ApprovalQueueRules.SendKeyOf(item.Id, Text), row.HostReplyIdempotencyKey);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReplyState);
        Assert.DoesNotContain("speriamo", h.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_AReplyTheChannelRefused_IsAFailedAction_AndTheRetryOfTheSameTextSendsWithTheNextKey()
    {
        var (h, _, item) = await ChannelReviewAsync();
        h.Client
            .SetupSequence(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ReviewNotAvailable(403))
            .Returns(Task.CompletedTask);

        var refused = await ExecuteAsync(h, item, Text);
        Assert.False(refused.Succeeded);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, refused.ErrorCode);

        // The same key would replay the refusal for ever: the retry sends with the next one, and only after a refusal.
        var retried = await ExecuteAsync(h, item, Text);

        Assert.True(retried.Succeeded);
        TheReplyWasSent(h, Times.Exactly(2));
        Assert.Equal(ApprovalQueueRules.SendKeyOf(item.Id, Text, 2), (await h.ReviewOfAsync(h.Granted)).HostReplyIdempotencyKey);
    }

    [Fact]
    public async Task Approve_ACallTheChannelManagerDidNotAnswer_CountsAsDone_AndIsNeverSentAgain()
    {
        var (h, _, item) = await ChannelReviewAsync();
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));

        var first = await ExecuteAsync(h, item, Text);
        var again = await ExecuteAsync(h, item, Text);

        Assert.True(first.Succeeded);
        Assert.Equal(nameof(OtaReviewActionState.ManualVerify), first.State);
        Assert.True(again.Succeeded);
        TheReplyWasSent(h, Times.Once());
    }

    [Fact]
    public async Task Approve_WhenTheChannelSaysItIsNotAllowedOrTheReviewIsNotThere_IsAFailedActionWithItsCode()
    {
        var (h, review, item) = await ChannelReviewAsync();
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateReviewActionNotAllowedException(409));

        var notAllowed = await ExecuteAsync(h, item, Text);
        Assert.False(notAllowed.Succeeded);
        Assert.Equal(OtaReviewErrorCodes.ActionNotAllowed, notAllowed.ErrorCode);

        // A review that is not there for the org of the item: nothing is asked of the channel.
        var gone = ItemOf(review.OrgId, Guid.NewGuid(), MessageThreadKey.ForOtaReview(review.Id).ToString());
        var mismatch = await ExecuteAsync(h, gone, Text);
        Assert.False(mismatch.Succeeded);
        Assert.Equal(MessageThreadKey.InvalidCode, mismatch.ErrorCode);

        var other = ItemOf(h.Other.OrgId, review.Id, MessageThreadKey.ForOtaReview(review.Id).ToString());
        var foreign = await ExecuteAsync(h, other, Text);
        Assert.False(foreign.Succeeded);
        Assert.Equal(OtaReviewErrorCodes.NotFound, foreign.ErrorCode);
        TheReplyWasSent(h, Times.Once());
    }

    [Fact]
    public async Task Approve_AnItemThatDoesNotNameTheReviewItAnswers_SendsNothing()
    {
        var (h, review, _) = await ChannelReviewAsync();
        var items = new[]
        {
            ItemOf(review.OrgId, review.Id, null),
            ItemOf(review.OrgId, review.Id, "garbage"),
            ItemOf(review.OrgId, review.Id, MessageThreadKey.ForOta(review.Id).ToString()),
            ItemOf(review.OrgId, review.Id, MessageThreadKey.ForDirect(review.Id).ToString()),
            ItemOf(review.OrgId, review.Id, MessageThreadKey.ForOtaReview(Guid.NewGuid()).ToString()),
        };

        foreach (var item in items)
        {
            var result = await ExecuteAsync(h, item, Text);

            Assert.False(result.Succeeded);
            Assert.Equal(MessageThreadKey.InvalidCode, result.ErrorCode);
        }

        TheReplyWasSent(h, Times.Never());
        Assert.Equal(OtaReviewActionState.None, (await h.ReviewOfAsync(h.Granted)).HostReplyState);
    }

    [Fact]
    public async Task Approve_AReplyToAReviewOfTheBookingSite_AnswersItOnce_AndAnAnswerThatIsThereIsAFailedAction()
    {
        var reviewId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var site = new Mock<IHostReviewService>();
        var channel = new Mock<IOtaReviewService>(MockBehavior.Strict);
        var action = ActionOf(channel.Object, site.Object);
        var item = ItemOf(orgId, reviewId, MessageThreadKey.ForReview(reviewId).ToString());

        var done = await action.ExecuteAsync(item, Text, User, CancellationToken.None);

        Assert.True(done.Succeeded);
        Assert.Equal(nameof(OtaReviewActionState.Sent), done.State);
        site.Verify(s => s.ReplyAsync(reviewId, Text, It.IsAny<CancellationToken>()), Times.Once);

        site.Setup(s => s.ReplyAsync(reviewId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException("review_reply_already_sent", "ReviewReplyAlreadySent"));
        var already = await action.ExecuteAsync(item, "Un altro testo", User, CancellationToken.None);
        Assert.False(already.Succeeded);
        Assert.Equal("review_reply_already_sent", already.ErrorCode);

        site.Setup(s => s.ReplyAsync(reviewId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("gone"));
        var gone = await action.ExecuteAsync(item, "Un altro testo ancora", User, CancellationToken.None);
        Assert.False(gone.Succeeded);
        Assert.Equal(OtaReviewErrorCodes.NotFound, gone.ErrorCode);

        // Nothing of a review of the booking site is ever asked of the channel manager.
        channel.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reject_ADraftOfAReply_UndoesNothing_BecauseNothingWasSent()
    {
        var (h, _, item) = await ChannelReviewAsync();
        await using var db = h.NewDb();
        var action = ActionOf(h.Service(db));

        await action.OnRejectedAsync(item, "Non adatto", User, CancellationToken.None);

        TheReplyWasSent(h, Times.Never());
        Assert.Equal(ApprovalItemTypes.ReviewReply, action.ItemType);
        Assert.Equal(ApprovalOriginActions.SendReviewReply, action.OriginAction);
    }

    // ─── The queue, the real one ───

    /// <summary>
    /// PROOF TEST: data rule (AC-NF69-4). The draft of a reply is an item of the approval queue and nothing else: while it waits, and when it is rejected, nothing reaches the channel manager; the
    /// approval sends the text the host approved (its edit when it edited it), once, however many times it is approved; a refusal keeps the item pending with the code, so the host edits and
    /// tries again. Nothing is ever sent without the approval of a person.
    /// </summary>
    [Fact]
    public async Task ReviewReply_NeverSentWithoutApproval_Test()
    {
        using var rig = new ApprovalQueueRig();
        await rig.SeedAsync();
        var channel = new Mock<IOtaReviewService>();
        channel
            .Setup(s => s.ReplyAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtaReviewActionResult(null!, OtaReviewActionState.Sent, false, null));
        var service = rig.Service(new ApprovalOriginActionRegistry(() => [ActionOf(channel.Object)]));

        // The assistant's draft waits: the queue has it, the channel manager has heard nothing.
        var reviewId = Guid.NewGuid();
        var item = await service.SubmitForApprovalAsync(
            rig.OrgId,
            ApprovalItemTypes.ReviewReply,
            reviewId,
            "Grazie di cuore!",
            isAiGenerated: true,
            propertyId: rig.PropertyA.Id,
            originAction: ApprovalOriginActions.SendReviewReply,
            threadKey: MessageThreadKey.ForOtaReview(reviewId).ToString());
        Assert.Equal(ApprovalStatus.Pending, item.Status);
        Assert.Equal(ApprovalOriginActions.SendReviewReply, item.OriginAction);
        channel.Verify(s => s.ReplyAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // A person edits it and approves it: the edit is what is sent, with the key of the item and the text, once.
        await service.EditAsync(rig.OrgWide, item.Id, "Grazie di cuore, a presto!", ApprovalQueueRig.Owner);
        var decision = await service.ApproveAsync(rig.OrgWide, item.Id, ApprovalQueueRig.Owner);
        await service.ApproveAsync(rig.OrgWide, item.Id, ApprovalQueueRig.Owner);
        Assert.Equal(nameof(OtaReviewActionState.Sent), decision.ActionState);
        channel.Verify(
            s => s.ReplyAsync(reviewId, "Grazie di cuore, a presto!", ApprovalQueueRules.SendKeyOf(item.Id, "Grazie di cuore, a presto!"), ApprovalQueueRig.Owner, It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(s => s.ReplyAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // A draft that is rejected does nothing.
        var otherReview = Guid.NewGuid();
        var rejected = await service.SubmitForApprovalAsync(
            rig.OrgId,
            ApprovalItemTypes.ReviewReply,
            otherReview,
            "Una risposta che non va",
            isAiGenerated: true,
            propertyId: rig.PropertyA.Id,
            originAction: ApprovalOriginActions.SendReviewReply,
            threadKey: MessageThreadKey.ForOtaReview(otherReview).ToString());
        var result = await service.RejectAsync(rig.OrgWide, rejected.Id, ApprovalQueueRig.Owner, "Non è il tono giusto");
        Assert.Equal(ApprovalStatus.Rejected, result.Status);
        channel.Verify(s => s.ReplyAsync(otherReview, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // A refusal keeps the item pending with the code of the failure: the host edits and tries again.
        var refusing = new Mock<IOtaReviewService>();
        refusing
            .Setup(s => s.ReplyAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtaReviewActionResult(null!, OtaReviewActionState.Error, false, OtaReviewErrorCodes.ReviewNotAvailable));
        var failing = rig.Service(new ApprovalOriginActionRegistry(() => [ActionOf(refusing.Object)]));
        var thirdReview = Guid.NewGuid();
        var third = await failing.SubmitForApprovalAsync(
            rig.OrgId,
            ApprovalItemTypes.ReviewReply,
            thirdReview,
            "Una terza risposta",
            isAiGenerated: true,
            propertyId: rig.PropertyA.Id,
            originAction: ApprovalOriginActions.SendReviewReply,
            threadKey: MessageThreadKey.ForOtaReview(thirdReview).ToString());

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => failing.ApproveAsync(rig.OrgWide, third.Id, ApprovalQueueRig.Owner));

        Assert.Equal(ApprovalQueueRules.OriginActionFailedCode, error.Code);
        var stored = await rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == third.Id);
        Assert.Equal(ApprovalStatus.Pending, stored.Status);
        Assert.Equal(ApprovalOriginActionStatus.Failed, stored.OriginActionStatus);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, stored.OriginActionError);
    }

    [Fact]
    public async Task Submit_AReplyToAReview_NamesItsReview_AndADraftOfAMessageNamesAConversation()
    {
        using var rig = new ApprovalQueueRig();
        await rig.SeedAsync();
        var service = rig.Service(new ApprovalOriginActionRegistry(() => [ActionOf(Mock.Of<IOtaReviewService>())]));
        var id = Guid.NewGuid();

        var item = await service.SubmitForApprovalAsync(
            rig.OrgId,
            ApprovalItemTypes.ReviewReply,
            id,
            "Grazie!",
            isAiGenerated: true,
            propertyId: rig.PropertyA.Id,
            originAction: ApprovalOriginActions.SendReviewReply,
            threadKey: MessageThreadKey.ForReview(id).ToString());
        Assert.Equal(MessageThreadKey.ForReview(id).ToString(), item.ThreadKey);

        // The key of nothing, of a conversation, of another review, or of none: the reply would not know what it answers.
        foreach (var key in new string?[] { null, "review:nope", MessageThreadKey.ForOta(id).ToString(), MessageThreadKey.ForOtaReview(Guid.NewGuid()).ToString() })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
                rig.OrgId, ApprovalItemTypes.ReviewReply, id, "Un altro testo", isAiGenerated: true, originAction: ApprovalOriginActions.SendReviewReply, threadKey: key));
        }

        // A draft of a message never belongs to a review.
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            rig.OrgId, ApprovalItemTypes.MessageDraft, id, "Ciao", isAiGenerated: true, threadKey: MessageThreadKey.ForReview(id).ToString()));
    }

    [Fact]
    public void TheTextOfAnEditOfAReply_IsPlainText_OfAtMostTheLengthOfAReply()
    {
        Assert.Equal("ReviewReply", ApprovalItemTypes.ReviewReply);
        Assert.Equal("SendReviewReply", ApprovalOriginActions.SendReviewReply);
        Assert.Equal("Grazie di cuore!", ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.ReviewReply, "  Grazie di cuore!  "));
        Assert.Equal(ReviewRules.ReplyMaxLength, ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.ReviewReply, new string('a', ReviewRules.ReplyMaxLength))?.Length);
        Assert.Null(ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.ReviewReply, new string('a', ReviewRules.ReplyMaxLength + 1)));
        Assert.Null(ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.ReviewReply, "   "));
        Assert.Null(ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.ReviewReply, null));
    }
}
