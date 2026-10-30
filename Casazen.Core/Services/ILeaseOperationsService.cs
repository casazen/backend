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
    /// The agreement of the parties to renew or extend the lease, for the period from <paramref name="startsOn"/> to <paramref name="endsOn"/>
    /// (<paramref name="agreed"/>: an extension to a day agreed on; otherwise the standard renewal). It is built from the days given, so it can
    /// be prepared before the renewal is recorded and downloaded again after.
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_letter_not_applicable</c> (a type that does not renew), <c>lease_renewal_end_invalid</c>, <c>lease_letter_unavailable</c>.</exception>
    Task<LeaseLetterFile> BuildRinnovoAsync(
        Guid leaseId, DateOnly startsOn, DateOnly endsOn, bool agreed, CancellationToken cancellationToken = default);

    /// <summary>
    /// The confirmation that the need of a transitory lease goes on, from the landlord to the tenant, to be sent before the end of the lease.
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_letter_not_applicable</c> (not a transitory lease, or the need is the tenant's), <c>lease_letter_unavailable</c>.</exception>
    Task<LeaseLetterFile> BuildConfermaEsigenzaAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The ISTAT update of the rent worked out from the variation of the FOI index (<paramref name="foiPercent"/>, typed by the landlord) and the
    /// share of it the contract recognizes (<paramref name="sharePercent"/>). Nothing is written and no external service is called.
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_istat_waived</c> (cedolare secca), <c>lease_istat_no_rent</c>, <c>lease_istat_percent_invalid</c>, <c>lease_istat_no_increase</c>.</exception>
    Task<LeaseIstatCalculationDto> CalculateIstatAsync(Guid leaseId, decimal foiPercent, decimal sharePercent, CancellationToken cancellationToken = default);

    /// <summary>
    /// The request to update the rent, from <paramref name="effectiveFrom"/> (never in the past, not after the end of the period), with the numbers
    /// <see cref="CalculateIstatAsync"/> gives for the same percentages.
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>.</exception>
    /// <exception cref="DomainRuleException">422 the ones of <see cref="CalculateIstatAsync"/>, <c>lease_letter_effect_date_invalid</c>, <c>lease_letter_unavailable</c>.</exception>
    Task<LeaseLetterFile> BuildAggiornamentoIstatAsync(
        Guid leaseId, decimal foiPercent, decimal sharePercent, DateOnly effectiveFrom, CancellationToken cancellationToken = default);

    /// <summary>
    /// The communication to the tenant that the landlord has chosen the cedolare secca, with the renunciation of the updates of the rent; the
    /// option applies from <paramref name="effectiveFrom"/> when given (otherwise the letter leaves it to complete).
    /// </summary>
    /// <exception cref="NotFoundException">404 <c>lease_not_found</c>.</exception>
    /// <exception cref="DomainConflictException">409 <c>lease_not_in_force</c>.</exception>
    /// <exception cref="DomainRuleException">422 <c>lease_letter_not_applicable</c> (the lease is not under the cedolare secca), <c>lease_letter_effect_date_invalid</c>, <c>lease_letter_unavailable</c>.</exception>
    Task<LeaseLetterFile> BuildOpzioneCedolareAsync(Guid leaseId, DateOnly? effectiveFrom, CancellationToken cancellationToken = default);

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
