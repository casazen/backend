using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

/// <summary>What one run of the hourly job <c>lease-term-renewal</c> did: the leases brought forward, the periods added, the leases left for the next run.</summary>
/// <param name="Skipped">Another run held the lock of the job: nothing was done.</param>
public sealed record LeaseTermRenewalRun(bool Skipped, int Leases, int Periods, int Left)
{
    public static LeaseTermRenewalRun SkippedRun { get; } = new(true, 0, 0, 0);
}

/// <summary>
/// The end of a long-term lease in force (LR-14): the tacit renewal that moves <c>CurrentTermEndDate</c> forward, the notice of
/// non-renewal that stops it, and the early termination that brings the end back. A libero (4+4) or concordato (3+2) lease that nobody gave
/// notice of renews by itself for one more period (decision D5 of the interview of the area); the transitory one never does.
/// </summary>
/// <remarks>
/// <para>No method checks who the caller is: the web layer has authorized the lease (<c>lease.create</c> on its property, TN-3, AM-03)
/// before it calls, and the tenant filter keeps other orgs' leases invisible (404 <c>lease_not_found</c>). The renewal is the job's: it
/// has no caller and reads every org.</para>
/// <para>Every change takes a row lock on the lease for the whole operation (PostgreSQL) and reads the lease after it, so a notice, an
/// early termination and a renewal of the same lease are applied one after the other, each on what the previous one committed. A lease
/// that stays locked for more than a few seconds answers 409 <c>lease_busy</c> and nothing is written.</para>
/// <para>Nothing here writes to a third party: no e-mail is sent to a tenant by a renewal, a notice or a termination. The only money
/// the changes touch is the rent plan of the lease, through <see cref="IRentBillingService"/>, in the same transaction.</para>
/// </remarks>
public interface ILeaseTermService
{
    /// <summary>The property of a lease visible to the caller's org, or <c>null</c>: how the web layer finds what to authorize.</summary>
    Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the notice of non-renewal given on <paramref name="on"/> (a day from the start of the lease up to today) by
    /// <paramref name="from"/>: the lease ends with its current period and does not renew. Event <c>NoticeRecorded</c>.
    /// </summary>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>, <c>lease_notice_already_recorded</c>, <c>lease_already_terminated</c>, <c>lease_busy</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_type_has_no_renewal</c> (transitory), <c>lease_notice_date_invalid</c>.</exception>
    Task RecordNoticeAsync(Guid leaseId, LeaseNoticeParty from, DateOnly on, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes back the notice, so the lease renews by itself again; only while the current period has not ended. Taking back a notice that
    /// is not there changes nothing. Event <c>NoticeRemoved</c>.
    /// </summary>
    /// <exception cref="DomainConflictException">409 <c>lease_term_already_ended</c>, <c>lease_busy</c>.</exception>
    Task RemoveNoticeAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the lease on <paramref name="on"/>, its last day: after the start of the lease and no later than the end of the current period (a
    /// day in the past included), and only once the lease has begun (LR-TERM): the end of the period becomes that day, the renewal stops for
    /// good, and the installments of the rent plan
    /// that <b>start after</b> that day and are not paid are cancelled (a payment already made or in flight is never touched; the one
    /// installment that straddles the day stays as it is). Irreversible. Event <c>Terminated</c>.
    /// </summary>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>, <c>lease_busy</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_termination_date_invalid</c>, with <c>min</c> and <c>max</c> in its extensions.</exception>
    Task TerminateAsync(Guid leaseId, DateOnly on, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the withdrawal (recesso) the tenant sent, received on <paramref name="receivedOn"/> (a day from the start of the lease up to
    /// today), and ends the lease on the day the notice runs out (LR-OPS-1): <paramref name="receivedOn"/> plus the months of notice, which are
    /// the ones of the rules (<c>LongRentRules</c>) for serious reasons and <paramref name="noticeMonths"/> (the rules' when omitted) when the
    /// contract gave the faculty. The ending is the early termination of <see cref="TerminateAsync"/>, with the same rules and the same effects
    /// (<c>start &lt; last day ≤ end of the period</c>, the rent plan cancelled after it), in the same transaction; irreversible. Events
    /// <c>WithdrawalRecorded</c> and <c>Terminated</c>.
    /// </summary>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>, <c>lease_busy</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_withdrawal_date_invalid</c>, <c>lease_withdrawal_notice_invalid</c>, and <c>lease_termination_date_invalid</c> (with <c>min</c> and <c>max</c>) when the day the notice runs out is not inside the period.</exception>
    Task RecordWithdrawalAsync(
        Guid leaseId, DateOnly receivedOn, TenantWithdrawalReason reason, int? noticeMonths, CancellationToken cancellationToken = default);

    /// <summary>
    /// The hourly job: every lease that renews by itself and whose period is over is brought forward one period at a time (at most
    /// <c>LeaseRenewal.MaxPeriodsPerRun</c> in a run), an event <c>TermRenewed</c> for each, and an active rent plan is extended to the new end.
    /// Idempotent: a lease that was renewed is not due any more, so a second run, a retry or a manual trigger finds nothing to do. It
    /// never touches a lease with a notice, ended early, of a transitory type, or not signed by every party.
    /// </summary>
    Task<LeaseTermRenewalRun> RenewDueAsync(CancellationToken cancellationToken = default);
}
