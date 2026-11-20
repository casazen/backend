using Casazen.Core.Assistant;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Assistant;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>An approval queue that keeps what it is given and decides nothing: what the drafter of the replies to the reviews writes, and what it supersedes.</summary>
internal sealed class RecordingApprovalQueue : IApprovalQueueService
{
    public List<(ApprovalQueueItem Item, bool IsAiGenerated, string? ContentVersion, string? OriginAction)> Submitted { get; } = [];

    public List<(Guid ItemId, string UserId, string? Reason)> Rejected { get; } = [];

    public Task<ApprovalQueueItem> SubmitForApprovalAsync(
        Guid orgId,
        string itemType,
        Guid itemId,
        string contentText,
        bool isAiGenerated,
        string? contentVersion = null,
        Guid? propertyId = null,
        string? originAction = null,
        decimal? confidenceScore = null,
        string? situationCode = null,
        string? threadKey = null,
        CancellationToken cancellationToken = default)
    {
        var item = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = itemType,
            ItemId = itemId,
            ContentText = contentText,
            ContentVersion = contentVersion,
            IsAiGenerated = isAiGenerated,
            Status = ApprovalStatus.Pending,
            PropertyId = propertyId,
            OriginAction = originAction ?? ApprovalOriginActions.None,
            ConfidenceScore = confidenceScore,
            SituationCode = situationCode,
            ThreadKey = threadKey,
        };
        Submitted.Add((item, isAiGenerated, contentVersion, originAction));
        return Task.FromResult(item);
    }

    public Task<ApprovalQueueItem> RejectAsync(HostScope scope, Guid itemId, string userId, string? reason, CancellationToken cancellationToken = default)
    {
        Rejected.Add((itemId, userId, reason));
        return Task.FromResult(new ApprovalQueueItem { Id = itemId, OrgId = scope.OrgId, Status = ApprovalStatus.Rejected });
    }

    public Task<ApprovalQueueItem> EditAsync(HostScope scope, Guid itemId, string? text, string userId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ApprovalDecision> ApproveAsync(HostScope scope, Guid itemId, string userId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<List<ApprovalQueueItem>> GetPendingAsync(HostScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> ReturnToPendingIfChangedAsync(string itemType, Guid itemId, string currentContentText, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>
/// The assistant writes the reply to a review for the host to approve (E05/F06/US01/T03, NF-69, AI-06, PO-09): a draft in the approval queue and nothing else. What is proven is what the
/// drafter does with a review: the model reads the stars and the words with the names, the codes and the contact data taken out; a reply that promises money or says it is an AI is never
/// queued; there is one draft waiting for a review; a review that is not the caller's does not exist; and nothing is sent to the channel manager or to the booking site from here.
/// </summary>
public class ReviewReplyDrafterTests
{
    private const string User = "auth0|titolare";

    private sealed class Rig
    {
        public required OtaReviewHarness H { get; init; }

        public Mock<IAssistantRulesService> Rules { get; } = new();

        public Mock<IAiRuntimeSettings> Settings { get; } = new();

        public ScriptedAiProvider Ai { get; } = new();

        public RecordingApprovalQueue Queue { get; } = new();

        public CapturingLogger<ReviewReplyDrafter> Log { get; } = new();

        public static async Task<Rig> CreateAsync(string reply = "Grazie di cuore per le belle parole, speriamo di rivedervi presto!")
        {
            var rig = new Rig { H = await OtaReviewHarness.CreateAsync() };
            var rules = EffectiveAssistantRules.Defaults with { Enabled = true, Signature = "Il team di Casa Bella (TEST)" };
            rig.Rules
                .Setup(r => r.ResolveEffectiveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(rules);
            rig.Settings
                .Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AiOptions { Provider = AiOptions.DeepSeekProvider, ApiKey = "test-key", Model = "deepseek-v4-flash" });
            rig.SetReply(reply);
            return rig;
        }

        public void SetReply(string reply) => Ai.Draft = System.Text.Json.JsonSerializer.Serialize(new { reply });

        public void SwitchOff() =>
            Rules
                .Setup(r => r.ResolveEffectiveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(EffectiveAssistantRules.Defaults);

        public async Task<ReviewDraftOutcome> DraftAsync(MessageThreadKey key, Guid? orgId = null, ReviewDraftOptions? options = null)
        {
            await using var db = H.NewDb();
            var drafter = new ReviewReplyDrafter(db, Rules.Object, Settings.Object, Ai, Queue, H.Service(db), Log);
            using (H.Scope.Use(orgId ?? H.Granted.OrgId))
                return await drafter.DraftAsync(orgId ?? H.Granted.OrgId, key, options);
        }

        public ReviewDraftOptions AskedBy(HostScope scope) => new() { RequestedBy = scope, RequestedByUserId = User };
    }

    private static MessageThreadKey KeyOf(OtaReview review) => MessageThreadKey.ForOtaReview(review.Id);

    private static async Task<OtaReview> PublishedAsync(Rig rig, ReviewStay? stay = null, string text = "Posto bellissimo, host gentilissimo.", decimal rating = 5m)
    {
        var target = stay ?? rig.H.Granted;
        rig.H.ChannelHasReview(target, text, rating);
        return await rig.H.SyncAsync(target);
    }

    private static void NothingWasSent(Rig rig)
    {
        rig.H.Client.Verify(
            c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        rig.H.Client.Verify(
            c => c.SendHostReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateHostReviewCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Draft_OfAPublishedReviewOfAChannel_QueuesOneReplyWaitingForApproval_AndSendsNothing()
    {
        var rig = await Rig.CreateAsync();
        var review = await PublishedAsync(rig);

        var outcome = await rig.DraftAsync(KeyOf(review));

        Assert.Equal(ReviewDraftStatus.Drafted, outcome.Status);
        var (item, isAiGenerated, version, originAction) = Assert.Single(rig.Queue.Submitted);
        Assert.Equal(ApprovalItemTypes.ReviewReply, item.ItemType);
        Assert.Equal(review.Id, item.ItemId);
        Assert.Equal(rig.H.Granted.OrgId, item.OrgId);
        Assert.Equal(rig.H.Granted.PropertyId, item.PropertyId);
        Assert.Equal(KeyOf(review).ToString(), item.ThreadKey);
        Assert.Equal(ApprovalOriginActions.SendReviewReply, originAction);
        Assert.True(isAiGenerated);
        Assert.Equal(ReviewReplyPromptBuilder.Version, version);
        Assert.Null(item.ConfidenceScore);

        // The reply of the model, then the signature of the rules on a line of its own.
        Assert.StartsWith("Grazie di cuore per le belle parole", item.ContentText, StringComparison.Ordinal);
        Assert.EndsWith("Il team di Casa Bella (TEST)", item.ContentText, StringComparison.Ordinal);

        // The draft waits: the review has no reply, no key, no state, and the channel manager was not called for a reply.
        NothingWasSent(rig);
        var row = await rig.H.ReviewOfAsync(rig.H.Granted);
        Assert.Equal(OtaReviewActionState.None, row.HostReplyState);
        Assert.Null(row.HostReplyText);
        var call = Assert.Single(rig.Ai.Calls);
        Assert.Equal(AiModelTier.Standard, call.Tier);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (PO-09). The name of the guest, the code of the booking, the reference on the channel manager, the address and the number of the guest never reach the
    /// model: not in the words of the review it reads, not in the instructions; and the instructions forbid greeting by a name, so the reply has none.
    /// </summary>
    [Fact]
    public async Task ReviewReply_TheGuestNameNeverReachesTheModel_Test()
    {
        var rig = await Rig.CreateAsync();
        var bookingCode = await rig.H.ReadAsync(db => db.Bookings.Where(b => b.Id == rig.H.Granted.BookingId).Select(b => b.BookingCode).SingleAsync());
        var text = $"Siamo Giulietta Verdi, un soggiorno perfetto! Prenotazione {bookingCode} ({OtaReviewHarness.GrantedRefer}). Scrivetemi a giulietta.verdi@guest-mail.example o al +39 333 1234567.";
        var review = await PublishedAsync(rig, text: text, rating: 4.5m);

        var outcome = await rig.DraftAsync(KeyOf(review));

        Assert.Equal(ReviewDraftStatus.Drafted, outcome.Status);
        var request = Assert.Single(rig.Ai.Calls).Request;
        var everything = $"{request.SystemPrompt}\n{request.Prompt}";
        foreach (var secret in new[] { "Giulietta", "Verdi", bookingCode, OtaReviewHarness.GrantedRefer, "guest-mail.example", "333 1234567", "3331234567" })
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);

        // What the model does read: the stars, the words that are left, and the rule of not greeting by a name.
        Assert.Contains("RATING: 4.5 out of 5", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("perfetto", request.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not greet by name", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Giulietta", rig.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("perfetto", rig.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Ci scusiamo: le rimborseremo la notte e le daremo uno sconto sul prossimo soggiorno.")]
    [InlineData("We are sorry, we will refund your stay and offer you a discount.")]
    [InlineData("Come assistente AI ringrazio per la recensione.")]
    [InlineData("Thanks! Write to us at info@casa-bella.example or call +39 02 1234 5678.")]
    [InlineData("Grazie, trovate tutto su https://casa-bella.example/prenota")]
    [InlineData("Gentile [guest], grazie per la recensione.")]
    public async Task Draft_AReplyThatPromisesMoneyIsSignedByAnAiOrHasContactDetails_IsNeverQueued(string reply)
    {
        var rig = await Rig.CreateAsync(reply);
        var review = await PublishedAsync(rig);

        var outcome = await rig.DraftAsync(KeyOf(review));

        Assert.Equal(ReviewDraftStatus.NotDrafted, outcome.Status);
        Assert.Equal(ReviewDraftReasons.DraftRuleViolation, outcome.Reason);
        Assert.Empty(rig.Queue.Submitted);
        NothingWasSent(rig);
    }

    [Fact]
    public async Task Draft_WithoutTheAssistantAProviderOrBudget_IsNotDrafted_WithTheReason_AndNothingIsQueued()
    {
        var rig = await Rig.CreateAsync();
        var review = await PublishedAsync(rig);
        var key = KeyOf(review);

        // The assistant is not switched on for the property: the model is not asked.
        rig.SwitchOff();
        Assert.Equal(ReviewDraftReasons.AssistantDisabled, (await rig.DraftAsync(key)).Reason);
        Assert.Empty(rig.Ai.Calls);

        // No provider in effect: the model is not asked either.
        var on = await Rig.CreateAsync();
        var onReview = await PublishedAsync(on);
        on.Settings.Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiOptions());
        Assert.Equal(ReviewDraftReasons.AiProviderNotConfigured, (await on.DraftAsync(KeyOf(onReview))).Reason);
        Assert.Empty(on.Ai.Calls);

        // A provider that did not answer, the budget used up, a provider that fails, an answer that is not a reply.
        var failing = await Rig.CreateAsync();
        var failingReview = await PublishedAsync(failing);
        failing.Ai.Configured = false;
        Assert.Equal(ReviewDraftReasons.AiProviderNotConfigured, (await failing.DraftAsync(KeyOf(failingReview))).Reason);
        failing.Ai.Configured = true;
        failing.Ai.Throw = new AiBudgetExceededException();
        Assert.Equal(ReviewDraftReasons.AiBudgetExceeded, (await failing.DraftAsync(KeyOf(failingReview))).Reason);
        failing.Ai.Throw = new InvalidOperationException("boom");
        Assert.Equal(ReviewDraftReasons.AiProviderError, (await failing.DraftAsync(KeyOf(failingReview))).Reason);
        failing.Ai.Throw = null;
        failing.Ai.Draft = "not a reply";
        Assert.Equal(ReviewDraftReasons.AiInvalidAnswer, (await failing.DraftAsync(KeyOf(failingReview))).Reason);

        Assert.Empty(rig.Queue.Submitted);
        Assert.Empty(on.Queue.Submitted);
        Assert.Empty(failing.Queue.Submitted);
        Assert.DoesNotContain("boom", failing.Log.AllOutput, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), failing.Log.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_ForAReviewThatCannotBeAnswered_IsNotDrafted_WithTheReason()
    {
        var rig = await Rig.CreateAsync();

        // Not published: nothing to answer.
        rig.H.ChannelHasNothing(rig.H.Hidden);
        var unpublished = await rig.H.SyncAsync(rig.H.Hidden);
        Assert.Equal(ReviewDraftReasons.NotPublished, (await rig.DraftAsync(KeyOf(unpublished))).Reason);

        // Answered already, or an attempt waits for a check: nothing to draft.
        var answered = await PublishedAsync(rig);
        await using (var db = rig.H.NewDb())
        {
            var row = await db.OtaReviews.SingleAsync(r => r.Id == answered.Id);
            row.HostReplyState = OtaReviewActionState.ManualVerify;
            row.HostReplyText = "Grazie!";
            await db.SaveChangesAsync();
        }

        Assert.Equal(ReviewDraftReasons.AlreadyAnswered, (await rig.DraftAsync(KeyOf(answered))).Reason);

        // The channel has no integration for reviews.
        var booking = await rig.H.AddStayAsync(rig.H.Granted.OrgId, rig.H.Granted.PropertyId, "585009", "HMBOOKING1", BookingSource.BookingCom);
        Guid otherChannelReview;
        await using (var db = rig.H.NewDb())
        {
            var row = new OtaReview
            {
                OrgId = booking.OrgId,
                PropertyId = booking.PropertyId,
                BookingId = booking.BookingId,
                Channel = OtaChannel.Booking,
                OctorateRefer = booking.Refer,
                GuestReviewPublished = true,
                GuestReviewText = "Bello",
            };
            db.OtaReviews.Add(row);
            await db.SaveChangesAsync();
            otherChannelReview = row.Id;
        }

        Assert.Equal(ReviewDraftReasons.ChannelNotSupported, (await rig.DraftAsync(MessageThreadKey.ForOtaReview(otherChannelReview))).Reason);

        // A reply could not be sent now (the connector is off): no draft is written for it.
        var fresh = await Rig.CreateAsync();
        var freshReview = await PublishedAsync(fresh);
        fresh.H.Flags.Set(FeatureFlags.OctorateConnector, false);
        Assert.Equal(ReviewDraftReasons.ReplyNotPossible, (await fresh.DraftAsync(KeyOf(freshReview))).Reason);

        Assert.Empty(rig.Queue.Submitted);
        Assert.Empty(fresh.Queue.Submitted);
        Assert.Empty(rig.Ai.Calls);
        Assert.Empty(fresh.Ai.Calls);
    }

    [Fact]
    public async Task Draft_OfAReviewOfTheBookingSite_UsesItsStarsAndWords_AndIsNotDraftedOnceAnswered()
    {
        var rig = await Rig.CreateAsync();
        var stay = await rig.H.AddStayAsync(rig.H.Granted.OrgId, rig.H.Granted.PropertyId, "585009", "HMDIRECT01", BookingSource.Direct);
        var review = await rig.H.AddSiteReviewAsync(stay, "Casa pulita e tranquilla.", 3);
        var answeredStay = await rig.H.AddStayAsync(rig.H.Granted.OrgId, rig.H.Granted.PropertyId, "585009", "HMDIRECT02", BookingSource.Direct);
        var answered = await rig.H.AddSiteReviewAsync(answeredStay, "Tutto ok.", 5, hostReply: "Grazie a voi!");

        var outcome = await rig.DraftAsync(MessageThreadKey.ForReview(review.Id));
        var already = await rig.DraftAsync(MessageThreadKey.ForReview(answered.Id));

        Assert.Equal(ReviewDraftStatus.Drafted, outcome.Status);
        var (item, _, _, originAction) = Assert.Single(rig.Queue.Submitted);
        Assert.Equal(MessageThreadKey.ForReview(review.Id).ToString(), item.ThreadKey);
        Assert.Equal(review.Id, item.ItemId);
        Assert.Equal(ApprovalOriginActions.SendReviewReply, originAction);
        Assert.Contains("RATING: 3 out of 5", Assert.Single(rig.Ai.Calls).Request.Prompt, StringComparison.Ordinal);
        Assert.Equal(ReviewDraftReasons.AlreadyAnswered, already.Reason);
    }

    [Fact]
    public async Task Draft_ByItself_WritesNoSecondDraft_ButADraftAPersonAsksForSupersedesTheOneThatWaits()
    {
        var rig = await Rig.CreateAsync();
        var review = await PublishedAsync(rig);
        var key = KeyOf(review);
        var waiting = new ApprovalQueueItem
        {
            OrgId = review.OrgId,
            ItemType = ApprovalItemTypes.ReviewReply,
            ItemId = review.Id,
            ContentText = "Un vecchio draft",
            Status = ApprovalStatus.Pending,
            ThreadKey = key.ToString(),
            CreatedAt = OtaReviewHarness.Now.UtcDateTime,
            ExpiresAt = OtaReviewHarness.Now.UtcDateTime.AddDays(7),
        };
        await using (var db = rig.H.NewDb())
        {
            db.ApprovalQueueItems.Add(waiting);
            await db.SaveChangesAsync();
        }

        // By itself (an event delivered twice, two jobs): one draft waiting is enough.
        var automatic = await rig.DraftAsync(key);
        Assert.Equal(ReviewDraftReasons.AlreadyDrafted, automatic.Reason);
        Assert.Empty(rig.Queue.Submitted);
        Assert.Empty(rig.Ai.Calls);

        // A person who asks for a new one gets it, and the one that waited is rejected with the reason «regenerated» in the person's reach.
        var asked = await rig.DraftAsync(key, options: rig.AskedBy(rig.H.World.OrgWide));
        Assert.Equal(ReviewDraftStatus.Drafted, asked.Status);
        Assert.Single(rig.Queue.Submitted);
        var rejected = Assert.Single(rig.Queue.Rejected);
        Assert.Equal((waiting.Id, User, AssistantDecisionCodes.RegeneratedReason), rejected);
        NothingWasSent(rig);
    }

    [Fact]
    public async Task Draft_ForAReviewOfAnotherOrgOrOutOfTheReach_IsNotFound_AndTheModelIsNeverAsked()
    {
        var rig = await Rig.CreateAsync();
        var granted = await PublishedAsync(rig);
        var hidden = await PublishedAsync(rig, rig.H.Hidden);
        var other = await PublishedAsync(rig, rig.H.Other);

        // A person limited to some properties asks for the review of a house it was not given.
        var restricted = await rig.DraftAsync(KeyOf(hidden), options: rig.AskedBy(rig.H.World.Restricted));
        // The scope of another org asks for a review of this one.
        var foreign = await rig.DraftAsync(KeyOf(granted), options: rig.AskedBy(new HostScope(rig.H.Other.OrgId)));
        // The job of another org is named with the key of a review of this one.
        var wrongOrg = await rig.DraftAsync(KeyOf(other), orgId: rig.H.Granted.OrgId);
        // The key of a conversation is never the one of a review.
        var conversation = await rig.DraftAsync(MessageThreadKey.ForOta(granted.Id));

        Assert.Equal(ReviewDraftStatus.ReviewNotFound, restricted.Status);
        Assert.Equal(ReviewDraftStatus.ReviewNotFound, foreign.Status);
        Assert.Equal(ReviewDraftStatus.ReviewNotFound, wrongOrg.Status);
        Assert.Equal(ReviewDraftStatus.ReviewNotFound, conversation.Status);
        Assert.Empty(rig.Queue.Submitted);
        Assert.Empty(rig.Ai.Calls);

        // The same person, in its reach, gets its draft.
        var granted2 = await rig.DraftAsync(KeyOf(granted), options: rig.AskedBy(rig.H.World.Restricted));
        Assert.Equal(ReviewDraftStatus.Drafted, granted2.Status);
    }

    [Fact]
    public async Task Draft_ASignatureAndAReplyThatDoNotFit_AreNeverQueued_AndAReviewWithNoTextStillGetsADraft()
    {
        var rig = await Rig.CreateAsync(new string('a', ReviewRules.ReplyMaxLength - 5));
        var review = await PublishedAsync(rig);

        // The reply leaves room for the signature: one that does not fit with it is not queued, never cut.
        var tooLong = await rig.DraftAsync(KeyOf(review));
        Assert.Equal(ReviewDraftStatus.NotDrafted, tooLong.Status);
        Assert.Equal(ReviewReplyDrafter.DraftTooLongReason, tooLong.Reason);
        Assert.Empty(rig.Queue.Submitted);

        // A review with only stars.
        var empty = await Rig.CreateAsync();
        empty.H.Client
            .Setup(c => c.GetReviewAsync(empty.H.Granted.Accommodation, empty.H.Granted.Refer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReview(new OctorateGuestReview { Published = true, Rating = 5m, Text = null }, null));
        var starsOnly = await empty.H.SyncAsync(empty.H.Granted);
        var outcome = await empty.DraftAsync(KeyOf(starsOnly));

        Assert.Equal(ReviewDraftStatus.Drafted, outcome.Status);
        Assert.Contains("(the guest wrote no text)", Assert.Single(empty.Ai.Calls).Request.Prompt, StringComparison.Ordinal);
    }
}
