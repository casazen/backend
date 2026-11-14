using System.Globalization;
using System.Numerics;
using System.Text;
using Casazen.Core.BankStatements;
using Casazen.Core.Documents;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// The Swiss QR-bill of an owner invoice (E14/F01/US01/T02, NF-113): the text of the Swiss QR Code and what its payment part prints, as the Swiss Implementation
/// Guidelines for the QR-bill of SIX prescribe (version <see cref="GuidelinesVersion"/>, <see cref="GuidelinesUrl"/>; the runbook <c>owner-accounting.md</c>
/// records them).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The payload</b> is the header <c>SPC</c> / <c>0200</c> / <c>1</c>, the account, the creditor, seven empty lines of the ultimate creditor (reserved),
/// the amount and the currency, the debtor (seven empty lines when it is not known), the reference type and the reference, the unstructured message and the
/// trailer <c>EPD</c>, one per line (LF), in that order. Addresses are structured only (type <c>S</c>).</item>
/// <item><b>The reference.</b> A QR-IBAN is paid with a QR reference (<c>QRR</c>, 27 digits, the last a recursive modulo-10 check digit); an IBAN with a creditor
/// reference (<c>SCOR</c>, ISO 11649: <c>RF</c>, two check digits, up to 21 letters or digits) or none (<c>NON</c>).</item>
/// <item><b>Characters.</b> Only the Latin set of the guidelines (Basic Latin, Latin-1 Supplement, Latin Extended-A, Ș ș Ț ț and €): any other character becomes a
/// dot, a line break a space, and every field is cut to its length.</item>
/// <item><b>Never a broken bill.</b> <see cref="Problem"/> names what makes the data not a QR-bill (an account that is not of CH or LI, a reference of the wrong type
/// or check digit, a currency other than CHF and EUR, an amount out of range, an address without name, postal code, town or country): the caller then prints
/// no payment part.</item>
/// </list>
/// </remarks>
public static class SwissQrBill
{
    /// <summary>The version of the Swiss Implementation Guidelines for the QR-bill the payload follows.</summary>
    public const string GuidelinesVersion = "2.3";

    /// <summary>Where SIX publishes the guidelines.</summary>
    public const string GuidelinesUrl = "https://www.six-group.com/en/products-services/banking-services/payment-standardization/standards/qr-bill.html";

    public const string QrType = "SPC";
    public const string Version = "0200";
    public const string CodingType = "1";
    public const string Trailer = "EPD";

    /// <summary>The reference types: QR reference, creditor reference (ISO 11649), none.</summary>
    public const string ReferenceQrr = "QRR";
    public const string ReferenceScor = "SCOR";
    public const string ReferenceNone = "NON";

    /// <summary>The only address type the payload carries: structured.</summary>
    public const string StructuredAddress = "S";

    public const decimal MinAmount = 0.01m;
    public const decimal MaxAmount = 999_999_999.99m;

    /// <summary>The longest payload (the QR code of the guidelines holds at most 997 characters).</summary>
    public const int MaxPayloadLength = 997;

    public const int NameMaxLength = 70;
    public const int StreetMaxLength = 70;
    public const int BuildingNumberMaxLength = 16;
    public const int PostalCodeMaxLength = 16;
    public const int TownMaxLength = 35;
    public const int MessageMaxLength = 140;

    /// <summary>The longest body of a creditor reference (ISO 11649: 25 characters with <c>RF</c> and the two check digits).</summary>
    public const int ScorBodyMaxLength = 21;

    /// <summary>The table of the recursive modulo-10 check digit of a QR reference.</summary>
    private static readonly int[] QrrTable = [0, 9, 4, 6, 8, 2, 7, 1, 3, 5];

    /// <summary>The currencies a QR-bill is paid in.</summary>
    public static IReadOnlyList<string> Currencies { get; } = ["CHF", "EUR"];

