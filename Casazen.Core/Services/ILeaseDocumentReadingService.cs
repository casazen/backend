namespace Casazen.Core.Services;

/// <summary>
/// What the receipt of the registration of a lease says (CF-DOCS-REAL), read from the PDF the person uploaded: the figures the Agenzia delle
/// Entrate recorded, to be checked by the person who declares the registration. Holds no name, no fiscal code and no street. Dates are
/// <c>yyyy-MM-dd</c>.
/// </summary>
/// <param name="Outcome"><c>Registered</c> or <c>Rejected</c> (the Agenzia discarded the request: the contract is NOT registered).</param>
/// <param name="Code">The identification code of the contract: what the declaration of the registration asks as the number.</param>
/// <param name="Warnings">Codes of what disagrees with the lease (<c>RegistrationReceiptWarnings</c>): never a block.</param>
public sealed record RegistrationReceiptDto(
    string Outcome,
    string? Code,
    string? Number,
    string? Series,
    string? RegistrationDate,
    string? Office,
    decimal? AnnualRent,
    string? Start,
    string? End,
    string? StipulationDate,
    string? Typology,
    bool? CedolareSecca,
    string? Comune,
    string? Province,
    IReadOnlyList<string> Warnings);

/// <summary>The reading of one document of a lease, on request (<c>GET api/leases/{id}/documents/{documentId}/reading</c>).</summary>
/// <param name="State"><see cref="LeaseDocumentReadingStates"/>.</param>
/// <param name="Receipt">The receipt, when <paramref name="State"/> is <c>Read</c>.</param>
public sealed record LeaseDocumentReadingDto(Guid DocumentId, string Kind, string State, RegistrationReceiptDto? Receipt);

/// <summary>The values of <see cref="LeaseDocumentReadingDto.State"/>.</summary>
public static class LeaseDocumentReadingStates
{
    /// <summary>The PDF is a receipt of the Agenzia delle Entrate and its values are in <see cref="LeaseDocumentReadingDto.Receipt"/>.</summary>
    public const string Read = "Read";

    /// <summary>The PDF holds no text (a scan or a photo): the person types the number and the date.</summary>
    public const string Unreadable = "Unreadable";

    /// <summary>The PDF holds text, but it is not a receipt of the Agenzia delle Entrate.</summary>
    public const string NotAReceipt = "NotAReceipt";
}

/// <summary>
/// Reads, when the person asks, the receipt of a registration they uploaded to a lease (CF-DOCS-REAL), so the declaration of the registration
/// is filled in with what the Agenzia wrote and the person checks it. Nothing is declared by the reading and no value is kept: the file is
/// read again on every request, in process, without OCR.
/// </summary>
public interface ILeaseDocumentReadingService
{
    /// <summary>
    /// The reading of a document of kind <c>Registration</c> of this lease, or <c>null</c> when there is no such document (another lease,
    /// another organization, another kind, or the file is gone): the same answer for every one of those cases.
    /// </summary>
    Task<LeaseDocumentReadingDto?> ReadAsync(Guid leaseId, Guid documentId, CancellationToken cancellationToken = default);
}
