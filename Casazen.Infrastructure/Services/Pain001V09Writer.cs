using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A party of a wire file (the debtor, a creditor): a name and a <b>structured</b> postal address, never an address in free lines (E16/F01/US01/T01, NF-113).
/// The texts are what the owner or the issuer wrote: <see cref="Pain001Text"/> brings them to the characters and the lengths of the message when the file is
/// written, so the check that a party is complete (<see cref="Pain001V09Writer.IsComplete"/>) is made on what the file will say.
/// </summary>
internal sealed record Pain001Party(string? Name, string? Street, string? BuildingNumber, string? PostalCode, string? Town, string? Country);

/// <summary>One transfer of a wire file: its end-to-end id, the amount, who is paid and on which IBAN, the remittance text and the date the bank is asked to execute it.</summary>
internal sealed record Pain001Transfer(
    string EndToEndId,
    decimal Amount,
    Pain001Party Creditor,
    string CreditorIban,
    string Remittance,
    DateOnly ExecutionDate);

/// <summary>
/// What a wire file says, before it is written in a message: the message id, when it is made (the clock of Europe/Rome, without an offset), the currency, the
/// debtor and the account that is debited, and the transfers.
/// </summary>
internal sealed record Pain001Message(
    string MessageId,
    DateTime CreatedAt,
    string Currency,
    Pain001Party Debtor,
    string DebtorIban,
    IReadOnlyList<Pain001Transfer> Transfers);

/// <summary>
/// Writes the CHF wire file of the agency to its owners, the Swiss credit transfer <c>pain.001.001.09.ch.03</c> (E16/F01/US01/T01, NF-113, ADR-017): ISO 20022
/// <c>pain.001.001.09</c> restricted by the Swiss Payment Standards, with <b>structured addresses only</b> (<c>StrtNm</c>, <c>BldgNb</c>, <c>PstCd</c>,
/// <c>TwnNm</c>, <c>Ctry</c>). The file is validated against the official schema by <see cref="WireTransferService"/> before anything is saved: this class
/// only writes.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Group header.</b> The unique message id, the creation time, the number of transactions and the control sum of the whole file, the initiating party
/// (the agency).</item>
/// <item><b>One payment per requested execution date.</b> Each with its unique id, the transfer method <c>TRF</c>, its own count and control sum, the date
/// (<c>ReqdExctnDt/Dt</c>), the debtor (the agency: name and structured address), its IBAN and the debtor agent (<c>NOTPROVIDED</c>: the bank reads it from the
/// IBAN).</item>
/// <item><b>One transfer per payout.</b> The unique end-to-end id, the amount in the currency, the creditor (the owner: name and structured address), the
/// creditor IBAN and the remittance. No creditor agent: a domestic IBAN identifies the bank.</item>
/// <item><b>Texts.</b> Every text is brought to the characters the Swiss messages accept and cut to the length of its element (<see cref="Pain001Text"/>); the
/// XML escaping is the writer's. Amounts are exact decimals in invariant culture with two decimals.</item>
/// </list>
/// EUR has its own message until its own change; this class is the Swiss flavor of <c>pain.001.001.09</c> and is where that change starts.
/// </remarks>
internal static class Pain001V09Writer
{
    /// <summary>The target namespace of the message (<c>pain.001.001.09</c>).</summary>
    public const string Namespace = Pain001SchemaProvider.SwissNamespace;

    /// <summary>The name of the Swiss schema file the message is written for (the <c>xsi:schemaLocation</c> of its root).</summary>
    public const string SchemaFile = "pain.001.001.09.ch.03.xsd";

    /// <summary>The longest <c>Nm</c> and <c>StrtNm</c> (<c>Max70Text</c>).</summary>
    public const int Max70 = 70;

    /// <summary>The longest <c>BldgNb</c> and <c>PstCd</c> (<c>Max16Text</c>).</summary>
    public const int Max16 = 16;

    /// <summary>The longest <c>TwnNm</c> (<c>Max35Text</c>) and <c>MsgId</c>, <c>PmtInfId</c>, <c>EndToEndId</c>.</summary>
    public const int Max35 = 35;

    /// <summary>The longest <c>Ustrd</c> (<c>Max140Text</c>).</summary>
    public const int Max140 = 140;

    private static readonly XNamespace Xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");