    /// <summary>
    /// The account a QR-bill of the issuer is paid on: its QR-IBAN when it has a valid one, else its IBAN when it is of Switzerland or Liechtenstein (a QR-bill
    /// takes no other); <c>null</c> without such an account.
    /// </summary>
    public static string? AccountOf(string? qrIban, string? iban)
    {
        if (BankValues.NormalizeIban(qrIban) is { } qr && IsSwissAccount(qr) && IssuerBankAccounts.IsQrIban(qr))
            return qr;

        return BankValues.NormalizeIban(iban) is { } plain && IsSwissAccount(plain) && !IssuerBankAccounts.IsQrIban(plain) ? plain : null;
    }

    /// <summary>True for an IBAN of Switzerland or Liechtenstein (21 characters).</summary>
    public static bool IsSwissAccount(string normalizedIban) =>
        normalizedIban.Length == 21 && (normalizedIban.StartsWith("CH", StringComparison.Ordinal) || normalizedIban.StartsWith("LI", StringComparison.Ordinal));

    /// <summary>
    /// What makes <paramref name="data"/> not a QR-bill, as a short code (<c>account</c>, <c>reference</c>, <c>currency</c>, <c>amount</c>, <c>creditor</c>,
    /// <c>debtor</c>), or <c>null</c> when it is one.
    /// </summary>
    public static string? Problem(SwissQrBillData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (BankValues.NormalizeIban(data.Account) is not { } account || !string.Equals(account, data.Account, StringComparison.Ordinal) || !IsSwissAccount(account))
            return "account";

        var qrAccount = IssuerBankAccounts.IsQrIban(account);
        var referenceOk = data.ReferenceType switch
        {
            ReferenceQrr => qrAccount && IsValidQrr(data.Reference),
            ReferenceScor => !qrAccount && IsValidScor(data.Reference),
            ReferenceNone => !qrAccount && string.IsNullOrEmpty(data.Reference),
            _ => false,
        };
        if (!referenceOk)
            return "reference";
        if (!Currencies.Contains(data.Currency, StringComparer.Ordinal))
            return "currency";
        if (data.Amount is { } amount && (amount < MinAmount || amount > MaxAmount || decimal.Round(amount, 2) != amount))
            return "amount";
        if (!IsComplete(data.Creditor))
            return "creditor";
        if (data.Debtor is { } debtor && !IsComplete(debtor))
            return "debtor";
        return null;
    }

    /// <summary>The payload of the Swiss QR Code (see the remarks). <see cref="ArgumentException"/> when <see cref="Problem"/> names a problem.</summary>
    public static string Build(SwissQrBillData data)
    {
        if (Problem(data) is { } problem)
            throw new ArgumentException($"The data are not a QR-bill: {problem}.", nameof(data));

        var lines = new List<string> { QrType, Version, CodingType, data.Account };
        AddAddress(lines, data.Creditor);
        lines.AddRange(Enumerable.Repeat(string.Empty, 7));
        lines.Add(data.Amount is { } amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty);
        lines.Add(data.Currency);
        if (data.Debtor is { } debtor)
            AddAddress(lines, debtor);
        else
            lines.AddRange(Enumerable.Repeat(string.Empty, 7));
        lines.Add(data.ReferenceType);
        lines.Add(data.Reference ?? string.Empty);
        lines.Add(Clean(data.Message, MessageMaxLength));
        lines.Add(Trailer);

        var payload = string.Join('\n', lines);
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentException("The payload of the QR-bill is longer than the guidelines allow.", nameof(data));
        return payload;
    }

    /// <summary>
    /// What the payment part of <paramref name="data"/> prints, with the payload of its QR code and the labels of the language of the document; <c>null</c> when
    /// the data are not a QR-bill (<see cref="Problem"/>).
    /// </summary>
    public static PdfSwissQrBill? Part(SwissQrBillData data, PdfSwissQrBillLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (Problem(data) is not null)
            return null;

        return new PdfSwissQrBill(
            Build(data),
            FormatIban(data.Account),
            AddressLines(data.Creditor),
            data.ReferenceType == ReferenceNone ? null : FormatReference(data.ReferenceType, data.Reference!),
            string.IsNullOrWhiteSpace(data.Message) ? null : Clean(data.Message, MessageMaxLength),
            data.Debtor is { } debtor ? AddressLines(debtor) : null,
            data.Currency,
            data.Amount is { } amount ? FormatAmount(amount) : null,
            labels);
    }

    // ---------------------------------------------------------------- references

