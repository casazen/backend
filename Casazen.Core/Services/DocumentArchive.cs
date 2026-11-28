using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The kinds of document the archive holds (NF-99, REP-04, E07/F03/US03). The enum is the contract of the archive: a source registers one of
/// these. A type with no source registered has no rows and is offered to nobody (the invoices, the receipts and the credit notes wait for the
/// fiscal epic; the Alloggiati receipts and the ISTAT proofs the host uploaded are «Prove di adempimento», read with <c>guest.read</c>, E20/F02/US01/T01).
/// </summary>
public enum ArchiveDocumentType
{
    Invoice,
    Receipt,
    CreditNote,
    AlloggiatiReceipt,
    IstatProof,
    TouristTax,
    Contract,
    PropertyDocument,
}

public static class ArchiveDocumentTypes
{
    private static readonly IReadOnlyDictionary<ArchiveDocumentType, string> KeysByType = new Dictionary<ArchiveDocumentType, string>
    {
        [ArchiveDocumentType.Invoice] = "invoice",
        [ArchiveDocumentType.Receipt] = "receipt",
        [ArchiveDocumentType.CreditNote] = "creditNote",
        [ArchiveDocumentType.AlloggiatiReceipt] = "alloggiatiReceipt",
        [ArchiveDocumentType.IstatProof] = "istatProof",
        [ArchiveDocumentType.TouristTax] = "touristTax",
        [ArchiveDocumentType.Contract] = "contract",
        [ArchiveDocumentType.PropertyDocument] = "propertyDocument",
    };

    public static string KeyOf(ArchiveDocumentType type) =>
        KeysByType.TryGetValue(type, out var key) ? key : throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown archive document type.");

    public static bool TryParse(string? key, out ArchiveDocumentType type)
    {
        var word = key?.Trim();
        foreach (var (candidate, name) in KeysByType)
        {
            if (string.Equals(word, name, StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>
    /// The documents that prove a compliance duty (the Alloggiati receipts and the ISTAT proofs): reading one writes a line in the log, as an
    /// export does. They carry data about guests, so their sources ask <c>guest.read</c>, which the Accountant does not hold.
    /// </summary>
    public static bool IsComplianceProof(ArchiveDocumentType type) =>
        type is ArchiveDocumentType.AlloggiatiReceipt or ArchiveDocumentType.IstatProof;
}

/// <summary>
/// One document of the archive as the list shows it: a row of a read model, never a copy of the file. The sources fill it in SQL, all with the
/// same member-initialiser, so that the provider can concatenate their queries into one statement.
/// </summary>
public sealed record ArchiveRow
{
    public ArchiveDocumentType Type { get; init; }

    public Guid Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public Guid? PropertyId { get; init; }

    public string? PropertyName { get; init; }

    public Guid? BookingId { get; init; }

    public string? BookingCode { get; init; }

    public string ContentType { get; init; } = string.Empty;

    public long? SizeBytes { get; init; }
}

/// <summary>
/// What a caller asks of the archive. <see cref="Types"/> <c>null</c> is every type the caller may read; an empty list is none (never
/// everything). The days are Europe/Rome dates, both included. <see cref="Search"/> looks in the title, the name of the property and the code
/// of the booking.
/// </summary>
public sealed record DocumentArchiveQuery(
    IReadOnlyCollection<ArchiveDocumentType>? Types,
    DateOnly? From,
    DateOnly? To,
    Guid? PropertyId,
    Guid? BookingId,
    string? Search,
    int Page,
    int PageSize)
{
    public const int MaxPageSize = 100;

    public const int DefaultPageSize = 25;

    public const int MaxSearchLength = 100;
}

public sealed record DocumentArchivePage(IReadOnlyList<ArchiveRow> Items, int TotalCount, int Page, int PageSize);

public sealed record ArchiveTypeCount(ArchiveDocumentType Type, int Count);

public sealed record DocumentArchiveAccess(IReadOnlySet<ArchiveDocumentType> Types)
{
    public static DocumentArchiveAccess None { get; } = new(new HashSet<ArchiveDocumentType>());

    public bool Allows(ArchiveDocumentType type) => Types.Contains(type);
}

public sealed record ArchiveFile(Stream Content, string FileName, string ContentType);

public sealed record DocumentArchiveExportResult(int Documents, int Files, long Bytes);

public interface IDocumentArchiveSource
{
    ArchiveDocumentType Type { get; }

    string ContextKey { get; }

    string PermissionKey { get; }

    /// <summary>
    /// The rows of the caller: those of its org, and, when its scope is limited to some properties, only those of properties it reaches;
    /// a row with no property only for a scope that reaches the whole org. Always an explicit <c>OrgId</c> predicate, applied in SQL, and the
    /// same member-initialiser as the other sources.
    /// </summary>
    IQueryable<ArchiveRow> Query(HostScope scope);

    Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken);
}
