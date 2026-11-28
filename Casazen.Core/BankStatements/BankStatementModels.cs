namespace Casazen.Core.BankStatements;

public enum BankStatementFormat
{
    Csv = 0,

    Xlsx = 1,

    Camt053 = 2,

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
    public string ShortDescription(int max = 200) => BankValues.Clip(Description, max);
}

public sealed record ParsedBankStatement(BankStatementFormat Format, IReadOnlyList<BankMovement> Credits, int MovementCount);

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

public static class BankStatementLimits
{
    public const int MaxFileBytes = 8 * 1024 * 1024;

    public const int MaxCredits = 5_000;

    public const int MaxRows = 50_000;

    public const int MaxColumns = 100;

    public const int MaxTextLength = 1_000;

    public const long MaxUnzippedPartBytes = 48L * 1024 * 1024;

    public const int MaxSelections = 500;
}

public static class BankStatementErrorCodes
{
    public const string FileMissing = "bank_statement_file_missing";

    public const string TooLarge = "bank_statement_too_large";

    public const string FormatUnsupported = "bank_statement_format_unsupported";

    public const string Unreadable = "bank_statement_unreadable";

    public const string Empty = "bank_statement_empty";

    public const string TooManyRows = "bank_statement_too_many_rows";

    public const string ColumnsUnrecognized = "bank_statement_columns_unrecognized";

    public const string MappingInvalid = "bank_statement_mapping_invalid";

    public const string SelectionsInvalid = "bank_import_selections_invalid";

    public const string MovementAlreadyImported = "bank_movement_already_imported";

    public const string MovementNotFound = "bank_movement_not_found";

    public const string AmountMismatch = "bank_movement_amount_mismatch";
}
