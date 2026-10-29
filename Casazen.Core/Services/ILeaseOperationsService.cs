using Casazen.Core.DTOs.Leases;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

/// <summary>A letter as a PDF, with the name to save it as (no name of a person in it).</summary>
public sealed record LeaseLetterFile(byte[] Pdf, string FileName);

/// <summary>
/// The operations of a lease in force (LR-OPS-1): what the landlord can do about the end of the lease, the letters CasaZen prepares for it
/// and the record that a letter was sent. CasaZen prepares, the person signs and sends: nothing here writes to the tenant (no e-mail, no PEC,
/// no registered mail), and nothing keeps a copy of a letter (it is built again from the lease when asked).
/// </summary>
/// <remarks>
/// <para>No method checks who the caller is: the web layer has authorized the lease (<c>lease.create</c> on its property, TN-3, AM-03) before it
/// calls, and the tenant filter keeps other orgs' leases invisible (404 <c>lease_not_found</c>).</para>
/// <para>A letter is printed with the watermark «BOZZA — testo non ancora approvato» until the product owner approves its text on the host
/// (<c>LeaseLetters__Variants__{Kind}__…</c>); the PDF carries the residences of the parties, which are read here on the server and are
/// never in a log or in the read model. The withdrawal itself (which ends the lease) is <see cref="ILeaseTermService.RecordWithdrawalAsync"/>.</para>
/// </remarks>
public interface ILeaseOperationsService
{
    /// <summary>The property of a lease visible to the caller's org, or <c>null</c>: how the web layer finds what to authorize.</summary>
    Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>What the landlord can do now, and the state of each letter.</summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    Task<LeaseOperationsDto> GetAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The letter of the notice of non-renewal. At the first expiry <paramref name="reason"/> is the reason of the law the letter states and is
    /// required; at a later one it is ignored.
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_type_has_no_renewal</c> (transitory), <c>lease_disdetta_reason_required</c>, <c>lease_letter_unavailable</c>.</exception>
    Task<LeaseLetterFile> BuildDisdettaAsync(Guid leaseId, LeaseDisdettaReason? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The acknowledgement of a withdrawal received on <paramref name="receivedOn"/> (a day from the start of the lease up to today). The last
    /// day of the lease it states is the one <see cref="ILeaseTermService.RecordWithdrawalAsync"/> would end it on.
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_withdrawal_date_invalid</c>, <c>lease_withdrawal_notice_invalid</c>, <c>lease_letter_unavailable</c>.</exception>
    Task<LeaseLetterFile> BuildPresaAttoRecessoAsync(
        Guid leaseId, DateOnly receivedOn, TenantWithdrawalReason reason, int? noticeMonths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the landlord sent a letter on <paramref name="sentOn"/> (a day from the start of the lease up to today) by
    /// <paramref name="means"/>: an event <c>LetterSent</c> in the history, and nothing more (the lease does not change).
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_letter_date_invalid</c>.</exception>
    Task MarkLetterSentAsync(
        Guid leaseId, LeaseLetterKind kind, DateOnly sentOn, LeaseLetterMeans means, CancellationToken cancellationToken = default);
}
