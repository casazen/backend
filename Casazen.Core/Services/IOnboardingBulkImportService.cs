using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The import of the data that changes from one property to the next (comune and region, the Alloggiati Web room numbers, the lock code) from a CSV
/// or an Excel file, with the check of every row, so an agency fills 250 properties without 250 wizards (NF-105, ONB-02, E10/F02/US01/T02).
/// </summary>
/// <remarks>
/// <para><b>Stateless, in two calls.</b> The same call validates and answers each row; with <c>dryRun</c> it stops there and writes nothing, without it
/// it validates again and writes ONLY the valid rows, each in its own unit of work: an invalid row never blocks a valid one. The file is read from
/// memory and never stored.</para>
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

/// <summary>The names of the columns of the file in the answer (cross-repo contract): never rename one.</summary>
public static class BulkImportColumns
{
    /// <summary>The id of the property (one of the two keys of a row).</summary>
    public const string PropertyId = "propertyId";

    /// <summary>The CIN of the property, normalized (the other key of a row).</summary>
    public const string CinCode = "cinCode";

    /// <summary>The ISTAT code (6 digits) of the comune; written with the region that follows it.</summary>
    public const string ComuneIstatCode = "comuneIstatCode";

    /// <summary>The CasaZen code of the region (<c>LOM</c>); it has to be the comune's.</summary>
    public const string RegionCode = "regionCode";

    /// <summary>The Alloggiati Web room numbers, separated by <c>;</c>.</summary>
    public const string AlloggiatiRoomNumbers = "alloggiatiRoomNumbers";

    /// <summary>The lock code of the property.</summary>
    public const string LockCode = "lockCode";
}

/// <summary>The status of a row (cross-repo contract): <c>ok</c>, or the first of its errors. Never rename one.</summary>
public static class BulkImportRowCodes
{
    /// <summary>The row is valid.</summary>
    public const string Ok = "ok";

    /// <summary>No property of the caller has that id or CIN (also another org's, one outside the reach, a row with no key).</summary>
    public const string PropertyNotFound = "property_not_found";

    /// <summary>More than one property of the caller has that CIN.</summary>
    public const string PropertyAmbiguous = "property_ambiguous";

    /// <summary>The comune is not an active comune of the official ISTAT list the property wizard uses (also a code that is not 6 digits).</summary>
    public const string ComuneUnknown = "comune_unknown";

    /// <summary>The region is not a region code, or is not the one of the comune (the comune of the row, or the one the property already has).</summary>
    public const string RegionMismatch = "region_mismatch";

    /// <summary>The room numbers are more than 20, empty, repeated or not 1 to 20 letters, digits or hyphens each.</summary>
    public const string RoomNumbersInvalid = "room_numbers_invalid";

    /// <summary>The lock code is not 3 to 20 characters.</summary>
    public const string LockCodeInvalid = "lock_code_invalid";

    /// <summary>The same property was already on an earlier row of the file: this row is not applied.</summary>
    public const string DuplicateRow = "duplicate_row";
}

/// <summary>The stable codes of the errors of the import (cross-repo contract): never rename one.</summary>
public static class BulkImportCodes
{
    /// <summary>400: no file in the field <c>file</c>.</summary>
    public const string FileMissing = "import_file_missing";

    /// <summary>415: not a <c>.csv</c> or an <c>.xlsx</c> (a <c>.xls</c>, an <c>.xlsm</c>, another type, or content that is not the type its name says).</summary>
    public const string FileTypeUnsupported = "import_file_type_unsupported";

    /// <summary>413: more than 2 MB, or an Excel file that unzips to more than 20 MB.</summary>
    public const string FileTooLarge = "import_file_too_large";

    /// <summary>422: more than 1,000 rows.</summary>
    public const string TooManyRows = "import_too_many_rows";

    /// <summary>422: the file cannot be read (a damaged archive, more than 200 entries, no sheet, malformed XML, a quote never closed).</summary>
    public const string FileUnreadable = "import_file_unreadable";

    /// <summary>422: the file has no row under the header.</summary>
    public const string FileEmpty = "import_file_empty";

    /// <summary>422: the header has no column that names the property (<c>propertyId</c> or <c>cinCode</c>) or none that holds data to import.</summary>
    public const string ColumnsUnrecognized = "import_columns_unrecognized";
}

/// <summary>The limits of the import.</summary>
public static class BulkImportLimits
{
    /// <summary>The largest file.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>The most rows under the header.</summary>
    public const int MaxRows = 1_000;

    /// <summary>The most an Excel file may unzip to (the sum of its parts, checked on the header and on the bytes actually read).</summary>
    public const long MaxUnzippedBytes = 20L * 1024 * 1024;

    /// <summary>The most entries an Excel archive may hold.</summary>
    public const int MaxZipEntries = 200;

    /// <summary>The most columns read from a row; the rest are not read.</summary>
    public const int MaxColumns = 100;

    /// <summary>The longest text kept of a cell; a longer one is cut (and is then not valid for any column).</summary>
    public const int MaxCellLength = 1_000;

    /// <summary>The fewest characters of a lock code.</summary>
    public const int LockCodeMinLength = 3;

    /// <summary>The most characters of a lock code.</summary>
    public const int LockCodeMaxLength = 20;

    /// <summary>The most characters of one room number.</summary>
    public const int RoomNumberMaxLength = 20;
}

/// <summary>One error of a row: the column it is about and the code.</summary>
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
/// <param name="Applied">True when the row was valid and was written (never in a dry run).</param>
public sealed record BulkImportRow(
    int RowNumber,
    string? PropertyRef,
    Guid? PropertyId,
    string Status,
    IReadOnlyList<BulkImportRowError> Errors,
    bool LockCodeProvided,
    bool Applied);

/// <summary>How many rows the file has, how many are valid, how many are not and how many were written.</summary>
public sealed record BulkImportSummary(int Rows, int Valid, int Invalid, int Applied);

/// <summary>The answer of an import.</summary>
/// <param name="DryRun">True when nothing was written.</param>
/// <param name="Summary">The counts.</param>
/// <param name="UnknownColumns">The header cells that name no column of the import (or repeat one), once each.</param>
/// <param name="Rows">One entry for each row of data.</param>
public sealed record BulkImportResult(
    bool DryRun,
    BulkImportSummary Summary,
    IReadOnlyList<string> UnknownColumns,
    IReadOnlyList<BulkImportRow> Rows);
