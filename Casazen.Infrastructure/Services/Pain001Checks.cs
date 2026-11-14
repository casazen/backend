using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What a schema cannot say about a pain.001 wire file (E06/F05/US01/T01, E16/F01/US01/T01, E16/F01/US02/T01, NF-25): that the file is what its lines say, and that
/// the EUR file follows the rules of the SEPA credit transfer. The schema checks the shape; this checks the money, in exact decimals and never in a floating point
/// number, and the rules of the scheme, and answers with the name of the ISO element that is broken, never with a value of the file (an IBAN, a name, an amount).
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
/// <item><b>SvcLvl, ChrgBr, PstlAdr (EUR only).</b> The SEPA rules of the EPC that the ISO schema leaves open: every payment has the service level <c>SEPA</c> and the
/// charge bearer <c>SLEV</c>, and every debtor and creditor has a postal address that is structured or hybrid (a town and a country, with at most two free lines),
/// never one made of free lines alone. The BIC is optional, so its absence is no violation.</item>
/// </list>
/// The elements are read in the namespace of the message of the currency (<see cref="Pain001SchemaProvider.NamespaceOf"/>: <c>pain.001.001.09</c> for EUR and for CHF
/// alike): a file written in another namespace has no initiation message at all. The document is read with the DTD prohibited and no resolver: no external entity
/// and no network, as in the validation against the schema.
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

    /// <summary>A payment of the EUR file has not the service level of the SEPA credit transfer (<c>PmtTpInf/SvcLvl/Cd</c> is not <c>SEPA</c>).</summary>
    public const string ServiceLevel = "SvcLvl";

    /// <summary>A payment of the EUR file has not the charge bearer of the SEPA credit transfer (<c>ChrgBr</c> is not <c>SLEV</c>).</summary>
    public const string ChargeBearer = "ChrgBr";

    /// <summary>A debtor or a creditor of the EUR file has no structured or hybrid postal address (a town and a country, at most two free lines).</summary>
    public const string PostalAddress = "PstlAdr";

    /// <summary>The most free lines of a hybrid address (<c>AdrLine</c>).</summary>
    private const int MaxAddressLines = 2;

    // \z, not $: a text that ends with a line break is not an amount.
    private static readonly Regex AmountText = new(@"^\d{1,16}\.\d{2}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The names of the elements whose rule <paramref name="xml"/> breaks, in the order they were found and each once; empty when the file is what its lines
    /// say and follows the rules of its message. <paramref name="expectedTotal"/> and <paramref name="expectedCount"/> are the total and the number of transfers of
    /// the batch the file was written for.
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

        var ns = XNamespace.Get(Pain001SchemaProvider.NamespaceOf(currency));
        var initiation = Read(xml).Root?.Element(ns + "CstmrCdtTrfInitn");
        var header = initiation?.Element(ns + "GrpHdr");
        var payments = initiation?.Elements(ns + "PmtInf").ToList() ?? [];
        if (header is null || payments.Count == 0)
        {
            Broken(Initiation);
            return violations;
        }

        var sepa = string.Equals(currency, WireTransferErrors.SepaCurrency, StringComparison.Ordinal);
        var endToEndIds = new HashSet<string>(StringComparer.Ordinal);
        var total = 0m;
        var count = 0;
        foreach (var payment in payments)
        {
            if (sepa)
                SepaRules(payment, ns, Broken);

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

    /// <summary>
    /// The rules of the SEPA credit transfer of the EPC for one payment of the EUR file (the file CasaZen writes has the service level and the charge bearer in
    /// the payment, never in a transfer): the service level <c>SEPA</c>, the charge bearer <c>SLEV</c>, and a structured or hybrid address for the debtor and for every
    /// creditor.
    /// </summary>
    private static void SepaRules(XElement payment, XNamespace ns, Action<string> broken)
    {
        var serviceLevels = payment.Element(ns + "PmtTpInf")?.Elements(ns + "SvcLvl").Select(s => s.Element(ns + "Cd")?.Value) ?? [];
        if (!serviceLevels.Any(code => string.Equals(code, Pain001V09Writer.SepaServiceLevel, StringComparison.Ordinal)))
            broken(ServiceLevel);

        if (!string.Equals(payment.Element(ns + "ChrgBr")?.Value, Pain001V09Writer.SepaChargeBearer, StringComparison.Ordinal))
            broken(ChargeBearer);

        var parties = payment.Elements(ns + "Dbtr").Concat(payment.Elements(ns + "CdtTrfTxInf").Elements(ns + "Cdtr"));
        if (parties.Any(party => !IsStructuredOrHybrid(party.Element(ns + "PstlAdr"), ns)))
            broken(PostalAddress);
    }

    /// <summary>
    /// True for a postal address that is structured or hybrid: a town and a country, and at most two free lines (an address made of free lines alone, the
    /// unstructured form, has neither a town nor a country of its own).
    /// </summary>
    private static bool IsStructuredOrHybrid(XElement? address, XNamespace ns)
    {
        return address is not null
               && !string.IsNullOrWhiteSpace(address.Element(ns + "TwnNm")?.Value)
               && !string.IsNullOrWhiteSpace(address.Element(ns + "Ctry")?.Value)
               && address.Elements(ns + "AdrLine").Count() <= MaxAddressLines;
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
