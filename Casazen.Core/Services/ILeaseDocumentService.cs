using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public sealed record LeaseDocumentDto(Guid Id, Guid LeaseId, LeaseDocumentKind Kind, string FileName, long SizeBytes, DateTime UploadedAt);
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

/// <summary>A contract document and where its reading is; <c>Error</c> is one of <c>ContractReadingCodes</c> when the status is Failed.</summary>
public sealed record LeaseReadingDocumentDto(Guid Id, string FileName, LeaseDocumentExtractionStatus Status, string? Error);

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