    /// <summary>
    /// True when <paramref name="party"/> can be written as a creditor or a debtor of the message: a name, a street, a postal code, a town and a country of two
    /// letters, all of them left after the characters and the lengths of the message are applied. The building number is optional.
    /// </summary>
    public static bool IsComplete(Pain001Party party)
    {
        ArgumentNullException.ThrowIfNull(party);
        return Pain001Text.Clean(party.Name, Max70).Length > 0
               && Pain001Text.Clean(party.Street, Max70).Length > 0
               && Pain001Text.Clean(party.PostalCode, Max16).Length > 0
               && Pain001Text.Clean(party.Town, Max35).Length > 0
               && Pain001Text.Country(party.Country) is not null;
    }

    /// <summary>
    /// The file of <paramref name="message"/>, one payment for each execution date in date order, the transfers in the order they have in the message. Every
    /// party must be complete (<see cref="IsComplete"/>): the service refuses a batch before it gets here.
    /// </summary>
    public static string Write(Pain001Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Transfers.Count == 0)
            throw new ArgumentException("A wire file has at least one transfer.", nameof(message));
        if (!IsComplete(message.Debtor) || message.Transfers.Any(t => !IsComplete(t.Creditor)))
            throw new ArgumentException("Every party of a wire file has a name and a complete structured address.", nameof(message));

        var ns = XNamespace.Get(Namespace);
        var payments = message.Transfers.GroupBy(t => t.ExecutionDate).OrderBy(g => g.Key).ToList();
        var total = message.Transfers.Sum(t => t.Amount);

        var header = new XElement(
            ns + "GrpHdr",
            new XElement(ns + "MsgId", message.MessageId),
            new XElement(ns + "CreDtTm", message.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(ns + "NbOfTxs", message.Transfers.Count.ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "CtrlSum", Amount(total)),
            new XElement(ns + "InitgPty", new XElement(ns + "Nm", Pain001Text.Clean(message.Debtor.Name, Max70))));

        var initiation = new XElement(ns + "CstmrCdtTrfInitn", header);
        for (var i = 0; i < payments.Count; i++)
        {
            var transfers = payments[i].ToList();
            initiation.Add(Payment(ns, message, payments[i].Key, transfers, PaymentId(message.MessageId, i + 1)));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                ns + "Document",
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                new XAttribute(Xsi + "schemaLocation", Namespace + " " + SchemaFile),
                initiation));

