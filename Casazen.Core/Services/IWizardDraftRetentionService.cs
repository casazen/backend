namespace Casazen.Core.Services;

/// <summary>
/// The nightly retention of the wizard drafts (ADR-024-R5), run by <c>GdprDataRetentionJob</c>: a draft past its expiry (30 days from its
/// last save) is anonymized, which takes the data and the person out of the row, and the row stays for the audit trail: it is never deleted.
/// </summary>
/// <remarks>
/// A platform job: it works across every org on purpose (the tenant filter is bypassed), it is reachable from no request, and it only
/// lets go of data. Idempotent: an anonymized row has no user, so a second run finds nothing to do.
/// </remarks>
public interface IWizardDraftRetentionService
{
    /// <summary>Anonymizes every draft whose expiry has passed and returns how many it was.</summary>
    Task<WizardDraftRetentionRun> ApplyRetentionAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one run of <see cref="IWizardDraftRetentionService.ApplyRetentionAsync"/> did.</summary>
/// <param name="Drafts">The drafts anonymized by the run.</param>
public sealed record WizardDraftRetentionRun(int Drafts);