    /// <summary>The check digit of the 26 digits of a QR reference: the recursive modulo 10 of the guidelines.</summary>
    public static int QrrCheckDigit(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length != 26 || !digits.All(char.IsAsciiDigit))
            throw new ArgumentException("Twenty-six digits are expected.", nameof(digits));

        var carry = 0;
        foreach (var digit in digits)
            carry = QrrTable[(carry + (digit - '0')) % 10];
        return (10 - carry) % 10;
    }

    /// <summary>The QR reference of 26 digits: the digits and their check digit.</summary>
    public static string QrrReference(string digits) => digits + QrrCheckDigit(digits).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The QR reference of a document: 26 digits taken from its id (the same for every reading of the document, a different one for each document), and the
    /// check digit.
    /// </summary>
    public static string QrrReferenceOf(Guid documentId)
    {
        var number = new BigInteger(documentId.ToByteArray(), isUnsigned: true) % BigInteger.Pow(10, 26);
        return QrrReference(number.ToString("D26", CultureInfo.InvariantCulture));
    }

    /// <summary>True for a QR reference: 27 digits, the last its check digit.</summary>
    public static bool IsValidQrr(string? reference) =>
        reference is { Length: 27 } && reference.All(char.IsAsciiDigit) && QrrCheckDigit(reference[..26]) == reference[26] - '0';

    /// <summary>
    /// The creditor reference (ISO 11649) of <paramref name="body"/>: <c>RF</c>, the two check digits and the body (its letters and digits only, upper case, at most
    /// 21). <see cref="ArgumentException"/> for a body without a letter or a digit.
    /// </summary>
    public static string ScorReference(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var clean = new string(body.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (clean.Length == 0)
            throw new ArgumentException("A creditor reference needs a letter or a digit.", nameof(body));
        if (clean.Length > ScorBodyMaxLength)
            clean = clean[^ScorBodyMaxLength..];

        var check = 98 - Mod97(clean + "RF00");
        return "RF" + check.ToString("00", CultureInfo.InvariantCulture) + clean;
    }

    /// <summary>True for a creditor reference: <c>RF</c>, two check digits and 1 to 21 letters or digits, with the right check digits.</summary>
    public static bool IsValidScor(string? reference) =>
        reference is { Length: >= 5 and <= ScorBodyMaxLength + 4 }
        && reference.StartsWith("RF", StringComparison.Ordinal)
        && char.IsAsciiDigit(reference[2]) && char.IsAsciiDigit(reference[3])
        && reference[4..].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c))
        && Mod97(reference[4..] + reference[..4]) == 1;

    // ---------------------------------------------------------------- what the payment part prints

    /// <summary>An IBAN in groups of four (<c>CH44 3199 9123 0008 8901 2</c>).</summary>
    public static string FormatIban(string iban) => Groups(iban, 4, 0);

    /// <summary>A QR reference in groups of five from the right (<c>21 00000 00003 13947 14300 09017</c>); a creditor reference in groups of four.</summary>
    public static string FormatReference(string type, string reference) =>
        type == ReferenceQrr ? Groups(reference, 5, reference.Length % 5) : Groups(reference, 4, 0);

    /// <summary>An amount with two decimals and a space between the thousands (<c>1 949.75</c>).</summary>
    public static string FormatAmount(decimal amount)
    {
        var numbers = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        numbers.NumberGroupSeparator = " ";
        numbers.NumberDecimalSeparator = ".";
        return amount.ToString("N2", numbers);
    }

    /// <summary>
    /// <paramref name="text"/> as a field of the payload: trimmed, a line break a space, a character outside the Latin set of the guidelines a dot, at most
    /// <paramref name="max"/> characters; empty for none.
    /// </summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
            sb.Append(c is '\r' or '\n' or '\t' ? ' ' : IsAllowed(c) ? c : '.');
        var clean = sb.ToString().Trim();
        return clean.Length <= max ? clean : clean[..max].TrimEnd();
    }

    /// <summary>The characters of the guidelines: Basic Latin, Latin-1 Supplement, Latin Extended-A, Ș ș Ț ț and the euro sign.</summary>
    private static bool IsAllowed(char c) =>
        c is (>= (char)0x20 and <= (char)0x7E) or (>= (char)0xA0 and <= (char)0x17F) or (>= (char)0x218 and <= (char)0x21B) or (char)0x20AC;

    private static bool IsComplete(SwissQrBillAddress address) =>
        Clean(address.Name, NameMaxLength).Length > 0
        && Clean(address.PostalCode, PostalCodeMaxLength).Length > 0
        && Clean(address.Town, TownMaxLength).Length > 0
        && address.Country is { Length: 2 } country && country.All(char.IsAsciiLetter);

    private static void AddAddress(List<string> lines, SwissQrBillAddress address)
    {
        lines.Add(StructuredAddress);
        lines.Add(Clean(address.Name, NameMaxLength));
        lines.Add(Clean(address.Street, StreetMaxLength));
        lines.Add(Clean(address.BuildingNumber, BuildingNumberMaxLength));
        lines.Add(Clean(address.PostalCode, PostalCodeMaxLength));
        lines.Add(Clean(address.Town, TownMaxLength));
        lines.Add(address.Country.ToUpperInvariant());
    }

    /// <summary>
    /// The lines of an address on the payment part: the name, the street and number, the postal code and the town (with the country code before the postal code
    /// for an address outside Switzerland and Liechtenstein).
    /// </summary>
    private static IReadOnlyList<string> AddressLines(SwissQrBillAddress address)
    {
        var lines = new List<string> { Clean(address.Name, NameMaxLength) };
        var street = string.Join(' ', new[] { Clean(address.Street, StreetMaxLength), Clean(address.BuildingNumber, BuildingNumberMaxLength) }.Where(s => s.Length > 0));
        if (street.Length > 0)
            lines.Add(street);
        var country = address.Country.ToUpperInvariant();
        var prefix = country is "CH" or "LI" ? string.Empty : country + "-";
        lines.Add($"{prefix}{Clean(address.PostalCode, PostalCodeMaxLength)} {Clean(address.Town, TownMaxLength)}");
        return lines;
    }

    /// <summary>The text in groups of <paramref name="size"/>, the first group of <paramref name="first"/> characters when it is not 0.</summary>
    private static string Groups(string text, int size, int first)
    {
        var groups = new List<string>();
        var start = 0;
        if (first > 0)
        {
            groups.Add(text[..first]);
            start = first;
        }

        for (var i = start; i < text.Length; i += size)
            groups.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        return string.Join(' ', groups);
    }

    /// <summary>The remainder of the division by 97 of the number made of <paramref name="text"/>, its letters written as 10 to 35 (ISO 7064).</summary>
    private static int Mod97(string text)
    {
        var remainder = 0;
        foreach (var c in text)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;
            remainder = value >= 10 ? ((remainder * 100) + value) % 97 : ((remainder * 10) + value) % 97;
        }

        return remainder;
    }
}

