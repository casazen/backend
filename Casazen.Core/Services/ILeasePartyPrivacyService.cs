namespace Casazen.Core.Services;

/// <summary>
/// Personal data of the parties of a long-term lease (LT-12, A7-18, #179; runbook <c>docs/runbooks/gdpr.md</c> § 7).
/// The parties of a lease are anonymized only once the lease has ended (the day after its end date, Europe/Rome):
/// <list type="bullet">
/// <item>on an erasure request (art. 17 GDPR), from the day after the end date;</item>
/// <item>by the retention period <c>Gdpr:Retention:LeaseParties</c> counted from the end date, only when configured with
/// its source (no period is invented: without it nothing is anonymized by retention).</item>
/// </list>
/// A party whose fiscal code is also a party of another lease of the same org that has not ended keeps its data until
/// that lease ends too. Idempotent: anonymized parties and leases carry a marker and are never processed again.
/// </summary>
public interface ILeasePartyPrivacyService
{
    /// <summary>
    /// Records the erasure request of the lease's parties (flag and <c>ErasureRequested</c> event, once) and, when the lease
    /// has already ended, anonymizes them now. A lease of another org raises <c>NotFoundException</c> (tenant filter).
    /// </summary>
    Task<LeaseErasureResult> RequestErasureAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nightly run over every org: anonymizes the parties of the ended leases with an erasure request and of the leases
    /// whose retention period ended.
    /// </summary>
    Task<LeasePartyRetentionRunResult> ApplyRetentionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Where an erasure request of a lease stands.</summary>
public enum LeaseErasureStatus
{
    /// <summary>Every party of the lease is anonymized.</summary>
    Anonymized,

    /// <summary>
    /// The lease has ended but some parties keep their data because they are parties of another lease that has not ended
    /// (<see cref="LeaseErasureResult.PartiesKept"/>): the nightly job anonymizes them when that lease ends.
    /// </summary>
    PartiallyAnonymized,

    /// <summary>The lease has not ended: the parties are anonymized from <see cref="LeaseErasureResult.AnonymizationFrom"/>.</summary>
    Scheduled,
}

/// <param name="Status">Outcome of the request.</param>
/// <param name="AnonymizationFrom">
/// Calendar day (midnight UTC of the Rome date) from which the nightly job anonymizes the parties of a lease that has not
/// ended; null otherwise.
/// </param>
/// <param name="PartiesKept">Parties not anonymized yet because they are parties of another lease that has not ended.</param>
public sealed record LeaseErasureResult(LeaseErasureStatus Status, DateTime? AnonymizationFrom, int PartiesKept);

/// <param name="RetentionConfigured">Whether <c>Gdpr:Retention:LeaseParties</c> has a period and its source.</param>
/// <param name="Leases">Leases whose parties are now all anonymized.</param>
/// <param name="Parties">Parties anonymized by this run.</param>
/// <param name="PartiesKept">Parties of due leases kept because they are parties of another lease that has not ended.</param>
public sealed record LeasePartyRetentionRunResult(bool RetentionConfigured, int Leases, int Parties, int PartiesKept);
