using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IOtaMessageRetentionService"/> (E05/F01/US01/T03, ADR-032-R13, docs/runbooks/gdpr.md): the copy of the OTA conversations is guest data
/// and follows the retention of the guest data of its stay. A system job over every org (the tenant filter is off, every query spans all orgs on
/// purpose); the anonymization itself is <see cref="GuestDataEraser.AnonymizeOtaThreadsAsync"/>, the one place that knows which fields are personal.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which period.</b> <c>Gdpr:Retention:AlloggiatiData</c>, the period of the data of the guests of a stay, counted from the check-out of the stay
/// (the day after check-out plus the period, Europe/Rome, like every category). There is no period of its own for the conversations: that is the
/// assumption of the brief, and the product owner confirms or changes it. <b>Without a period and its source cited nothing is anonymized</b>.</item>
/// <item><b>Which threads.</b> The threads of a booking whose stay is over (a booking still pending, confirmed or in the house is left alone); a thread
/// with no booking (an inquiry, or a conversation whose booking CasaZen does not hold) has no stay to count from, so its period is counted from its last
/// message.</item>
/// <item><b>Once.</b> A thread that was anonymized carries a mark and is not selected again by the period; the read of the channel manager brings nothing
/// of it back. The one thing done again, whichever the period, is the repair of a marked thread that holds a name or a link of a person again because a
/// read of the channel manager raced the mark (<see cref="GuestDataEraser.HoldingPersonalData"/>): the erasure and the sync do not wait for each other.</item>
/// <item><b>Privacy.</b> The log carries counts, never an id of a person, a name or a text.</item>
/// </list>
/// </remarks>
public sealed class OtaMessageRetentionService(
    AppDbContext db,
    GuestDataEraser eraser,
    IOptions<GdprOptions> gdprOptions,
    TimeProvider timeProvider,
    ILogger<OtaMessageRetentionService> logger) : IOtaMessageRetentionService
{
    private const int ChunkSize = 100;

    public async Task<OtaMessageRetentionResult> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        // Whatever the period: a conversation that is marked as anonymized but holds a name or a link again is emptied again. It is no decision of the
        // retention, it finishes the work of an erasure or of an earlier run that a read of the channel manager raced.
        var repaired = await RepairMarkedThreadsAsync(cancellationToken);

        var period = gdprOptions.Value.Retention.AlloggiatiData;
        if (!period.IsConfigured)
        {
            logger.LogWarning(
                "GDPR retention: the copy of the OTA conversations is not applied ({Problem}); nothing of it is anonymized until " +
                "Gdpr:Retention:AlloggiatiData has a period and its source (docs/runbooks/gdpr.md)",
                period.ConfigurationProblem);
            return new OtaMessageRetentionResult(false, repaired.Threads, repaired.Messages);
        }

        var today = timeProvider.TodayInRome();
        var candidateStartBefore = period.CandidateStartBefore(today);

        // The threads of a stay that is over: the period counts from the check-out.
        var stays = await db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(t => t.AnonymizedAt == null
                        && t.BookingId != null
                        && t.Booking != null
                        && !GuestDataRetentionService.OpenBookingStatuses.Contains(t.Booking.Status)
                        && t.Booking.CheckOutDate < candidateStartBefore)
            .Select(t => new { t.Id, t.Booking!.CheckOutDate })
            .ToListAsync(cancellationToken);

        // The threads with no booking: there is no stay to count from, so the last message is the reference.
        var requests = await db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(t => t.AnonymizedAt == null && t.BookingId == null && t.LastMessageAt < candidateStartBefore)
            .Select(t => new { t.Id, CheckOutDate = t.LastMessageAt })
            .ToListAsync(cancellationToken);

        var due = stays.Concat(requests)
            .Where(t => period.HasEnded(t.CheckOutDate, today))
            .Select(t => t.Id)
            .ToList();

        var anonymized = await AnonymizeInChunksAsync(due, cancellationToken);
        var threads = anonymized.Threads + repaired.Threads;
        var messages = anonymized.Messages + repaired.Messages;

        logger.LogInformation(
            "GDPR retention: the copy of the OTA conversations applied: {Threads} threads, {Messages} messages anonymized", threads, messages);
        return new OtaMessageRetentionResult(true, threads, messages);
    }

    /// <summary>
    /// The threads that are marked as anonymized while a message of theirs still has a sender's name or a link to an attachment (see
    /// <see cref="GuestDataEraser.HoldingPersonalData"/>): a read of the channel manager that ran while the guest was erased stored it after the erasure
    /// had read the thread, and neither waits for the other. Emptied again, whichever the period.
    /// </summary>
    private async Task<(int Threads, int Messages)> RepairMarkedThreadsAsync(CancellationToken ct)
    {
        var marked = db.OtaMessageThreads
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(t => t.AnonymizedAt != null);
        var ids = await eraser.HoldingPersonalData(marked).Select(t => t.Id).ToListAsync(ct);
        if (ids.Count == 0)
            return (0, 0);

        var repaired = await AnonymizeInChunksAsync(ids, ct);
        logger.LogInformation(
            "GDPR retention: {Threads} conversations already marked as anonymized held a name or a link again, {Messages} messages emptied",
            repaired.Threads,
            repaired.Messages);
        return repaired;
    }

    private async Task<(int Threads, int Messages)> AnonymizeInChunksAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        int threads = 0, messages = 0;
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var outcome = await eraser.AnonymizeOtaThreadsAsync(chunk, now, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            threads += outcome.Threads;
            messages += outcome.Messages;
        }

        return (threads, messages);
    }
}