/// <summary>A structured address of the QR-bill (type <c>S</c>): the name, the street and building number (optional), the postal code, the town and the country.</summary>
public sealed record SwissQrBillAddress(string Name, string? Street, string? BuildingNumber, string PostalCode, string Town, string Country);

/// <summary>The data of a QR-bill.</summary>
/// <param name="Account">The IBAN or QR-IBAN of the creditor, upper case without spaces.</param>
/// <param name="Creditor">Who is paid: the agency (the snapshot of the issuer).</param>
/// <param name="Amount">The amount to pay; <c>null</c> leaves it to the payer.</param>
/// <param name="Currency"><c>CHF</c> or <c>EUR</c>.</param>
/// <param name="Debtor">Who pays: the owner; <c>null</c> when its structured address is not known (the payment part leaves a box).</param>
/// <param name="ReferenceType"><c>QRR</c>, <c>SCOR</c> or <c>NON</c>.</param>
/// <param name="Reference">The reference, without spaces; <c>null</c> for <c>NON</c>.</param>
/// <param name="Message">The unstructured message (<c>Fattura PM2026/7</c>).</param>
public sealed record SwissQrBillData(
    string Account,
    SwissQrBillAddress Creditor,
    decimal? Amount,
    string Currency,
    SwissQrBillAddress? Debtor,
    string ReferenceType,
    string? Reference,
    string? Message);
