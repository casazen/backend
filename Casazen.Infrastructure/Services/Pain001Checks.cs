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
    public const string Initiation = "CstmrCdtTrfInitn";

    public const string NumberOfTransactions = "NbOfTxs";

    public const string ControlSum = "CtrlSum";

    public const string InstructedAmount = "InstdAmt";

    public const string Currency = "Ccy";

    public const string EndToEndId = "EndToEndId";

    public const string ServiceLevel = "SvcLvl";

    public const string ChargeBearer = "ChrgBr";

    public const string PostalAddress = "PstlAdr";

    private const int MaxAddressLines = 2;

    private static readonly Regex AmountText = new(@"^\d{1,16}\.\d{2}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    private static bool IsStructuredOrHybrid(XElement? address, XNamespace ns)
    {
        return address is not null
               && !string.IsNullOrWhiteSpace(address.Element(ns + "TwnNm")?.Value)
               && !string.IsNullOrWhiteSpace(address.Element(ns + "Ctry")?.Value)
               && address.Elements(ns + "AdrLine").Count() <= MaxAddressLines;
    }

    private static bool TryAmount(string? text, out decimal value)
    {
        value = 0m;
        return text is not null
               && AmountText.IsMatch(text)
               && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

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
