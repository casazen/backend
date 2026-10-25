using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>
/// A document of a lease. For a contract that was read, <paramref name="Sections"/> says which pages are the contract, the attestation
/// and the calculation sheet of the file (CF-DOCS-REAL): when the file holds the attestation, the person is not asked for it again.
/// </summary>
/// <param name="Sections">The sections the reading recognized, in page order; empty when the document was not read or is not a contract.</param>
/// <param name="ContainsAttestation">A section of the file is the attestation of conformity.</param>
/// <param name="ContainsCalculationSheet">A section of the file is the calculation sheet of the rent.</param>
public sealed record LeaseDocumentDto(
    Guid Id,
    Guid LeaseId,
    LeaseDocumentKind Kind,
    string FileName,
    long SizeBytes,
    DateTime UploadedAt,
    IReadOnlyList<LeaseDocumentSectionDto>? Sections = null,
    bool ContainsAttestation = false,
    bool ContainsCalculationSheet = false);

/// <summary>A run of pages of a document that is one thing: <c>Kind</c> is <c>contract</c>, <c>attestation</c> or <c>calculationSheet</c>; pages count from 1.</summary>
public sealed record LeaseDocumentSectionDto(string Kind, int FirstPage, int LastPage);

/// <summary>One thing worth a second look that the reading found: a stable code (<c>ContractReadingWarnings</c>) and, when it is about pages, which.</summary>
public sealed record LeaseReadingWarningDto(string Code, IReadOnlyList<int>? Pages);

public sealed record LeaseDocumentDownload(Stream Content, string FileName);

/// <summary>
/// How far the reading of the contract documents of a draft has got (NL-DOCS, <c>GET api/leases/{id}/draft/reading</c>): the contract
/// documents with their status, the values read and what became of each, the day of the signature and the document the final import can use.
/// No value of a person in it: the field names, the ids of the entries of the draft, the states and how sure the reading was.
/// </summary>
/// <param name="State"><see cref="LeaseReadingStates"/>.</param>
/// <param name="Documents">The documents of kind Contract, oldest first.</param>
/// <param name="Fields">What the reading found, one entry per field (and per person's field): the screen marks the ones in state <c>Read</c> and tells the <c>Conflict</c> ones.</param>
/// <param name="StipulaDate"><c>yyyy-MM-dd</c>, when the contract says the day it was signed.</param>
/// <param name="ContractDocumentId">The first contract document: the PDF the final import takes instead of a file sent again.</param>
public sealed record LeaseReadingDto(
    string State,
    IReadOnlyList<LeaseReadingDocumentDto> Documents,
    IReadOnlyList<LeaseReadingFieldDto> Fields,
    string? StipulaDate,
    Guid? ContractDocumentId);

/// <summary>
/// A contract document and where its reading is; <c>Error</c> is one of <c>ContractReadingCodes</c> when the status is Failed. Once read, the
/// <paramref name="Sections"/> the file is made of (the contract, its attestation, its calculation sheet) and the <paramref name="Warnings"/>
/// the coherence check found (CF-DOCS-REAL): codes only, the screen puts them in words.
/// </summary>
public sealed record LeaseReadingDocumentDto(
    Guid Id,
    string FileName,
    LeaseDocumentExtractionStatus Status,
    string? Error,
    IReadOnlyList<LeaseDocumentSectionDto>? Sections = null,
    bool ContainsAttestation = false,
    bool ContainsCalculationSheet = false,
    IReadOnlyList<LeaseReadingWarningDto>? Warnings = null);

/// <summary>One value of the contract and its fate: <c>State</c> is Read, Agrees, Conflict or Unusable (<c>FieldReadingState</c>).</summary>
public sealed record LeaseReadingFieldDto(string Field, Guid? PartyId, string? Role, string State, int Confidence, bool Derived);

/// <summary>The values of <see cref="LeaseReadingDto.State"/>.</summary>
public static class LeaseReadingStates
{
    /// <summary>No contract document has been uploaded.</summary>
    public const string None = "None";

    /// <summary>A document is still being read: ask again in a few seconds.</summary>
    public const string Reading = "Reading";

    /// <summary>Every document is read and at least one gave something.</summary>
    public const string Done = "Done";

    /// <summary>Every document is read and none could be: the wizard goes on by hand.</summary>
    public const string Failed = "Failed";
}

public interface ILeaseDocumentService
{
    Task<IReadOnlyList<LeaseDocumentDto>> ListAsync(Guid leaseId, CancellationToken ct = default);
    Task<LeaseDocumentDto> UploadAsync(Guid leaseId, IFormFile file, LeaseDocumentKind kind, string uploadedBy, CancellationToken ct = default);
    Task<LeaseDocumentDownload?> DownloadAsync(Guid leaseId, Guid documentId, CancellationToken ct = default);

    /// <summary>Where the reading of the contract documents of a lease is (NL-DOCS).</summary>
    Task<LeaseReadingDto> GetReadingAsync(Guid leaseId, CancellationToken ct = default);
}
