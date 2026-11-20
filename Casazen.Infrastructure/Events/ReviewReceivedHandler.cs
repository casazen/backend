using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A guest's review appeared (<see cref="DomainEventType.ReviewReceived"/>: the review of the booking site, or the review of a channel the channel manager showed as published, E05/F06/US01/T03, NF-69):
/// the assistant is asked to write the reply for the host to approve, in a job of its own (<see cref="ReviewReplyDraftJob"/>), so the dispatcher of the events never waits for the model.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only behind its switches.</b> Nothing is queued while <c>Features:MessageAssistant</c> is off, nor for an organization that has not switched its own assistant on
/// (<c>OrgAssistantRules.Enabled</c>); the property's own exception and the budget are asked by the drafter when the job runs, which also asks the flag again.</item>
/// <item><b>Never automatic.</b> This class only queues a job; the job only writes an item of the approval queue. A reply to a review is public, so it leaves, if ever, from the approve action
/// of that item and from nowhere else (<c>ReviewReplyApprovalAction</c>).</item>
/// <item><b>Once.</b> The drafter writes no second draft for a review that has one waiting or approved a short time ago, so an event delivered again queues a job that does nothing. A failure of the
/// queue is not swallowed here: the exception reaches the dispatcher, which delivers the event again.</item>
/// <item><b>The org is the event's</b> (ADR-022-R6): the job carries it, and every read names it. The review is found by its id among the reviews of that org only: the one of a channel, or the one of the
/// booking site; the key says which. The payload is not read (ids only, ADR-022-R21), and the log carries the event and the review.</item>
/// </list>
/// </remarks>
public sealed class ReviewReceivedHandler(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IBackgroundJobClient jobs,
    ILogger<ReviewReceivedHandler> logger) : IEventHandler<DomainEvent>
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.ReviewReceived || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.MessageAssistant))
            return;

        var orgId = domainEvent.OrgId;
        var reviewId = domainEvent.EntityId;
        var switchedOn = await db.OrgAssistantRules
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(r => r.OrgId == orgId && r.Enabled, ct);
        if (!switchedOn)
            return;

        if (await KeyOfAsync(orgId, reviewId, ct) is not { } key)
        {
            // Written in the same save as the review, so it is there when the event is delivered; a review that is gone has nothing to answer.
            logger.LogWarning("Event {EventId} ({EventType}) of org {OrgId} names a review that is not there: nothing is queued", domainEvent.Id, domainEvent.Type, orgId);
            return;
        }

        var reviewKey = key.ToString();
        var jobId = jobs.Enqueue<ReviewReplyDraftJob>(job => job.RunAsync(orgId, reviewKey, CancellationToken.None));
        logger.LogInformation("Review {ReviewKey} (org {OrgId}) goes to the assistant (job {JobId})", reviewKey, orgId, jobId);
    }

    /// <summary>The key of the review of the event: the one of a channel when the id is a row of the reviews of the channels of the org, else the one of the booking site.</summary>
    private async Task<MessageThreadKey?> KeyOfAsync(Guid orgId, Guid reviewId, CancellationToken ct)
    {
        if (await db.OtaReviews.IgnoreQueryFilters([Tenant]).AsNoTracking().AnyAsync(r => r.OrgId == orgId && r.Id == reviewId, ct))
            return MessageThreadKey.ForOtaReview(reviewId);

        if (await db.Reviews.IgnoreQueryFilters([Tenant]).AsNoTracking().AnyAsync(r => r.OrgId == orgId && r.Id == reviewId, ct))
            return MessageThreadKey.ForReview(reviewId);

        return null;
    }
}
