using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Leases.Reading;

namespace Casazen.Core.Services;

/// <summary>
/// The guided draft of a lease (LR-12): a draft that exists from the first step of the wizard, is saved step by step with values
/// that may be incomplete or wrong, can be taken up again identical, confirmed after a review, marked as downloaded for the
/// signature on paper, and deleted. The complete creation of a lease (<see cref="ILeaseWorkflowService.CreateDraftAsync"/>) is not
/// changed by it.
/// </summary>
/// <remarks>
/// <para>No method checks who the caller is: the web layer has authorized the lease (or the property) for <c>lease.create</c> as a
/// row of its property (TN-3) before it calls. The tenant filter keeps other orgs' leases invisible: they are not found.</para>
/// <para>Every method that changes the draft takes a row lock on the lease for the whole operation (PostgreSQL), so two saves of
/// the same draft are applied one after the other, and the second, which was made on the version the first replaced, is refused
/// with 409 <c>lease_draft_conflict</c>.</para>
/// </remarks>
public interface ILeaseDraftService
{
    /// <summary>The property of a lease visible to the caller's org, or <c>null</c>: how the web layer finds what to authorize.</summary>
    Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a draft with only the property (starting values for the columns that cannot be empty, step 1 reached, event
    /// <c>Created</c>). Nothing is asked of the property, the parties or the dates yet. <see cref="NotFoundException"/> 404
    /// <c>property_not_found</c> when the property is not visible.
    /// </summary>
    Task<LeaseDraftCreated> CreateAsync(Guid propertyId, LeaseOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a minimal ConcordatoFacile draft (ADR-004): PropertyId, OrgId, ContractType=Concordato, IsConcordatoFacileSource=true,
    /// other required fields from <see cref="LeaseDraftDefaults"/> (MonthlyRent=0, StartDate=PlaceholderDate). Idempotent: if a CF
    /// draft for the same property already exists in Draft status, returns the existing one. <see cref="NotFoundException"/> 404
    /// <c>property_not_found</c> when the property is not visible.
    /// </summary>
    Task<Guid> CreateCfDraftAsync(Guid propertyId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every value of the draft, not masked, with its version and its flow. Only for a lease that is a draft:
    /// <see cref="NotFoundException"/> 404 <c>lease_not_draft</c> for any other (the masked detail of every lease is
    /// <c>GET api/leases/{id}</c>).
    /// </summary>
    Task<LeaseDraftView> GetAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the values of one step (1 to 5): whatever they are, valid or not, complete or not. Changing the values of the first
    /// five steps after the review was confirmed withdraws the confirmation. 409 <c>lease_draft_conflict</c> when
    /// <paramref name="version"/> is not the current one, 409 <c>lease_not_draft</c> when the lease is no longer a draft.
    /// </summary>
    /// <exception cref="LeaseDraftValuesException">The values are not shaped like the step's (the web layer answers 400).</exception>
    Task<LeaseDraftSaved> SaveAsync(Guid leaseId, int version, int step, JsonElement values, CancellationToken cancellationToken = default);

    /// <summary>The review of the draft as it is now: the checks, worst first. 404 <c>lease_not_draft</c> for a lease that is not a draft.</summary>
    Task<LeaseReviewView> GetReviewAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the review: nothing blocking is left, the values of the first five steps are fingerprinted and the event
    /// <c>ReviewConfirmed</c> recorded. 422 <c>lease_review_blocked</c> while a check blocks. Confirming again changes nothing.
    /// </summary>
    Task<LeaseDraftReviewResult> ConfirmReviewAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    /// <summary>Withdraws the confirmation (and the "downloaded for signature" that rested on it). Withdrawing a confirmation that is not there changes nothing.</summary>
    Task<LeaseDraftReviewResult> ReopenReviewAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the contract was downloaded for the signature on paper: needs the confirmed review and the same gate as
    /// <c>GET contract.pdf</c> (422 <c>contract_template_not_approved</c> / <c>contract_data_missing</c>, so the state never says
    /// something false). The lease stays a draft. Marking again changes nothing.
    /// </summary>
    Task<LeaseDraftSentResult> MarkSentAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    /// <summary>Takes back "downloaded for signature" (event <c>SentRevoked</c>). Taking back what is not there changes nothing.</summary>
    Task<LeaseDraftSentResult> RevokeSentAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a draft with its parties, signers, events and every file tied to it. 409 <c>lease_not_deletable</c> for a lease
    /// that is not a draft, has a signed copy or has a rent plan. Closes BE-PM02-1: a property whose only lease was a draft can
    /// change mode again.
    /// </summary>
    Task DeleteAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts what was read in a contract document into an <b>imported</b> draft (NL-DOCS), the way a save would: under the row lock of the
    /// lease, through the same evaluation and the same writer of the columns and the parties, one version up. Only what is still empty is
    /// filled and nothing typed is overwritten (<see cref="LeaseDraftContractFill"/>). 409 <c>lease_not_draft</c> when the lease is no
    /// longer a draft, 409 <c>lease_draft_conflict</c> when it is busy; a lease that is not an imported draft is not touched (<c>Applied</c> false).
    /// </summary>
    Task<LeaseDraftFillResult> ApplyContractReadingAsync(Guid leaseId, LeaseContractReading reading, CancellationToken cancellationToken = default);
}

/// <summary>
/// What became of the values read in a contract: whether the draft was imported and changed, its version after, and each value with its
/// fate. No value of a person in it.
/// </summary>
public sealed record LeaseDraftFillResult(bool Applied, bool Changed, int Version, IReadOnlyList<FieldReading> Fields);

/// <summary>A new draft: its id, its version and its flow (every step still to do).</summary>
public sealed record LeaseDraftCreated(Guid Id, int Version, LeaseDraftFlow Flow);

/// <summary>
/// What the server computed from the values and does not take from the client: the last day of the first period (<c>yyyy-MM-dd</c>)
/// when the start date and the type are valid.
/// </summary>
public sealed record LeaseDraftComputed(string? EndDate, int? TermMonths);

/// <summary><c>GET api/leases/{id}/draft</c>: the draft, not masked.</summary>
public sealed record LeaseDraftView(
    Guid Id,
    int Version,
    LeaseOrigin Origin,
    int WizardMaxStep,
    DateTime? ReviewConfirmedAt,
    DateTime? SentForSignatureAt,
    DateTime UpdatedAt,
    LeaseDraftValues Values,
    LeaseDraftComputed Computed,
    LeaseDraftFlow Flow);

/// <summary>
/// <c>PATCH api/leases/{id}/draft</c>: the new version, the flow recomputed, and the ids of the entries of the parties in the order
/// of the lists (the server gives an id to every entry; the screens need them for step 5 and to find an entry again).
/// </summary>
public sealed record LeaseDraftSaved(
    int Version, LeaseDraftFlow Flow, DateTime UpdatedAt, IReadOnlyList<Guid> LandlordIds, IReadOnlyList<Guid> TenantIds);

/// <summary>The checks of the review of a draft.</summary>
public sealed record LeaseReviewView(int Version, DateTime? ReviewConfirmedAt, IReadOnlyList<LeaseReviewCheck> Checks, LeaseReviewCounts Counts);

/// <summary>How many checks of each severity the review has.</summary>
public sealed record LeaseReviewCounts(int Block, int Warn, int Info, int Ok);

/// <summary>The answer of confirming or withdrawing the review.</summary>
public sealed record LeaseDraftReviewResult(int Version, DateTime? ReviewConfirmedAt, LeaseDraftFlow Flow);

/// <summary>The answer of marking the contract as downloaded for the signature, or taking it back.</summary>
public sealed record LeaseDraftSentResult(int Version, DateTime? SentForSignatureAt, LeaseDraftFlow Flow);

/// <summary>
/// The data of the landlord that the wizard fills in for the person who is creating a lease (LR-12): the landlord of the last lease
/// the caller reaches, or else the fiscal data of the org and the name of the caller. Not masked, only for who may create a
/// lease. Every field may be <c>null</c>.
/// </summary>
public sealed record LandlordDefaultsDto(string? FirstName, string? LastName, string? FiscalCode, string? Residence, string? ContactEmail);

/// <summary>The landlord data the wizard suggests; see <see cref="LandlordDefaultsDto"/>.</summary>
public interface ILongRentLandlordDefaultsService
{
    /// <param name="scope">What the caller reaches (the leases it may read are the only ones the landlord is taken from).</param>
    /// <param name="userId">The caller: its name and e-mail are the fallback.</param>
    Task<LandlordDefaultsDto> GetAsync(HostScope scope, string userId, CancellationToken cancellationToken = default);
}

/// <summary>Deletes the drafts nobody touched for <c>LongRentLease:DraftRetentionDays</c> days (decision LR-D33).</summary>
public interface ILeaseDraftRetentionService
{
    /// <summary>One run: idempotent, never deletes a draft with a signed copy, takes a session lock so two runs never overlap.</summary>
    Task<LeaseDraftRetentionResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a run of <see cref="ILeaseDraftRetentionService"/>: whether it was skipped (another run holds the lock) and how many drafts it deleted.</summary>
public sealed record LeaseDraftRetentionResult(bool Skipped, int Deleted, DateTime Cutoff)
{
    public static LeaseDraftRetentionResult SkippedRun { get; } = new(true, 0, DateTime.MinValue);
}
