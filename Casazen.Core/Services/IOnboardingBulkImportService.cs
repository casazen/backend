using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The import of the data that changes from one property to the next (comune and region, the Alloggiati Web room numbers, the lock code) from a CSV
/// or an Excel file, with the check of every row, so an agency fills 250 properties without 250 wizards (NF-105, ONB-02, E10/F02/US01/T02).
/// </summary>
/// <remarks>
/// <para><b>Stateless, in two calls.</b> The same call validates and answers each row; with <c>dryRun</c> it stops there and writes nothing, without it
/// it validates again and writes ONLY the valid rows, each in its own unit of work: an invalid row never blocks a valid one. A valid row writes only
/// what differs from the stored value: one that repeats what the property holds already, or that has only its key, writes nothing and is not
/// counted as applied, so a second import of the same file answers no applied row. The file is read from memory and never stored.</para>
/// <para><b>The file.</b> <c>.csv</c> (UTF-8; <c>,</c> or <c>;</c> found on the header) or <c>.xlsx</c>, at most 2 MB and 1,000 rows. Another type
/// is 415, a larger file 413, more rows 422. The first row is the header; the columns are named in Italian or English (see
/// <see cref="BulkImportColumns"/>) and an unknown one is reported once, not on every row. An Excel file is read with the framework only: its first
/// sheet, shared strings, inline strings and numbers; a formula is never evaluated (its cached value is read); a file that unzips to more than
/// 20 MB or holds more than 200 entries is refused.</para>
/// <para><b>Reach.</b> A row names its property by <c>propertyId</c> or by <c>cinCode</c>; the property is looked up in the org of the scope and
/// among what the scope reaches (<c>InScope</c>, AM-03). A property of another org, one outside the reach and one that does not exist are the same
/// row status, <see cref="BulkImportRowCodes.PropertyNotFound"/>: nothing reveals that it exists.</para>
/// <para><b>The lock code</b> is the only secret of the file. It is stored in the encrypted <c>PropertyAccessCode</c> (Data Protection purpose
/// of the access codes), is never logged and never comes back in any answer: a row says only whether it had one
/// (<see cref="BulkImportRow.LockCodeProvided"/>).</para>
/// </remarks>
public interface IOnboardingBulkImportService
{
    /// <summary>
    /// Reads <paramref name="content"/> (the bytes of the file named <paramref name="fileName"/>), checks every row and, unless
    /// <paramref name="dryRun"/>, writes the valid ones.
    /// </summary>
    /// <remarks>
    /// Errors: 415 <see cref="BulkImportCodes.FileTypeUnsupported"/>, 413 <see cref="BulkImportCodes.FileTooLarge"/>, 422
    /// <see cref="BulkImportCodes.TooManyRows"/>, <see cref="BulkImportCodes.FileUnreadable"/>, <see cref="BulkImportCodes.FileEmpty"/>,
    /// <see cref="BulkImportCodes.ColumnsUnrecognized"/>, and 422 <c>comuni_dataset_unavailable</c> when a row carries a comune and the official list is
    /// not imported (the comune cannot be checked, so no row is judged on it).
    /// </remarks>
    Task<BulkImportResult> ImportAsync(HostScope scope, string actorUserId, string fileName, byte[] content, bool dryRun, CancellationToken ct);
}

public static class BulkImportColumns
{
    public const string PropertyId = "propertyId";

    public const string CinCode = "cinCode";

    /// <summary>The ISTAT code (6 digits) of the comune; written with the region that follows it.</summary>
    public const string ComuneIstatCode = "comuneIstatCode";

    public const string RegionCode = "regionCode";

    public const string AlloggiatiRoomNumbers = "alloggiatiRoomNumbers";

    public const string LockCode = "lockCode";
}

public static class BulkImportRowCodes
{
    public const string Ok = "ok";

    /// <summary>No property of the caller has that id or CIN (also another org's, one outside the reach, a row with no key).</summary>
    public const string PropertyNotFound = "property_not_found";

    public const string PropertyAmbiguous = "property_ambiguous";

    /// <summary>The comune is not an active comune of the official ISTAT list the property wizard uses (also a code that is not 6 digits).</summary>
    public const string ComuneUnknown = "comune_unknown";

    public const string RegionMismatch = "region_mismatch";

    public const string RoomNumbersInvalid = "room_numbers_invalid";

    public const string LockCodeInvalid = "lock_code_invalid";

    public const string DuplicateRow = "duplicate_row";
}

public static class BulkImportCodes
{
    public const string FileMissing = "import_file_missing";

    public const string FileTypeUnsupported = "import_file_type_unsupported";

    public const string FileTooLarge = "import_file_too_large";

    public const string TooManyRows = "import_too_many_rows";

    public const string FileUnreadable = "import_file_unreadable";

    public const string FileEmpty = "import_file_empty";

    public const string ColumnsUnrecognized = "import_columns_unrecognized";
}

public static class BulkImportLimits
{
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public const int MaxRows = 1_000;

    public const long MaxUnzippedBytes = 20L * 1024 * 1024;

    public const int MaxZipEntries = 200;

    public const int MaxColumns = 100;

    public const int MaxCellLength = 1_000;

    public const int LockCodeMinLength = 3;

    public const int LockCodeMaxLength = 20;

    public const int RoomNumberMaxLength = 20;
}

public sealed record BulkImportRowError(string Column, string Code);

/// <summary>
/// One row of the file. The row is <c>ok</c> or has the code of its first error as <see cref="Status"/> and all of them in <see cref="Errors"/>.
/// No field holds the lock code.
/// </summary>
/// <param name="RowNumber">The row as the person sees it in the sheet: the header is row 1, the first row of data row 2.</param>
/// <param name="PropertyRef">What the row names the property by (the id as written, or the CIN normalized); <c>null</c> when it names none.</param>
/// <param name="PropertyId">The property, when the row found one of the caller's.</param>
/// <param name="Status">One of <see cref="BulkImportRowCodes"/>.</param>
/// <param name="Errors">Every error of the row, by column.</param>
/// <param name="LockCodeProvided">True when the row has a lock code. The code itself is never answered.</param>
/// <param name="Applied">
/// True when the row was valid and changed something on the property (never in a dry run). A valid row that holds what the property has already,
/// or that has only its key, writes nothing and is not applied.
/// </param>
public sealed record BulkImportRow(
    int RowNumber,
    string? PropertyRef,
    Guid? PropertyId,
    string Status,
    IReadOnlyList<BulkImportRowError> Errors,
    bool LockCodeProvided,
    bool Applied);

public sealed record BulkImportSummary(int Rows, int Valid, int Invalid, int Applied);

public sealed record BulkImportResult(
    bool DryRun,
    BulkImportSummary Summary,
    IReadOnlyList<string> UnknownColumns,
    IReadOnlyList<BulkImportRow> Rows);
