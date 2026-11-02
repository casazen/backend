using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Exceptions;

namespace Casazen.Core.BankStatements;

/// <summary>
/// Reads an ISO 20022 <c>camt.053</c> statement (bank to customer statement), whatever its version: the elements are found by their local
/// names, one entry at a time, so a year of a busy account does not sit in memory as one tree. The file comes from outside: DTDs and external
/// entities are refused.
/// </summary>
internal static class Camt053StatementReader
{
    private const long MaxCharacters = 96L * 1024 * 1024;

    public static ParsedBankStatement Read(byte[] content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
        };

        var credits = new List<BankMovement>();
        var movements = 0;
        var entries = 0;
        using var stream = new MemoryStream(content, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Ntry")
            {
                var entry = (XElement)XNode.ReadFrom(reader);
                entries++;
                if (entries > BankStatementLimits.MaxRows)
                    throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);

                foreach (var line in ReadEntry(entry))
                {
                    movements++;
                    if (line.Credit is null)
                        continue;
                    credits.Add(line.Credit);
                    if (credits.Count > BankStatementLimits.MaxCredits)
                        throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
                }

                continue;
            }

            reader.Read();
        }

        if (movements == 0)
            throw new DomainRuleException(BankStatementErrorCodes.Empty, "BankStatementEmpty");
        return new ParsedBankStatement(BankStatementFormat.Camt053, credits, movements);
    }

    /// <summary>One movement of an entry: a credit with its data, or null for a debit (counted, not kept).</summary>
    private readonly record struct EntryLine(BankMovement? Credit);

    private static IEnumerable<EntryLine> ReadEntry(XElement entry)
    {
        // Entries that are not booked yet (pending, information only) are not money in the account.
        var status = Child(entry, "Sts")?.Value.Trim();
        if (!string.IsNullOrEmpty(status) && !string.Equals(status, "BOOK", StringComparison.OrdinalIgnoreCase))
            yield break;

        var booking = ReadDate(Child(entry, "BookgDt"));
        var value = ReadDate(Child(entry, "ValDt"));
        if (booking is null && value is null)
            yield break;
        var bookingDate = booking ?? value!.Value;
        var valueDate = value ?? bookingDate;

        // «CdtDbtInd» is the direction on the account. «RvslInd» says the entry cancels an earlier one: a payment the bank returns is a debit that
        // reverses a credit, an outgoing payment that comes back a credit that reverses a debit. Either way it is not a tenant paying.
        var indicator = Child(entry, "CdtDbtInd")?.Value.Trim();
        var reversed = string.Equals(Child(entry, "RvslInd")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var isCredit = string.Equals(indicator, "CRDT", StringComparison.OrdinalIgnoreCase) && !reversed;
        var reference = Reference(entry);

        // What the bank says about the whole entry goes with each of its lines and is read once: an entry of thousands of lines must not
        // copy it thousands of times.
        var entryInformation = Children(entry, "AddtlNtryInf").Select(e => BankValues.Clip(e.Value)).ToList();

        var details = entry.Descendants().Where(e => e.Name.LocalName == "TxDtls").ToList();
        var split = details.Count > 1 && details.All(d => SplitAmount(d) is not null);
        if (split)
        {
            foreach (var detail in details)
            {
                var (amount, currency) = SplitAmount(detail)!.Value;
                yield return Line(isCredit, bookingDate, valueDate, amount, currency, () => Description(detail, entryInformation), detail, reference);
            }

            yield break;
        }

        var amountElement = Child(entry, "Amt");
        if (amountElement is null
            || !decimal.TryParse(amountElement.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var total))
        {
            yield break;
        }

        yield return Line(
            isCredit,
            bookingDate,
            valueDate,
            decimal.Round(Math.Abs(total), 2, MidpointRounding.AwayFromZero),
            amountElement.Attribute("Ccy")?.Value ?? "EUR",
            () => Description(entry, entryInformation),
            details.FirstOrDefault(),
            reference);
    }

    /// <param name="describe">The reason of the payment, worked out only for a credit: a debit is counted and nothing else is read of it.</param>
    private static EntryLine Line(
        bool isCredit,
        DateOnly bookingDate,
        DateOnly valueDate,
        decimal amount,
        string? currency,
        Func<string> describe,
        XElement? detail,
        string? reference)
    {
        if (!isCredit || amount <= 0m)
            return new EntryLine(null);

        var description = describe();
        var name = detail is null ? null : DebtorName(detail);
        var iban = (detail is null ? null : DebtorIban(detail)) ?? BankValues.FindIban(description);
        return new EntryLine(new BankMovement(
            bookingDate,
            valueDate,
            amount,
            BankValues.NormalizeCurrency(currency),
            description,
            string.IsNullOrWhiteSpace(name) ? null : BankValues.Clip(name, 200),
            iban,
            reference));
    }

    private static (decimal Amount, string? Currency)? SplitAmount(XElement detail)
    {
        var amount = detail.Descendants().FirstOrDefault(e => e.Name.LocalName == "TxAmt")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Amt");
        if (amount is null || !decimal.TryParse(amount.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        return (decimal.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero), amount.Attribute("Ccy")?.Value);
    }

    private static string Description(XElement scope, List<string> entryInformation)
    {
        var parts = scope
            .Descendants()
            .Where(e => e.Name.LocalName is "Ustrd" or "AddtlTxInf")
            .Select(e => e.Value)
            .ToList();
        // The reference the payer wrote for the creditor (a structured remittance) and what the bank adds to the entry are part of the reason too.
        parts.AddRange(scope.Descendants().Where(e => e.Name.LocalName == "CdtrRefInf").SelectMany(e => e.Descendants()).Where(e => e.Name.LocalName == "Ref").Select(e => e.Value));
        parts.AddRange(entryInformation);
        return BankValues.JoinDistinct(parts);
    }

    private static string? DebtorName(XElement detail)
    {
        foreach (var parties in detail.Descendants().Where(e => e.Name.LocalName == "RltdPties"))
        {
            foreach (var role in new[] { "Dbtr", "UltmtDbtr" })
            {
                var name = Children(parties, role).SelectMany(p => p.Descendants()).FirstOrDefault(e => e.Name.LocalName == "Nm")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return null;
    }

    private static string? DebtorIban(XElement detail)
    {
        foreach (var parties in detail.Descendants().Where(e => e.Name.LocalName == "RltdPties"))
        {
            var account = Children(parties, "DbtrAcct").SelectMany(p => p.Descendants()).FirstOrDefault(e => e.Name.LocalName == "IBAN")?.Value;
            if (BankValues.NormalizeIban(account) is { } iban)
                return iban;
        }

        return null;
    }

    private static string? Reference(XElement entry)
    {
        // The bank's own reference tells two entries apart; an end-to-end id the payer chose ("NOTPROVIDED") does not.
        var text = Child(entry, "AcctSvcrRef")?.Value
            ?? entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "AcctSvcrRef")?.Value
            ?? Child(entry, "NtryRef")?.Value;
        var clipped = BankValues.Clip(text, 100);
        return clipped.Length == 0 ? null : clipped;
    }

    private static DateOnly? ReadDate(XElement? container)
    {
        var text = container?.Elements().FirstOrDefault(e => e.Name.LocalName is "Dt" or "DtTm")?.Value;
        return text is not null && BankValues.TryParseDate(new Cell(text, false), out var date) ? date : null;
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);
}

/// <summary>
/// Reads a SWIFT MT940 statement: the statement lines (<c>:61:</c>) with the information the bank adds to them (<c>:86:</c>), in the three
/// ways banks write it: the German-style <c>?20</c>…<c>?33</c> subfields, the <c>/NAME/…/REMI/…</c> tags, or free text.
/// </summary>
internal static partial class Mt940StatementReader
{
    private const int RegexTimeoutMs = 1000;

    /// <summary>Longest information (<c>:86:</c>) read for a line: the standard allows 390 characters, banks write a few times that.</summary>
    private const int MaxInformationLength = 4_000;

    [GeneratedRegex(@"^(?<vd>\d{6})(?<ed>\d{4})?(?<mark>RC|RD|C|D)(?<fund>[A-Z])?(?<amt>\d+,\d*)(?<type>[NFS][A-Z0-9]{3})(?<rest>.*)$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex StatementLine();

    [GeneratedRegex(@"^:(?<tag>\d{2}[A-Z]?):(?<value>.*)$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex TagLine();

    [GeneratedRegex(@"\?(?<n>\d{2})(?<v>[^?]*)", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex Subfield();

    [GeneratedRegex(@"/(?<tag>[A-Z]{3,4})/(?<v>[^/]*)", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex SlashTag();

    [GeneratedRegex(@"^:20:", RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexTimeoutMs)]
    private static partial Regex ReferenceTag();

    [GeneratedRegex(@"^:61:", RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexTimeoutMs)]
    private static partial Regex StatementTag();

    public static bool LooksLikeMt940(string text) => ReferenceTag().IsMatch(text) && StatementTag().IsMatch(text);

    public static ParsedBankStatement Read(string text)
    {
        var fields = Split(text);
        var credits = new List<BankMovement>();
        var movements = 0;
        var currency = "EUR";
        for (var index = 0; index < fields.Count; index++)
        {
            // The currency of the lines is the one of the opening balance of their statement (:60F: first, :60M: of a following page).
            if (fields[index].Tag is "60F" or "60M")
            {
                currency = ReadCurrency(fields[index].Value) ?? currency;
                continue;
            }

            if (fields[index].Tag != "61")
                continue;

            var info = index + 1 < fields.Count && fields[index + 1].Tag == "86" ? fields[index + 1].Value : null;
            if (!TryReadLine(fields[index].Value, info, currency, out var line))
                continue;

            movements++;
            if (movements > BankStatementLimits.MaxRows)
                throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
            if (line is null)
                continue;
            credits.Add(line);
            if (credits.Count > BankStatementLimits.MaxCredits)
                throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
        }

        if (movements == 0)
            throw new DomainRuleException(BankStatementErrorCodes.Empty, "BankStatementEmpty");
        return new ParsedBankStatement(BankStatementFormat.Mt940, credits, movements);
    }

    /// <summary>The tagged fields of the message, each with the lines that continue it.</summary>
    private static List<(string Tag, string Value)> Split(string text)
    {
        var fields = new List<(string Tag, List<string> Lines)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tag = TagLine().Match(line);
            if (tag.Success)
            {
                fields.Add((tag.Groups["tag"].Value, new List<string> { tag.Groups["value"].Value }));
                // A statement line comes with its information: twice the rows is room for both and for the balances.
                if (fields.Count > BankStatementLimits.MaxRows * 2)
                    throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
            }
            else if (fields.Count > 0 && line != "-" && !line.StartsWith("-}", StringComparison.Ordinal) && !line.StartsWith('}'))
                fields[^1].Lines.Add(line);
        }

        return fields.Select(field => (field.Tag, string.Join("\n", field.Lines))).ToList();
    }

    /// <summary>The currency of a statement from its opening balance («C261001EUR1000,00»: mark, date, currency, amount); null when it does not read so.</summary>
    private static string? ReadCurrency(string balance)
    {
        var text = balance.Trim();
        if (text.Length < 10 || text[0] is not ('C' or 'D'))
            return null;

        var code = text.Substring(7, 3);
        return code.All(char.IsAsciiLetterUpper) ? code : null;
    }

    /// <summary>
    /// Reads a statement line. False when the line is not one (so it is not counted); true with a null <paramref name="credit"/> for a
    /// debit, and with the credit otherwise. The reversals («RC», «RD») are counted and never offered: a reversal is the bank taking back
    /// or giving back an entry, not a tenant paying.
    /// </summary>
    private static bool TryReadLine(string value, string? information, string currency, out BankMovement? credit)
    {
        credit = null;
        var lines = value.Split('\n');
        var match = StatementLine().Match(lines[0].Trim());
        if (!match.Success)
            return false;

        var valueDate = ShortDate(match.Groups["vd"].Value, null);
        if (valueDate is null)
            return false;
        var bookingDate = match.Groups["ed"].Success ? ShortDate(match.Groups["vd"].Value[..2] + match.Groups["ed"].Value, valueDate) ?? valueDate.Value : valueDate.Value;

        if (!BankValues.TryParseAmount(new Cell(match.Groups["amt"].Value, false), DecimalMark.Comma, out var amount))
            return false;

        var isCredit = match.Groups["mark"].Value == "C";
        if (!isCredit || amount <= 0m)
            return true;

        // «customer reference//bank reference» and the supplementary line that may follow. What the bank adds is cut to a size no real
        // statement reaches before it is searched, so a file made of nothing else cannot keep the reader busy.
        var rest = match.Groups["rest"].Value;
        var split = rest.IndexOf("//", StringComparison.Ordinal);
        var bankReference = split >= 0 ? BankValues.Clip(rest[(split + 2)..], 100) : string.Empty;
        var supplementary = lines.Length > 1 ? BankValues.Clip(string.Join(' ', lines.Skip(1)), MaxInformationLength) : null;
        information = information is null ? null : BankValues.Clip(information, MaxInformationLength);

        var (description, name) = ReadInformation(information, supplementary);
        credit = new BankMovement(
            bookingDate,
            valueDate.Value,
            amount,
            currency,
            description,
            string.IsNullOrWhiteSpace(name) ? null : BankValues.Clip(name, 200),
            BankValues.FindIban($"{information} {supplementary}"),
            bankReference.Length == 0 ? null : bankReference);
        return true;
    }

    private static (string Description, string? Name) ReadInformation(string? information, string? supplementary)
    {
        var text = (information ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();

        var subfields = Subfield().Matches(text).Select(m => (Number: m.Groups["n"].Value, Value: m.Groups["v"].Value.Trim())).ToList();
        if (subfields.Count > 0)
        {
            // «?00» is the kind of transaction, «?20» to «?29» and «?60» to «?63» the reason of the payment.
            var reason = subfields
                .Where(s => s.Number == "00"
                    || (string.CompareOrdinal(s.Number, "20") >= 0 && string.CompareOrdinal(s.Number, "29") <= 0)
                    || (string.CompareOrdinal(s.Number, "60") >= 0 && string.CompareOrdinal(s.Number, "63") <= 0))
                .Select(s => s.Value);
            var name = string.Join(' ', subfields.Where(s => s.Number is "32" or "33").Select(s => s.Value));
            return (BankValues.JoinDistinct(reason.Append(supplementary)), name);
        }

        var tags = SlashTag().Matches(text).Select(m => (Tag: m.Groups["tag"].Value, Value: m.Groups["v"].Value.Trim())).ToList();
        if (tags.Count > 0)
        {
            // «/ORDP//NAME/Mario Rossi/REMI/affitto»: the payer is the first non-empty name, the reason the remittance information.
            var name = tags.Where(t => t.Tag is "NAME" or "ORDP" or "ORDPNAME" && t.Value.Length > 0).Select(t => t.Value).FirstOrDefault();
            var reason = tags.Where(t => t.Tag == "REMI").Select(t => t.Value);
            var described = BankValues.JoinDistinct(reason.Append(supplementary));
            return (described.Length > 0 ? described : BankValues.Clip(text.Replace('/', ' ')), name);
        }

        return (BankValues.JoinDistinct([text, supplementary]), null);
    }

    /// <summary>A <c>YYMMDD</c> date (or <c>YYMMDD</c> built from the year of the value date and a <c>MMDD</c> booking date, moved to the right year around New Year).</summary>
    private static DateOnly? ShortDate(string yymmdd, DateOnly? valueDate)
    {
        if (yymmdd.Length != 6
            || !int.TryParse(yymmdd.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(yymmdd.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(yymmdd.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return null;
        }

        try
        {
            var date = new DateOnly(2000 + year, month, day);
            if (valueDate is { } reference)
            {
                // A booking date six months or more away from the value date belongs to the next or the previous year.
                if (date.DayNumber - reference.DayNumber > 183)
                    date = date.AddYears(-1);
                else if (reference.DayNumber - date.DayNumber > 183)
                    date = date.AddYears(1);
            }

            return date;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
