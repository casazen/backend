using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The kinds of document the archive holds (NF-99, REP-04, E07/F03/US03). The enum is the contract of the archive: a source registers one of
/// these. A type with no source registered has no rows and is offered to nobody (the Alloggiati receipts and the ISTAT proofs wait for their
/// tasks; the invoices, the receipts and the credit notes wait for the fiscal epic).
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

/// <summary>The key each type travels under in the API (never a text to show) and the rules that belong to a type, not to a source.</summary>
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

    /// <summary>The key of <paramref name="type"/> in the API: <c>propertyDocument</c>, <c>touristTax</c>, ...</summary>
    public static string KeyOf(ArchiveDocumentType type) =>
        KeysByType.TryGetValue(type, out var key) ? key : throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown archive document type.");

    /// <summary>The type a key names, whatever its case; false for any other word (a number included).</summary>
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

    /// <summary>What the document is called (the name of its file, or the comune a payment was made to).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The day of the document, a Europe/Rome calendar date (the day an upload was made, the day a payment was made).</summary>
    public DateOnly DocumentDate { get; init; }

    public Guid? PropertyId { get; init; }

    public string? PropertyName { get; init; }

    public Guid? BookingId { get; init; }

    public string? BookingCode { get; init; }

    /// <summary>
    /// The MIME type of the file. Empty when the source cannot say it in SQL: the service completes it from the extension of the title when it
    /// reads a page (<see cref="StorageKeys.ContentTypeFor"/>).
    /// </summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>The size of the file in bytes; <c>null</c> when the source does not store it.</summary>
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
    /// <summary>The most documents a page holds.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The page size when the caller does not say.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The longest search the archive looks for; a longer text is cut.</summary>
    public const int MaxSearchLength = 100;
}

/// <summary>A page of the archive: <paramref name="TotalCount"/> is all the documents that match, whatever the page.</summary>
public sealed record DocumentArchivePage(IReadOnlyList<ArchiveRow> Items, int TotalCount, int Page, int PageSize);

/// <summary>How many documents of one type match the filters of a query (without its type filter).</summary>
public sealed record ArchiveTypeCount(ArchiveDocumentType Type, int Count);

/// <summary>
/// The types the caller may read, decided by the web layer from the permissions of the caller (the service never checks roles). A type that is
/// not here has no row in the list, the count, the download or the export.
/// </summary>
public sealed record DocumentArchiveAccess(IReadOnlySet<ArchiveDocumentType> Types)
{
    /// <summary>Nothing may be read.</summary>
    public static DocumentArchiveAccess None { get; } = new(new HashSet<ArchiveDocumentType>());

    public bool Allows(ArchiveDocumentType type) => Types.Contains(type);
}

/// <summary>A file read from the private storage, ready to be sent: the caller disposes <see cref="Content"/>.</summary>
public sealed record ArchiveFile(Stream Content, string FileName, string ContentType);

/// <summary>
/// A place the archive reads documents from. A source knows its table, who may read it and how to open a file; the archive knows how to union,
/// filter, page, download and compress. The fiscal epic adds its documents by registering a source of its own.
/// </summary>
public interface IDocumentArchiveSource
{
    /// <summary>The type of every row of this source.</summary>
    ArchiveDocumentType Type { get; }

    /// <summary>
    /// The context that grants <see cref="PermissionKey"/> (<c>short-rent</c>, <c>long-rent</c>); several contexts separated by <c>|</c> mean
    /// that any of them does, as in the context policies of the API.
    /// </summary>
    string ContextKey { get; }

    /// <summary>The permission that reads the area the documents belong to (<c>property.read</c>, <c>payment.read</c>, <c>lease.read</c>, <c>guest.read</c>).</summary>
    string PermissionKey { get; }

    /// <summary>
    /// The rows of the caller: those of its org, and, when its scope is limited to some properties, only those of properties it reaches;
    /// a row with no property only for a scope that reaches the whole org. Always an explicit <c>OrgId</c> predicate, applied in SQL, and the
    /// same member-initialiser as the other sources.
    /// </summary>
    IQueryable<ArchiveRow> Query(HostScope scope);

    /// <summary>
    /// Opens the file of a row of the caller's reach (the same reach as <see cref="Query"/>); <c>null</c> when there is no such row, the row has
    /// no file or the file is gone from the storage.
    /// </summary>
    Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken);
}
