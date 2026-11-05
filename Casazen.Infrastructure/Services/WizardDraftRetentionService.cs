using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IWizardDraftRetentionService"/>
/// <remarks>
/// <para><b>A system job.</b> The nightly retention works across every org on purpose (as the retention of the guests, of the lease parties
/// and of the supplier customers): it is run only by Hangfire (<c>gdpr-data-retention</c>) and reachable from no request, so its reads
/// bypass the tenant filter (<c>IgnoreQueryFilters</c>).</para>
/// <para><b>Marked by what is gone.</b> A draft is due while its expiry has passed and it still has a user; once anonymized
/// (<see cref="WizardDraft.Anonymize"/>) it has none, so a second run finds nothing. The rows are read in batches and changed through
/// their own entity: PostgreSQL's <c>xmin</c> turns a draft that a person saves at that very moment (its expiry moves forward) into a
/// concurrency failure, and the batch is read again without it.</para>
/// <para>Nothing about the content of a draft or the person it belonged to is logged: only the number of rows.</para>
/// </remarks>
public sealed class WizardDraftRetentionService(
    AppDbContext db,
    ILogger<WizardDraftRetentionService> logger,
    TimeProvider? timeProvider = null) : IWizardDraftRetentionService
{
    private const int BatchSize = 200;

    // A run that always finds more (or is always beaten by the saves of the people) stops here; the night after takes up the rest.
    private const int MaxBatches = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<WizardDraftRetentionRun> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);
        var anonymized = 0;

        for (var batch = 0; batch < MaxBatches; batch++)
        {
            db.ChangeTracker.Clear();
            var due = await db.WizardDrafts
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(d => d.ExpiresAt < now && d.UserId != string.Empty)
                .OrderBy(d => d.ExpiresAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;

            foreach (var draft in due)
                draft.Anonymize();

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                anonymized += due.Count;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A person saved one of these drafts just now, so it is no longer over (the batch was not written at all): read it again.
            }
        }

        db.ChangeTracker.Clear();
        logger.LogInformation("GDPR retention: wizard drafts applied: {Drafts} drafts anonymized", anonymized);
        return new WizardDraftRetentionRun(anonymized);
    }
}
