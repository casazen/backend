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

public sealed record LeaseDocumentReadingDto(Guid DocumentId, string Kind, string State, RegistrationReceiptDto? Receipt);

public static class LeaseDocumentReadingStates
{
    /// <summary>The PDF is a receipt of the Agenzia delle Entrate and its values are in <see cref="LeaseDocumentReadingDto.Receipt"/>.</summary>
    public const string Read = "Read";

    public const string Unreadable = "Unreadable";

    /// <summary>The PDF holds text, but it is not a receipt of the Agenzia delle Entrate.</summary>
    public const string NotAReceipt = "NotAReceipt";
}

public interface ILeaseDocumentReadingService
{
    Task<LeaseDocumentReadingDto?> ReadAsync(Guid leaseId, Guid documentId, CancellationToken cancellationToken = default);
}
