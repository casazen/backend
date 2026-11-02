namespace Casazen.Core.BankStatements;

/// <summary>The format a bank statement file was read as, decided on its content and not on its name.</summary>
public enum BankStatementFormat
{
    /// <summary>A text table with a delimiter (";", ",", tab or "|"), as the banks export the list of movements.</summary>
    Csv = 0,

    /// <summary>An Excel workbook (<c>.xlsx</c>), first sheet only.</summary>
    Xlsx = 1,

    /// <summary>ISO 20022 <c>camt.053</c> (bank to customer statement), any version.</summary>
    Camt053 = 2,

    /// <summary>SWIFT MT940.</summary>
    Mt940 = 3,
}

/// <summary>
/// One credit of a bank statement as the file says it: nothing that identifies the movement beyond what the bank wrote. Only credits are
/// kept (a rent comes in); debits are counted and dropped at the door.
/// </summary>
/// <param name="BookingDate">Day the bank booked it.</param>
/// <param name="ValueDate">Value date (the day the money is available); the booking date when the file has no value date.</param>
/// <param name="Amount">Always positive, in cents.</param>
/// <param name="Currency">ISO code in upper case, <c>EUR</c> when the file does not say.</param>
/// <param name="Description">The reason of the payment (all the descriptive columns of the file joined), as written.</param>
/// <param name="CounterpartyName">The payer, when the file has it as a field of its own.</param>
/// <param name="CounterpartyIban">The payer's IBAN, upper case without spaces, only if it is a valid IBAN.</param>
/// <param name="BankReference">A reference of the bank that tells the movement from any other (CAMT entry reference, MT940 bank reference).</param>
public sealed record BankMovement(
    DateOnly BookingDate,
    DateOnly ValueDate,
    decimal Amount,
    string Currency,
    string Description,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? BankReference)
{
    /// <summary>The description cut to <paramref name="max"/> characters: one line of it for a screen.</summary>
    public string ShortDescription(int max = 200) => BankValues.Clip(Description, max);
}

/// <summary>What was read from a bank statement: its credits in the order of the file and how many movements it had in all.</summary>
public sealed record ParsedBankStatement(BankStatementFormat Format, IReadOnlyList<BankMovement> Credits, int MovementCount);

/// <summary>
/// The columns of a table (CSV or Excel) when the landlord says which is which (the file has none of the titles the importer knows).
/// Rows and columns are counted from 0 over the rows that are not empty. <see cref="HeaderRow"/> is the row of the titles, -1 when the file has
/// none: the movements are the rows after it. An amount is either <see cref="Amount"/> (signed) or <see cref="Credit"/> (money in) with
/// <see cref="Debit"/> (money out).
/// </summary>
public sealed record BankColumnMapping(
    int HeaderRow,
    int Date,
    int Description,
    int? ValueDate = null,
    int? Amount = null,
    int? Credit = null,
    int? Debit = null,
    int? Counterparty = null,
    int? Iban = null);

/// <summary>The size of what the importer accepts: a statement is a few hundred lines, never a database.</summary>
public static class BankStatementLimits
{
    /// <summary>Largest file, in bytes (a year of a busy account as CAMT is a few MB).</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>Most credits one file may hold (a rent comes in once a month: this is far more than any landlord's account).</summary>
    public const int MaxCredits = 5_000;

    /// <summary>Most rows (or entries) read from a file, credits or not.</summary>
    public const int MaxRows = 50_000;

    /// <summary>Most columns read from a table row.</summary>
    public const int MaxColumns = 100;

    /// <summary>Most characters kept of one cell or one description.</summary>
    public const int MaxTextLength = 1_000;

    /// <summary>Most bytes read from one part of an Excel file once unzipped (a zip that declares less than it holds is cut here).</summary>
    public const long MaxUnzippedPartBytes = 48L * 1024 * 1024;

    /// <summary>Most installments the landlord confirms in one request.</summary>
    public const int MaxSelections = 500;
}

/// <summary>Stable codes of the errors of the importer (the screen translates them).</summary>
public static class BankStatementErrorCodes
{
    /// <summary>400: the request has no file, or an empty one.</summary>
    public const string FileMissing = "bank_statement_file_missing";

    /// <summary>413: the file is larger than <see cref="BankStatementLimits.MaxFileBytes"/>.</summary>
    public const string TooLarge = "bank_statement_too_large";

    /// <summary>415: not a CSV, Excel, CAMT.053 or MT940 file (a PDF, a legacy .xls, an image…).</summary>
    public const string FormatUnsupported = "bank_statement_format_unsupported";

    /// <summary>422: the file is of a supported kind but cannot be read (damaged, or not what its content says).</summary>
    public const string Unreadable = "bank_statement_unreadable";

    /// <summary>422: the file was read and holds no movement.</summary>
    public const string Empty = "bank_statement_empty";

    /// <summary>422: more than <see cref="BankStatementLimits.MaxCredits"/> credits or <see cref="BankStatementLimits.MaxRows"/> rows.</summary>
    public const string TooManyRows = "bank_statement_too_many_rows";

    /// <summary>422: the columns of the table are not the ones the importer knows; the answer carries the first rows so the landlord can say which is which.</summary>
    public const string ColumnsUnrecognized = "bank_statement_columns_unrecognized";

    /// <summary>422: the mapping the landlord sent does not fit the file.</summary>
    public const string MappingInvalid = "bank_statement_mapping_invalid";

    /// <summary>400: no installment chosen, more than <see cref="BankStatementLimits.MaxSelections"/>, or a choice that is not an id.</summary>
    public const string SelectionsInvalid = "bank_import_selections_invalid";

    /// <summary>Skipped, not an error of the request: the movement was imported before (the file was uploaded twice, or two files overlap).</summary>
    public const string MovementAlreadyImported = "bank_movement_already_imported";

    /// <summary>Skipped: the movement is not in the file (the file is not the one that was read first).</summary>
    public const string MovementNotFound = "bank_movement_not_found";

    /// <summary>Skipped: the amount of the movement is not the amount of the installment.</summary>
    public const string AmountMismatch = "bank_movement_amount_mismatch";
}
