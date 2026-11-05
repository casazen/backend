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
/// (<see cref="WizardDraft.Anonymize"/>) it has none, so a second run finds nothing.</para>
/// <para><b>Overwritten, never read.</b> The due drafts are changed by one set-based <c>UPDATE</c> (<c>ExecuteUpdateAsync</c>) and are not
/// loaded: loading a draft would decrypt <see cref="WizardDraft.EncryptedFieldsJson"/> only to throw it away, and a payload that can no
/// longer be decrypted (Data Protection keys lost or rotated, the risk ADR-024 names) would fail the read every night and stop the
/// anonymization of every draft after it, of every org. The <c>UPDATE</c> never touches the keys, so an unreadable draft is anonymized
/// like any other. The values it writes are those of <see cref="WizardDraft.Anonymize"/>, which the discard of a draft uses: keep the
/// two in step.</para>
/// <para><b>Atomic.</b> The predicate that picks the rows is evaluated by PostgreSQL on the current version of each row: a draft that a
/// person saves at that very moment (its expiry moves forward) is no longer due when the <c>UPDATE</c> reaches it, and is left alone.</para>
/// <para>Nothing about the content of a draft or the person it belonged to is logged: only the number of rows.</para>
/// </remarks>
public sealed class WizardDraftRetentionService(
    AppDbContext db,
    ILogger<WizardDraftRetentionService> logger,
    TimeProvider? timeProvider = null) : IWizardDraftRetentionService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<WizardDraftRetentionRun> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

        var anonymized = await db.WizardDrafts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(d => d.ExpiresAt < now && d.UserId != string.Empty)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.DraftJson, WizardDraft.EmptyJson)
                    .SetProperty(d => d.EncryptedFieldsJson, (string?)null)
                    .SetProperty(d => d.UserId, string.Empty),
                cancellationToken);

        logger.LogInformation("GDPR retention: wizard drafts applied: {Drafts} drafts anonymized", anonymized);
        return new WizardDraftRetentionRun(anonymized);
    }
}
