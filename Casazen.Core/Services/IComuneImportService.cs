using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Import of the official ISTAT comuni list (SU-04), the same path for the admin upload and for the seed file of the
/// deploy. Atomic (one invalid line rejects the whole file), idempotent (the same file again changes nothing: upsert by
/// ISTAT code) and logged with source, reference date and SHA-256. File format and steps: <c>docs/runbooks/comuni-istat.md</c>.
/// </summary>
public interface IComuneImportService
{
    /// <summary>Imports a file of the list. Rejections are returned in <see cref="ComuneImportResult.Errors"/>, never thrown.</summary>
    Task<ComuneImportResult> ImportAsync(
        ComuneImportRequest request,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the seed file of the deploy (<c>Data/Seeds/comuni-istat.csv</c> with its <c>comuni-istat.source.json</c>) when
    /// there is one and the database has no list or an older one (by reference date): never replaces a list that is as
    /// recent or more, e.g. one an admin imported later. Does nothing when the file is not part of the build.
    /// </summary>
    Task<ComuneSeedOutcome> ImportSeedIfNewerAsync(CancellationToken cancellationToken = default);
}

/// <param name="FileName">Name of the file, kept in the log of the import.</param>
/// <param name="SourceVersion">Source and edition, e.g. "ISTAT, Elenco dei comuni italiani, aggiornato al 21/02/2026".</param>
/// <param name="ReferenceDate">Date the list is valid at (the "aggiornato al" date of the official file).</param>
/// <param name="ImportedBy">Auth0 id of the admin, or <c>system</c>.</param>
/// <param name="Origin">Admin upload or seed file.</param>
/// <param name="IsPartial">The file is a part of the list: the comuni it does not have stay as they are.</param>
public sealed record ComuneImportRequest(
    string FileName,
    string SourceVersion,
    DateOnly ReferenceDate,
    string ImportedBy,
    ComuneImportOrigin Origin = ComuneImportOrigin.AdminUpload,
    bool IsPartial = false);

/// <summary>Outcome of an import; <see cref="Success"/> is false when the file was rejected (nothing was written).</summary>
public sealed record ComuneImportResult(
    Guid? ImportId,
    int Rows,
    int Inserted,
    int Updated,
    int Unchanged,
    int Deactivated,
    IReadOnlyList<ComuneImportLineError> Errors)
{
    public bool Success => Errors.Count == 0;

    public static ComuneImportResult Rejected(params ComuneImportLineError[] errors) => new(null, 0, 0, 0, 0, 0, errors);
}

/// <param name="Line">1-based line of the file; 0 for the file as a whole.</param>
/// <param name="Error">Stable snake_case code (<see cref="ComuneImportErrors"/>).</param>
public sealed record ComuneImportLineError(int Line, string Error);

/// <summary>What the seed file did at startup.</summary>
public enum ComuneSeedOutcome
{
    /// <summary>The build has no seed file (or no metadata): nothing to load.</summary>
    NoSeedFile = 0,

    /// <summary>The seed file was imported.</summary>
    Imported = 1,

    /// <summary>The database already has a list as recent as the seed file, or more: left alone.</summary>
    AlreadyUpToDate = 2,

    /// <summary>The seed file or its metadata is invalid: nothing was written (logged).</summary>
    Rejected = 3,
}

/// <summary>Error codes of an import, per line or for the whole file.</summary>
public static class ComuneImportErrors
{
    // File as a whole
    public const string EmptyFile = "empty_file";
    public const string FileTooLarge = "file_too_large";
    public const string UnreadableEncoding = "unreadable_encoding";
    public const string NoRows = "no_rows";
    public const string SourceVersionMissing = "source_version_missing";
    public const string ReferenceDateInFuture = "reference_date_in_future";
    public const string ReferenceDateOlderThanCurrent = "reference_date_older_than_current";

    // Header
    public const string IstatColumnMissing = "istat_column_missing";
    public const string NameColumnMissing = "name_column_missing";
    public const string CadastralColumnMissing = "cadastral_column_missing";
    public const string ProvinceColumnMissing = "province_column_missing";
    public const string RegionColumnMissing = "region_column_missing";

    // Row
    public const string ColumnCountMismatch = "column_count_mismatch";
    public const string IstatInvalid = "istat_invalid";
    public const string NameMissing = "name_missing";
    public const string NameTooLong = "name_too_long";
    public const string CadastralInvalid = "cadastral_invalid";
    public const string ProvinceInvalid = "province_invalid";
    public const string RegionUnknown = "region_unknown";
    public const string RegionInconsistent = "region_inconsistent";
    public const string DateInvalid = "date_invalid";
    public const string ValidityInconsistent = "validity_inconsistent";
    public const string DuplicateIstatCode = "duplicate_istat_code";
    public const string DuplicateCadastralCode = "duplicate_cadastral_code";

    /// <summary>The rows are valid but clash with comuni already stored (a cadastral code held by another active comune).</summary>
    public const string ConflictsWithStoredComuni = "conflicts_with_stored_comuni";
}
