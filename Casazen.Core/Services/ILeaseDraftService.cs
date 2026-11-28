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
    Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseDraftCreated> CreateAsync(Guid propertyId, LeaseOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a minimal ConcordatoFacile draft (ADR-004): PropertyId, OrgId, ContractType=Concordato, IsConcordatoFacileSource=true,
    /// other required fields from <see cref="LeaseDraftDefaults"/> (MonthlyRent=0, StartDate=PlaceholderDate). It is an <b>imported</b>
    /// draft (CF-IMPORT, origin <see cref="LeaseOrigin.Imported"/>): the contract is written and signed by the partner, so the draft is
    /// filled from it (the documents are read like those of any imported draft) and imported as a signed contract. Idempotent: if a CF
    /// draft for the same property already exists in Draft status, returns the existing one. <see cref="NotFoundException"/> 404
    /// <c>property_not_found</c> when the property is not visible.
    /// </summary>
    Task<Guid> CreateCfDraftAsync(Guid propertyId, Guid orgId, CancellationToken cancellationToken = default);

    Task<LeaseDraftView> GetAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseDraftSaved> SaveAsync(Guid leaseId, int version, int step, JsonElement values, CancellationToken cancellationToken = default);

    Task<LeaseReviewView> GetReviewAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseDraftReviewResult> ConfirmReviewAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    Task<LeaseDraftReviewResult> ReopenReviewAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the contract was downloaded for the signature on paper: needs the confirmed review and the same gate as
    /// <c>GET contract.pdf</c> (422 <c>contract_template_not_approved</c> / <c>contract_data_missing</c>, so the state never says
    /// something false). The lease stays a draft. Marking again changes nothing.
    /// </summary>
    Task<LeaseDraftSentResult> MarkSentAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    Task<LeaseDraftSentResult> RevokeSentAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts what was read in a contract document into an <b>imported</b> draft (NL-DOCS), the way a save would: under the row lock of the
    /// lease, through the same evaluation and the same writer of the columns and the parties, one version up. Only what is still empty is
    /// filled and nothing typed is overwritten (<see cref="LeaseDraftContractFill"/>). 409 <c>lease_not_draft</c> when the lease is no
    /// longer a draft, 409 <c>lease_draft_conflict</c> when it is busy; a lease that is not an imported draft is not touched (<c>Applied</c> false).
    /// 404 <c>document_not_found</c> when <paramref name="documentId"/> is no longer a document of this lease: nothing is written.
    /// </summary>
    Task<LeaseDraftFillResult> ApplyContractReadingAsync(Guid leaseId, LeaseContractReading reading, Guid documentId, CancellationToken cancellationToken = default);
}

public sealed record LeaseDraftFillResult(bool Applied, bool Changed, int Version, IReadOnlyList<FieldReading> Fields);

public sealed record LeaseDraftCreated(Guid Id, int Version, LeaseDraftFlow Flow);

public sealed record LeaseDraftComputed(string? EndDate, int? TermMonths);

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

public sealed record LeaseDraftSaved(
    int Version, LeaseDraftFlow Flow, DateTime UpdatedAt, IReadOnlyList<Guid> LandlordIds, IReadOnlyList<Guid> TenantIds);

public sealed record LeaseReviewView(int Version, DateTime? ReviewConfirmedAt, IReadOnlyList<LeaseReviewCheck> Checks, LeaseReviewCounts Counts);

public sealed record LeaseReviewCounts(int Block, int Warn, int Info, int Ok);

public sealed record LeaseDraftReviewResult(int Version, DateTime? ReviewConfirmedAt, LeaseDraftFlow Flow);

public sealed record LeaseDraftSentResult(int Version, DateTime? SentForSignatureAt, LeaseDraftFlow Flow);

public sealed record LandlordDefaultsDto(string? FirstName, string? LastName, string? FiscalCode, string? Residence, string? ContactEmail);

public interface ILongRentLandlordDefaultsService
{
    Task<LandlordDefaultsDto> GetAsync(HostScope scope, string userId, CancellationToken cancellationToken = default);
}

public interface ILeaseDraftRetentionService
{
    /// <summary>One run: idempotent, never deletes a draft with a signed copy, takes a session lock so two runs never overlap.</summary>
    Task<LeaseDraftRetentionResult> RunAsync(CancellationToken cancellationToken = default);
}

public sealed record LeaseDraftRetentionResult(bool Skipped, int Deleted, DateTime Cutoff)
{
    public static LeaseDraftRetentionResult SkippedRun { get; } = new(true, 0, DateTime.MinValue);
}