        return Format(document);
    }

    /// <summary>An amount as the file says it: invariant culture, exactly two decimals.</summary>
    public static string Amount(decimal amount)
    {
        return amount.ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>A document as indented UTF-8 text, with no byte order mark and <c>\n</c> line ends.</summary>
    public static string Format(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
        };

        using var stream = new MemoryStream();
        using (var xmlWriter = XmlWriter.Create(stream, settings))
        {
            document.Save(xmlWriter);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The id of the <paramref name="number"/>th payment of a message: the start of the message id and the number, never longer than <see cref="Max35"/>.</summary>
    public static string PaymentId(string messageId, int number)
    {
        var start = messageId.Length > 30 ? messageId[..30] : messageId;
        return start + "-" + number.ToString("D4", CultureInfo.InvariantCulture);
    }

    private static XElement Payment(XNamespace ns, Pain001Message message, DateOnly date, List<Pain001Transfer> transfers, string paymentId)
    {
        var payment = new XElement(
            ns + "PmtInf",
            new XElement(ns + "PmtInfId", paymentId),
            new XElement(ns + "PmtMtd", "TRF"),
            new XElement(ns + "BtchBookg", "true"),
            new XElement(ns + "NbOfTxs", transfers.Count.ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "CtrlSum", Amount(transfers.Sum(t => t.Amount))),
            new XElement(ns + "ReqdExctnDt", new XElement(ns + "Dt", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            Party(ns, "Dbtr", message.Debtor),
            new XElement(ns + "DbtrAcct", new XElement(ns + "Id", new XElement(ns + "IBAN", message.DebtorIban))),
            new XElement(ns + "DbtrAgt", new XElement(ns + "FinInstnId", new XElement(ns + "Othr", new XElement(ns + "Id", "NOTPROVIDED")))));

        foreach (var transfer in transfers)
        {
            payment.Add(
                new XElement(
                    ns + "CdtTrfTxInf",
                    new XElement(ns + "PmtId", new XElement(ns + "EndToEndId", transfer.EndToEndId)),
                    new XElement(
                        ns + "Amt",
                        new XElement(ns + "InstdAmt", new XAttribute("Ccy", message.Currency), Amount(transfer.Amount))),
                    Party(ns, "Cdtr", transfer.Creditor),
                    new XElement(ns + "CdtrAcct", new XElement(ns + "Id", new XElement(ns + "IBAN", transfer.CreditorIban))),
                    new XElement(ns + "RmtInf", new XElement(ns + "Ustrd", Pain001Text.Clean(transfer.Remittance, Max140)))));
        }

        return payment;
    }

    /// <summary>A party with its structured address: <c>Nm</c>, then <c>StrtNm</c>, <c>BldgNb</c> (when there is one), <c>PstCd</c>, <c>TwnNm</c>, <c>Ctry</c>.</summary>
    private static XElement Party(XNamespace ns, string element, Pain001Party party)
    {
        var address = new XElement(ns + "PstlAdr");
        address.Add(new XElement(ns + "StrtNm", Pain001Text.Clean(party.Street, Max70)));
        var building = Pain001Text.Clean(party.BuildingNumber, Max16);
        if (building.Length > 0)
            address.Add(new XElement(ns + "BldgNb", building));

        address.Add(new XElement(ns + "PstCd", Pain001Text.Clean(party.PostalCode, Max16)));
        address.Add(new XElement(ns + "TwnNm", Pain001Text.Clean(party.Town, Max35)));
        address.Add(new XElement(ns + "Ctry", Pain001Text.Country(party.Country)));

        return new XElement(ns + element, new XElement(ns + "Nm", Pain001Text.Clean(party.Name, Max70)), address);
    }
}

/// <summary>
/// The texts of a wire file (E16/F01/US01/T01): what an owner or an agency typed, brought to the characters the Swiss payment messages accept and to the length of
/// the element it goes in. Letters, digits, the space and <c>/ - ? : ( ) . , ' + &amp;</c> are kept; the letters of Latin-1 keep their accent, other accented
/// letters lose it, the few that have no base letter are replaced by their usual transcription, and anything else becomes a point. Whatever is left is trimmed,
/// its spaces collapsed, and it is cut to the length of the element. Never a value is logged: this class has no logger.
/// </summary>
internal static class Pain001Text
{
    private const string AllowedPunctuation = "/-?:().,'+&";

    private static readonly Dictionary<int, string> Transcriptions = new()
    {
        ['Ł'] = "L",
        ['ł'] = "l",
        ['Đ'] = "D",
        ['đ'] = "d",
        ['Œ'] = "OE",
        ['œ'] = "oe",
        ['ı'] = "i",
    };

    /// <summary>
    /// <paramref name="value"/> as the file writes it: only the characters of the message, spaces collapsed, trimmed and at most <paramref name="maxLength"/>
    /// characters long; an empty text when there is nothing left.
    /// </summary>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormC).EnumerateRunes())
            text.Append(Map(rune));

        var collapsed = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength].TrimEnd();
    }

    /// <summary>The two letters of a country in upper case, or <c>null</c> when it is not two letters (a country code of ISO 3166-1 alpha-2).</summary>
    public static string? Country(string? value)
    {
        var country = value?.Trim() ?? string.Empty;
        return country.Length == 2 && country.All(char.IsAsciiLetter) ? country.ToUpperInvariant() : null;
    }

    private static string Map(Rune rune)
    {
        var value = rune.Value;
        if (value < 0x80)
        {
            var c = (char)value;
            if (char.IsAsciiLetterOrDigit(c) || AllowedPunctuation.Contains(c))
                return c.ToString();

            // A space, a tab, a line break or any other control or sign: one space (the spaces are collapsed afterwards).
            return " ";
        }

        // The letters of Latin-1 (À to ÿ, without the multiplication and the division signs) are accepted as they are.
        if (value is >= 0xC0 and <= 0xFF and not 0xD7 and not 0xF7)
            return rune.ToString();

        if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            return " ";

        if (Transcriptions.TryGetValue(value, out var transcribed))
            return transcribed;

        // The base letter of an accented one (ś is s, ż is z): its decomposition starts with it.
        var decomposed = rune.ToString().Normalize(NormalizationForm.FormD);
        return decomposed.Length > 0 && char.IsAsciiLetter(decomposed[0]) ? decomposed[0].ToString() : ".";
    }
}
