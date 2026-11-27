using Casazen.Core.Assistant;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IReviewReplyDrafter"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One call, through the platform.</b> The reply is written on the Standard tier of <see cref="IAiProvider"/>, which goes through the monthly budget when an external provider is
/// active (<see cref="IAiBudgetGuard"/>, as the drafts of the messages do); the prompt is <see cref="ReviewReplyPromptBuilder"/>'s, built from the text of the review minimized by
/// <see cref="AssistantPromptBuilder.Minimize"/> with <see cref="PiiScrubber.Scrub"/>. A budget used up or a provider that fails is a reason, never a failed event: there is no draft and the
/// code is logged.</item>
/// <item><b>Nothing is sent.</b> The only write of this class is the item of the approval queue (<see cref="IApprovalQueueService.SubmitForApprovalAsync"/>) and the rejection of the one it
/// supersedes. <b>It cannot send, by its types:</b> it reads the review of a channel through <see cref="IOtaReviewReader"/>, the part of the service of the reviews that only reads, and it takes
/// no client of the channel manager, no <see cref="IOtaReviewService"/> and no <see cref="IHostReviewService"/>, so there is no <c>ReplyAsync</c> or send of a host's review within its reach
/// (the architecture test of the drafting path keeps the constructor so): the reply leaves in the approve action only, after a person approved it.</item>
/// <item><b>Once.</b> A review that has a draft waiting for approval, or one approved in the last days, gets no second one from the automatic path (an event delivered twice, two jobs): only a
/// person who asks for a new draft writes another, and the one that waits is superseded.</item>
/// <item><b>Logs</b> carry ids and codes: never a word of the review, a name, a code of the booking or a word of the draft.</item>
/// </list>
/// </remarks>
public sealed class ReviewReplyDrafter(
    AppDbContext db,
    IAssistantRulesService rules,
    IAiRuntimeSettings aiSettings,
    IAiProvider ai,
    IApprovalQueueService approvals,
    IOtaReviewReader otaReviews,
    ILogger<ReviewReplyDrafter> logger) : IReviewReplyDrafter
{
    /// <summary>The reason a draft that was written longer than a reply can be is not queued.</summary>
    public const string DraftTooLongReason = "draft_too_long";

    public async Task<ReviewDraftOutcome> DraftAsync(Guid orgId, MessageThreadKey reviewKey, ReviewDraftOptions? options = null, CancellationToken ct = default)
    {
        options ??= ReviewDraftOptions.Automatic;
        if (!reviewKey.IsReview)
            return ReviewDraftOutcome.NotFound;

        // A job has no caller: it reads the whole org. A person reads in its own reach, and a reach of another org is no reach at all.
        var scope = options.RequestedBy ?? new HostScope(orgId);
        if (scope.OrgId != orgId)
            return ReviewDraftOutcome.NotFound;

        var facts = reviewKey.Source == MessageThreadSource.OtaReview
            ? await OtaFactsAsync(scope, reviewKey.Id, ct)
            : await DirectFactsAsync(scope, reviewKey.Id, ct);
        if (facts is null)
            return ReviewDraftOutcome.NotFound;

        if (facts.Blocked is { } blocked)
            return NoDraft(reviewKey, orgId, blocked);

        // The draft that waits (or was approved a few days ago): the automatic path writes no second one; a person who asks supersedes the one that waits.
        var waiting = await WaitingDraftsAsync(orgId, facts.ReviewId, ct);
        if (!options.Requested && waiting.Count > 0)
            return NoDraft(reviewKey, orgId, ReviewDraftReasons.AlreadyDrafted);

        var effective = await rules.ResolveEffectiveAsync(orgId, facts.PropertyId, ct);
        if (!effective.Enabled)
            return NoDraft(reviewKey, orgId, ReviewDraftReasons.AssistantDisabled);

        var aiOptions = await aiSettings.GetEffectiveAsync(ct);
        if (!aiOptions.IsExternalProviderActive())
            return NoDraft(reviewKey, orgId, ReviewDraftReasons.AiProviderNotConfigured);

        // The review reaches the model without the names of the guest and of the host, the codes of the booking, the contact data and the long numbers (PO-09).
        var minimized = AssistantPromptBuilder.Minimize(facts.Text, facts.Names, PiiScrubber.Scrub);
        var signature = effective.Signature?.Trim();
        var room = Math.Max(200, ReviewRules.ReplyMaxLength - (string.IsNullOrEmpty(signature) ? 0 : signature.Length + 2));
        var request = ReviewReplyPromptBuilder.Build(facts.Rating, minimized, effective, room);

        var (content, failure) = await CallAsync(request, facts.ReviewId, ct);
        if (failure is not null)
            return NoDraft(reviewKey, orgId, failure);

        var reply = ReviewReplyPromptBuilder.ParseReply(content);
        if (reply is null)
        {
            logger.LogWarning("The answer of the AI to the reply to review {ReviewId} (org {OrgId}) could not be used ({Length} characters)", facts.ReviewId, orgId, content?.Length ?? 0);
            return NoDraft(reviewKey, orgId, ReviewDraftReasons.AiInvalidAnswer);
        }

        // What a reply to a review never says (a promise of money, being an AI, a link or a number): checked on the server, whatever the instructions asked.
        if (ReviewReplyPromptBuilder.Violation(reply) is { } violation)
        {
            logger.LogWarning("The reply the AI wrote to review {ReviewId} (org {OrgId}) breaks a rule ({Violation}): it is not queued", facts.ReviewId, orgId, violation);
            return NoDraft(reviewKey, orgId, ReviewDraftReasons.DraftRuleViolation);
        }

        var finalText = AssistantPromptBuilder.WithSignature(reply, signature);
        if (finalText.Length > ReviewRules.ReplyMaxLength)
            return NoDraft(reviewKey, orgId, DraftTooLongReason);

        var item = await approvals.SubmitForApprovalAsync(
            orgId,
            ApprovalItemTypes.ReviewReply,
            facts.ReviewId,
            finalText,
            isAiGenerated: true,
            contentVersion: ReviewReplyPromptBuilder.Version,
            propertyId: facts.PropertyId,
            originAction: ApprovalOriginActions.SendReviewReply,
            threadKey: reviewKey.ToString(),
            cancellationToken: ct);

        if (options.Requested)
            await SupersedeAsync(options, orgId, waiting.Where(i => i.Status == ApprovalStatus.Pending).Select(i => i.Id).Where(id => id != item.Id).ToList(), ct);

        logger.LogInformation("Review {ReviewId} (org {OrgId}): the draft of the reply waits for approval (item {ItemId})", facts.ReviewId, orgId, item.Id);
        return ReviewDraftOutcome.Drafted(item.Id);
    }

    private ReviewDraftOutcome NoDraft(MessageThreadKey key, Guid orgId, string reason)
    {
        logger.LogInformation("Review {ReviewKey} (org {OrgId}) got no draft of a reply: {Reason}", key.ToString(), orgId, reason);
        return ReviewDraftOutcome.NotDrafted(reason);
    }

    // ─── What the draft is about ───

    /// <summary>What the draft needs of a review: where it is, how it was rated, what it says, why it cannot be answered (a code, or <c>null</c>) and the names and codes the prompt never carries.</summary>
    private sealed record ReviewFacts(Guid ReviewId, Guid PropertyId, decimal? Rating, string? Text, string? Blocked, AssistantPromptNames Names);

    private async Task<ReviewFacts?> OtaFactsAsync(HostScope scope, Guid reviewId, CancellationToken ct)
    {
        OtaReviewView view;
        try
        {
            view = await otaReviews.GetAsync(scope, reviewId, ct);
        }
        catch (NotFoundException)
        {
            return null;
        }

        string? blocked = null;
        if (!view.Capabilities.CanRead)
            blocked = ReviewDraftReasons.ChannelNotSupported;
        else if (!view.GuestReviewPublished)
            blocked = ReviewDraftReasons.NotPublished;
        else if (view.Reply.State is OtaReviewActionState.Sent or OtaReviewActionState.ManualVerify)
            blocked = ReviewDraftReasons.AlreadyAnswered;
        else if (!view.Capabilities.CanReply)
            blocked = ReviewDraftReasons.ReplyNotPossible;

        return new ReviewFacts(
            view.Id,
            view.PropertyId,
            view.GuestRating,
            view.GuestText,
            blocked,
            await NamesAsync(scope.OrgId, view.BookingId, ct));
    }

    private async Task<ReviewFacts?> DirectFactsAsync(HostScope scope, Guid reviewId, CancellationToken ct)
    {
        var review = await db.Reviews
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.Id == reviewId && r.Status != ReviewStatus.Removed)
            .InScope(scope)
            .Select(r => new { r.PropertyId, r.BookingId, r.Rating, r.Text, Answered = r.HostReply != null })
            .FirstOrDefaultAsync(ct);
        if (review is null)
            return null;

        return new ReviewFacts(
            reviewId,
            review.PropertyId,
            review.Rating,
            review.Text,
            review.Answered ? ReviewDraftReasons.AlreadyAnswered : null,
            await NamesAsync(scope.OrgId, review.BookingId, ct));
    }

    /// <summary>The names and codes of the stay the prompt must never carry: the guest, the host, the code of the booking and its reference on the channel manager.</summary>
    private async Task<AssistantPromptNames> NamesAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var facts = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .Select(b => new
            {
                b.Guest.FirstName,
                b.Guest.LastName,
                b.BookingCode,
                b.OctorateReservationId,
                HostName = b.Org.HostName,
                OrgName = b.Org.DisplayName,
            })
            .FirstOrDefaultAsync(ct);
        if (facts is null)
            return AssistantPromptNames.None;

        var guest = $"{facts.FirstName} {facts.LastName}".Trim();
        return new AssistantPromptNames(
            [facts.FirstName, facts.LastName, guest],
            [facts.HostName, string.Equals(facts.OrgName, Org.PlaceholderName, StringComparison.Ordinal) ? null : facts.OrgName],
            [facts.BookingCode, facts.OctorateReservationId]);
    }

    // ─── The AI ───

    /// <summary>One call to the provider. The budget used up and any failure of the provider are codes, never exceptions; a provider that did not answer is «not configured».</summary>
    private async Task<(string? Content, string? Failure)> CallAsync(AiRequest request, Guid reviewId, CancellationToken ct)
    {
        try
        {
            var result = await ai.GenerateAsync(request, AiModelTier.Standard, ReviewReplyPromptBuilder.CallLabel(reviewId), ct);
            return result.ProviderConfigured ? (result.Content, null) : (null, ReviewDraftReasons.AiProviderNotConfigured);
        }
        catch (AiBudgetExceededException)
        {
            logger.LogWarning("The AI budget of the platform is used up: the reply to review {ReviewId} was not drafted", reviewId);
            return (null, ReviewDraftReasons.AiBudgetExceeded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout of the provider is a TaskCanceledException too: it is the provider that did not answer, not the caller who left. The type only: never the prompt.
            logger.LogWarning("The AI provider failed on the reply to review {ReviewId} ({ErrorType})", reviewId, ex.GetType().Name);
            return (null, ReviewDraftReasons.AiProviderError);
        }
    }

    // ─── The queue ───

    private sealed record WaitingDraft(Guid Id, ApprovalStatus Status);

    /// <summary>The drafts of the review that wait for a decision, or that were approved in the last days (the window in which the same item delivered again is not a new one).</summary>
    private async Task<List<WaitingDraft>> WaitingDraftsAsync(Guid orgId, Guid reviewId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-ApprovalQueueRules.ResubmitWindowDays);
        return await db.ApprovalQueueItems
            .AsNoTracking()
            .Where(i => i.OrgId == orgId
                        && i.ItemType == ApprovalItemTypes.ReviewReply
                        && i.ItemId == reviewId
                        && (i.Status == ApprovalStatus.Pending || (i.Status == ApprovalStatus.Approved && i.ApprovedAt >= since)))
            .Select(i => new WaitingDraft(i.Id, i.Status))
            .ToListAsync(ct);
    }

    /// <summary>The drafts of the review that still wait are rejected with <see cref="AssistantDecisionCodes.RegeneratedReason"/>, in the reach of the person who asked for a new one.</summary>
    private async Task SupersedeAsync(ReviewDraftOptions options, Guid orgId, IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        // Only a person supersedes, in its own reach: a request with nobody behind it supersedes nothing.
        if (options.RequestedBy is not { } requestedBy || options.RequestedByUserId is not { Length: > 0 } userId)
            return;

        foreach (var itemId in itemIds)
        {
            try
            {
                await approvals.RejectAsync(requestedBy, itemId, userId, AssistantDecisionCodes.RegeneratedReason, ct);
            }
            catch (Exception ex) when (ex is NotFoundException or DomainConflictException)
            {
                // Decided meanwhile, or out of the reach of the person: it is not this request's to supersede.
                logger.LogInformation("The waiting draft {ItemId} of org {OrgId} was not superseded ({ErrorType})", itemId, orgId, ex.GetType().Name);
            }
        }
    }
}
