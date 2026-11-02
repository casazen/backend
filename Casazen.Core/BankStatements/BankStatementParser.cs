using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Casazen.Core.Exceptions;

namespace Casazen.Core.BankStatements;

/// <summary>
/// Reads a bank statement of any of the four formats the Italian banks export: CSV, Excel (<c>.xlsx</c>), ISO 20022 CAMT.053 and SWIFT
/// MT940. The format is decided on the content (the name of the file is not even looked at); the file is never written anywhere, and what comes
/// out are the credits only. Pure: no database, no clock, no log.
/// </summary>
public static class BankStatementParser
{
    private const int BinarySniffBytes = 4096;

    public static ParsedBankStatement Parse(byte[] content, BankColumnMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            throw new DomainRuleException(BankStatementErrorCodes.Empty, "BankStatementEmpty");
        if (content.Length > BankStatementLimits.MaxFileBytes)
            throw new DomainPayloadTooLargeException(BankStatementErrorCodes.TooLarge, "BankStatementTooLarge", BankStatementLimits.MaxFileBytes / (1024 * 1024));

        try
        {
            var (format, text) = Detect(content);
            return format switch
            {
                BankStatementFormat.Xlsx => TabularStatementReader.Read(format, XlsxTableReader.Read(content), mapping),
                BankStatementFormat.Camt053 => Camt053StatementReader.Read(content),
                BankStatementFormat.Mt940 => Mt940StatementReader.Read(text!),
                _ => TabularStatementReader.Read(format, CsvTableReader.Read(text!), mapping),
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or FormatException or OverflowException
            or NotSupportedException or RegexMatchTimeoutException or ArgumentException or InvalidOperationException)
        {
            throw new DomainRuleException(BankStatementErrorCodes.Unreadable, "BankStatementUnreadable");
        }
    }

    /// <summary>The format of the bytes, and their text for the text formats; 415 for a PDF, a legacy .xls, an image or any other file.</summary>
    private static (BankStatementFormat Format, string? Text) Detect(byte[] content)
    {
        if (StartsWith(content, "%PDF"))
            throw Unsupported();

        // A legacy Excel file (.xls) is an OLE2 container: it is not read, the landlord saves it as .xlsx or CSV.
        if (content.Length >= 8 && content[0] == 0xD0 && content[1] == 0xCF && content[2] == 0x11 && content[3] == 0xE0)
            throw Unsupported();

        if (content.Length >= 4 && content[0] == (byte)'P' && content[1] == (byte)'K' && content[2] == 3 && content[3] == 4)
        {
            if (XlsxTableReader.LooksLikeWorkbook(content))
                return (BankStatementFormat.Xlsx, null);
            throw Unsupported();
        }

        if (IsBinary(content))
            throw Unsupported();

        var text = StatementText.Decode(content);
        var head = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith('<'))
        {
            if (head.Contains("BkToCstmrStmt", StringComparison.Ordinal) || head.Contains("camt.053", StringComparison.Ordinal))
                return (BankStatementFormat.Camt053, text);
            throw Unsupported();
        }

        return Mt940StatementReader.LooksLikeMt940(text) ? (BankStatementFormat.Mt940, text) : (BankStatementFormat.Csv, text);
    }

    private static bool StartsWith(byte[] content, string prefix)
    {
        if (content.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (content[i] != prefix[i])
                return false;
        }

        return true;
    }

    /// <summary>Control characters in the first bytes (a text never has them, but for the tab and the line breaks); a text with a UTF-16 mark is text.</summary>
    private static bool IsBinary(byte[] content)
    {
        if (content.Length >= 2 && ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF)))
            return false;

        var controls = 0;
        foreach (var value in content.AsSpan(0, Math.Min(content.Length, BinarySniffBytes)))
        {
            if (value == 0)
                return true;
            if (value < 0x20 && value is not (9 or 10 or 12 or 13))
                controls++;
        }

        return controls > 2;
    }

    private static DomainUnsupportedMediaTypeException Unsupported() =>
        new(BankStatementErrorCodes.FormatUnsupported, "BankStatementFormatUnsupported");
}

/// <summary>
/// The identity of a movement without keeping the movement: a SHA-256 of what the bank wrote (dates, amount, reason, payer, bank
/// reference), the org it belongs to and how many identical lines came before it in the file. The same movement in two exports that overlap
/// has the same fingerprint, so importing it twice does nothing; two identical payments the same day are told apart by their order. The org
/// is part of the hash, so the same line in two orgs never shares a value.
/// </summary>
public static class BankMovementFingerprint
{
    public static IReadOnlyList<string> ForAll(Guid orgId, IReadOnlyList<BankMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var fingerprints = new string[movements.Count];
        for (var i = 0; i < movements.Count; i++)
        {
            var key = Canonical(orgId, movements[i]);
            seen.TryGetValue(key, out var before);
            seen[key] = before + 1;
            fingerprints[i] = Hash($"{key}|n{before}");
        }

        return fingerprints;
    }

    internal static string Canonical(Guid orgId, BankMovement movement) =>
        string.Join(
            '|',
            "cz-bank-movement-v1",
            orgId.ToString("N"),
            movement.BookingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            movement.ValueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            movement.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            movement.Currency,
            BankValues.NormalizeText(movement.Description),
            BankValues.NormalizeText(movement.CounterpartyName),
            movement.CounterpartyIban ?? string.Empty,
            movement.BankReference?.Trim() ?? string.Empty);

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// What is kept of who paid, so that the next statement recognizes the same payer: a hash of the IBAN and a hash of the name, never the
/// IBAN or the name. An IBAN and a name are easy to guess, so a plain hash of either would give it away to whoever reads the table: these are
/// HMAC-SHA256 with a key that is not in the database (<see cref="DeriveKey"/>) and the org in the message. Without a key nothing is kept
/// (null) and the payer is simply not recognized. They are stored only next to the lease the payer paid and cleared when the tenant is erased.
/// </summary>
public static class BankPayerHash
{
    private const string KeyPurpose = "cz-bank-payer-hash-key-v1";

    /// <summary>The key of these hashes, made from the secret the server keeps for its signed links; null when there is no secret.</summary>
    public static byte[]? DeriveKey(string? secret) =>
        string.IsNullOrWhiteSpace(secret)
            ? null
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(KeyPurpose));

    public static string? Iban(Guid orgId, string? iban, byte[]? key) =>
        key is null || string.IsNullOrWhiteSpace(iban) ? null : Keyed(key, $"cz-bank-payer-iban-v2|{orgId:N}|{iban.Trim().ToUpperInvariant()}");

    /// <summary>The same hash whatever the order of the words and the accents: «Verdi Giulia» and «GIULIA VERDI» are one payer.</summary>
    public static string? Name(Guid orgId, string? name, byte[]? key)
    {
        var words = BankValues.NormalizeText(name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 2)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var joined = string.Join(' ', words);
        return key is null || joined.Length < 4 ? null : Keyed(key, $"cz-bank-payer-name-v2|{orgId:N}|{joined}");
    }

    private static string Keyed(byte[] key, string value) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
}
