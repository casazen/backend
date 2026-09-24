using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// Official Alloggiati Web code tables (comuni, stati, tipi documento, tipi alloggiato): import of the file downloaded
/// from the portal by an admin, status, search for the forms and resolution of the guests' codes (CO-12). The tables are
/// empty until an admin imports them: CasaZen ships no code (RS-1: never committed, never invented).
/// </summary>
public interface IAlloggiatiCodeTableService
{
    /// <summary>Rows and last import of every table.</summary>
    Task<IReadOnlyList<AlloggiatiCodeTableStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Entries of <paramref name="tables"/> whose description starts with (then contains) <paramref name="query"/>,
    /// ignoring case, accents and punctuation; at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<AlloggiatiCodeEntryInfo>> SearchAsync(
        IReadOnlyList<AlloggiatiCodeTable> tables,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>The codes of the imported tables needed to resolve <paramref name="guests"/>.</summary>
    Task<AlloggiatiCodeBook> LoadCodeBookAsync(IEnumerable<StayGuest> guests, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the codes of <paramref name="table"/> with the content of an official file (format in
    /// <c>docs/runbooks/alloggiati.md</c>). All or nothing: any invalid line rejects the whole file.
    /// </summary>
    Task<AlloggiatiCodeImportResult> ImportAsync(
        AlloggiatiCodeTable table,
        Stream content,
        string fileName,
        string sourceVersion,
        string importedBy,
        CancellationToken cancellationToken = default);
}

public sealed record AlloggiatiCodeTableStatus(
    AlloggiatiCodeTable Table,
    int RowCount,
    DateTime? ImportedAt,
    string? SourceVersion,
    string? SourceFileName);

public sealed record AlloggiatiCodeEntryInfo(AlloggiatiCodeTable Table, string Code, string Description, string? Province);

/// <param name="Line">1-based line of the file (0 for the file as a whole).</param>
/// <param name="Error">Stable snake_case code (see <see cref="AlloggiatiCodeImportErrors"/>).</param>
public sealed record AlloggiatiCodeImportLineError(int Line, string Error);

public sealed record AlloggiatiCodeImportResult(
    AlloggiatiCodeTable Table,
    int RowCount,
    Guid? ImportId,
    IReadOnlyList<AlloggiatiCodeImportLineError> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>Error codes of an import, per line or for the whole file.</summary>
public static class AlloggiatiCodeImportErrors
{
    public const string EmptyFile = "empty_file";
    public const string FileTooLarge = "file_too_large";
    public const string UnreadableEncoding = "unreadable_encoding";
    public const string CodeColumnMissing = "code_column_missing";
    public const string DescriptionColumnMissing = "description_column_missing";
    public const string CodeMissing = "code_missing";
    public const string CodeInvalid = "code_invalid";
    public const string DescriptionMissing = "description_missing";
    public const string DescriptionTooLong = "description_too_long";
    public const string ProvinceInvalid = "province_invalid";
    public const string DuplicateCode = "duplicate_code";
    public const string NoRows = "no_rows";
    public const string SourceVersionMissing = "source_version_missing";
}
