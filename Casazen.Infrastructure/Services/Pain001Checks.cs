using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What a schema cannot say about a pain.001 wire file (E06/F05/US01/T01, NF-25): that the file is what its lines say. The schema checks the shape; this
/// checks the money, in exact decimals and never in a floating point number, and answers with the name of the ISO element that is broken, never with a
/// value of the file (an IBAN, a name, an amount).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>InstdAmt, Ccy.</b> Every instructed amount is a positive number with exactly two decimals, and the currency of the batch is written next to it:
/// a missing or different currency is a violation (the currency is always explicit).</item>
/// <item><b>NbOfTxs.</b> The number of operations of the header, and of each payment, is the number of its transfers; the one of the header is also the
/// one the batch says.</item>
/// <item><b>CtrlSum.</b> The control sum of the header, and of each payment, is exactly the sum of its amounts; the one of the header is also the total the
/// batch says.</item>
/// <item><b>EndToEndId.</b> Every transfer has one and it is unique in the file.</item>
/// </list>
/// The document is read with the DTD prohibited and no resolver: no external entity and no network, as in the validation against the schema.
/// </remarks>
internal static class Pain001Checks
{
    /// <summary>The document has no initiation message, no group header or no payment at all.</summary>
    public const string Initiation = "CstmrCdtTrfInitn";

    /// <summary>The number of operations is not the number of the transfers (or not the one of the batch).</summary>
    public const string NumberOfTransactions = "NbOfTxs";

    /// <summary>The control sum is not the sum of the amounts (or not the total of the batch).</summary>
    public const string ControlSum = "CtrlSum";

    /// <summary>An amount is not a positive number with two decimals.</summary>
    public const string InstructedAmount = "InstdAmt";

    /// <summary>The currency of an amount is missing or is not the one of the batch.</summary>
    public const string Currency = "Ccy";

    /// <summary>A transfer has no end-to-end id, or two of them have the same.</summary>
    public const string EndToEndId = "EndToEndId";

    // \z, not $: a text that ends with a line break is not an amount.
    private static readonly Regex AmountText = new(@"^\d{1,16}\.\d{2}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The names of the elements whose rule <paramref name="xml"/> breaks, in the order they were found and each once; empty when the file is what its lines
    /// say. <paramref name="expectedTotal"/> and <paramref name="expectedCount"/> are the total and the number of transfers of the batch the file was written for.
    /// </summary>
    public static IReadOnlyList<string> Violations(string xml, string currency, decimal expectedTotal, int expectedCount)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(currency);

        var violations = new List<string>();
        void Broken(string rule)
        {
            if (!violations.Contains(rule, StringComparer.Ordinal))
                violations.Add(rule);
        }

        var ns = XNamespace.Get(Pain001SchemaProvider.Namespace);
        var initiation = Read(xml).Root?.Element(ns + "CstmrCdtTrfInitn");
        var header = initiation?.Element(ns + "GrpHdr");
        var payments = initiation?.Elements(ns + "PmtInf").ToList() ?? [];
        if (header is null || payments.Count == 0)
        {
            Broken(Initiation);
            return violations;
        }

        var endToEndIds = new HashSet<string>(StringComparer.Ordinal);
        var total = 0m;
        var count = 0;
        foreach (var payment in payments)
        {
            var paymentTotal = 0m;
            var paymentCount = 0;
            foreach (var transfer in payment.Elements(ns + "CdtTrfTxInf"))
            {
                paymentCount++;

                var endToEndId = transfer.Element(ns + "PmtId")?.Element(ns + "EndToEndId")?.Value;
                if (string.IsNullOrWhiteSpace(endToEndId) || !endToEndIds.Add(endToEndId))
                    Broken(EndToEndId);

                var amount = transfer.Element(ns + "Amt")?.Element(ns + "InstdAmt");
                var value = 0m;
                var isAmount = amount is not null && TryAmount(amount.Value, out value);
                if (!isAmount || value <= 0m)
                    Broken(InstructedAmount);
                if (!string.Equals(amount?.Attribute("Ccy")?.Value, currency, StringComparison.Ordinal))
                    Broken(Currency);

                paymentTotal += isAmount ? value : 0m;
            }

            if (!TryCount(payment.Element(ns + "NbOfTxs")?.Value, out var declaredPaymentCount) || declaredPaymentCount != paymentCount)
                Broken(NumberOfTransactions);
            if (!TryAmount(payment.Element(ns + "CtrlSum")?.Value, out var declaredPaymentSum) || declaredPaymentSum != paymentTotal)
                Broken(ControlSum);

            total += paymentTotal;
            count += paymentCount;
        }

        if (!TryCount(header.Element(ns + "NbOfTxs")?.Value, out var declaredCount) || declaredCount != count || count != expectedCount)
            Broken(NumberOfTransactions);
        if (!TryAmount(header.Element(ns + "CtrlSum")?.Value, out var declaredSum) || declaredSum != total || total != expectedTotal)
            Broken(ControlSum);

        return violations;
    }

    /// <summary>True for a plain decimal number with exactly two decimals (digits, a point, two digits: no sign, no exponent, no group separator).</summary>
    private static bool TryAmount(string? text, out decimal value)
    {
        value = 0m;
        return text is not null
               && AmountText.IsMatch(text)
               && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>True for a count written with digits only.</summary>
    private static bool TryCount(string? text, out int value)
    {
        value = 0;
        return text is not null && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static XDocument Read(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
}
