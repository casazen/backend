using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Read side of the official ISTAT comuni list (SU-04): search for the pickers, validation of the code a property or a
/// supplier profile stores, and resolution of the identifiers still stored by older data (cadastral code, name). Empty
/// until the list is imported (<c>docs/runbooks/comuni-istat.md</c>): callers say so (<see cref="IsAvailableAsync"/>)
/// instead of treating an empty answer as "no such comune".
/// </summary>
public interface IComuneDirectory
{
    Task<ComuneDatasetStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The comune with this ISTAT code. <paramref name="activeOnly"/> false also finds a comune no longer in the list (a
    /// merger, a suppression): a stored code stays resolvable. <c>null</c> for a blank or malformed code.
    /// </summary>
    Task<Comune?> FindByIstatCodeAsync(string? istatCode, bool activeOnly = true, CancellationToken cancellationToken = default);

    /// <summary>The comuni of these ISTAT codes (any status), by code; a code that is not in the list is absent.</summary>
    Task<IReadOnlyDictionary<string, Comune>> GetByIstatCodesAsync(
        IEnumerable<string> istatCodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Active comuni whose name starts with, then contains, <paramref name="query"/> (accents, case and punctuation
    /// ignored; also the name in the other language), exact name first, then the shortest. A 6-digit query is an ISTAT
    /// code, a letter and 3 digits a cadastral code. At most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<Comune>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The comune each identifier stands for: an ISTAT code, a cadastral code (<c>H501</c>) or a name (<c>Roma</c>). A name
    /// is resolved only when exactly one active comune has it; an identifier that is not found, or is ambiguous, is absent.
    /// Keys are the identifiers as given (trimmed, case-insensitive). Never a guess: used for the identifiers that older
    /// data keeps as free text.
    /// </summary>
    Task<IReadOnlyDictionary<string, Comune>> ResolveAsync(
        IEnumerable<string> identifiers,
        CancellationToken cancellationToken = default);

    Task<Comune?> FindByNameAndProvinceAsync(string name, string provinceCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Comune>> FindByNamesAndProvincesAsync(
        IEnumerable<(string Name, string ProvinceCode)> pairs,
        CancellationToken cancellationToken = default);
}

public sealed record ComuneDatasetStatus(int TotalRows, int ActiveRows, ComuneImportInfo? LastImport)
{
    public bool Available => ActiveRows > 0;
}

public sealed record ComuneImportInfo(
    Guid Id,
    ComuneImportOrigin Origin,
    string SourceFileName,
    string SourceVersion,
    DateOnly ReferenceDate,
    string Sha256,
    int RowCount,
    int InsertedCount,
    int UpdatedCount,
    int UnchangedCount,
    int DeactivatedCount,
    bool IsPartial,
    DateTime ImportedAt,
    string ImportedBy);
