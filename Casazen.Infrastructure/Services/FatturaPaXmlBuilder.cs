using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFatturaPaXmlBuilder"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Written, never concatenated.</b> The document is an <see cref="XDocument"/> serialized by an <see cref="XmlWriter"/>: every text is escaped,
/// the amounts are written in invariant culture with the decimals the schema wants, the days as <c>yyyy-MM-dd</c>.</item>
/// <item><b>Validated before it is returned</b> against the official schema (<see cref="FatturaPaSchemaProvider"/>, <see cref="XsdValidator"/>): an
/// invalid document is 422 <c>fatturapa_xml_invalid</c>, whose message names the lines and positions of the first errors and no value; the log carries
/// the number of the document's lines and of the errors only.</item>
/// <item><b>Destination code.</b> The recipient's SDI code; <c>0000000</c> with the PEC, or with nothing (the recipient finds it in its fiscal
/// drawer); <c>XXXXXXX</c> for a recipient outside Italy.</item>
/// </list>
/// </remarks>
public sealed class FatturaPaXmlBuilder(FatturaPaSchemaProvider schemas, ILogger<FatturaPaXmlBuilder> logger) : IFatturaPaXmlBuilder
{
    /// <summary>The transmission format of the documents to private parties.</summary>
    public const string TransmissionFormat = "FPR12";

    /// <summary>The destination code without a channel: the PEC, or the fiscal drawer of the recipient.</summary>
    public const string NoChannelCode = "0000000";

    /// <summary>The destination code of a recipient outside Italy.</summary>
    public const string ForeignRecipientCode = "XXXXXXX";

    private static readonly XNamespace P = FatturaPaSchemaProvider.FatturaPaNamespace;
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public string Build(FatturaPaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var schemaSet = schemas.GetSchemas();
        var xml = Write(input);
        var errors = XsdValidator.Validate(xml, schemaSet);
        if (errors.Count == 0)
            return xml;

        var positions = string.Join(", ", errors.Take(FatturaPaErrors.ListedErrors).Select(e => $"{e.Line}:{e.Position}"));
        logger.LogWarning(
            "FatturaPA document of {LineCount} lines does not validate against the official schema: {ErrorCount} errors",
            input.Lines.Count,
            errors.Count);
        throw new DomainRuleException(FatturaPaErrors.XmlInvalid, "FatturaPaXmlInvalid", errors.Count, positions);
    }

    /// <summary>The XML of <paramref name="input"/>, not validated (the validation is <see cref="Build"/>'s).</summary>
    internal static string Write(FatturaPaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                P + "FatturaElettronica",
                new XAttribute("versione", TransmissionFormat),
                new XAttribute(XNamespace.Xmlns + "p", P),
                new XAttribute(XNamespace.Xmlns + "ds", Ds),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                Header(input),
                Body(input)));

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false };
        using var text = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(text, settings))
            document.Save(writer);
        return text.ToString();
    }

    /// <summary>The destination code of the recipient (see the remarks).</summary>
    public static string DestinationCode(string? recipientCountry, string? recipientCode)
    {
        if (!string.Equals(recipientCountry?.Trim(), "IT", StringComparison.OrdinalIgnoreCase))
            return ForeignRecipientCode;

        return string.IsNullOrWhiteSpace(recipientCode) ? NoChannelCode : recipientCode.Trim().ToUpperInvariant();
    }

    private static XElement Header(FatturaPaInput input)
    {
        var destination = DestinationCode(input.Recipient.Address.CountryCode, input.RecipientCode);
        return new XElement(
            "FatturaElettronicaHeader",
            new XElement(
                "DatiTrasmissione",
                new XElement("IdTrasmittente", new XElement("IdPaese", input.Transmitter.CountryCode), new XElement("IdCodice", input.Transmitter.Code)),
                new XElement("ProgressivoInvio", input.ProgressivoInvio),
                new XElement("FormatoTrasmissione", TransmissionFormat),
                new XElement("CodiceDestinatario", destination),
                destination == NoChannelCode && !string.IsNullOrWhiteSpace(input.RecipientPec) ? new XElement("PECDestinatario", input.RecipientPec) : null),
            new XElement(
                "CedentePrestatore",
                new XElement(
                    "DatiAnagrafici",
                    FiscalId(input.Issuer.VatId),
                    FiscalCode(input.Issuer.FiscalCode),
                    Registry(input.Issuer),
                    new XElement("RegimeFiscale", input.Issuer.RegimeCode)),
                Address(input.Issuer.Address)),
            new XElement(
                "CessionarioCommittente",
                new XElement(
                    "DatiAnagrafici",
                    FiscalId(input.Recipient.VatId),
                    FiscalCode(input.Recipient.FiscalCode),
                    Registry(input.Recipient)),
                Address(input.Recipient.Address)));
    }

    private static XElement Body(FatturaPaInput input) =>
        new(
            "FatturaElettronicaBody",
            new XElement(
                "DatiGenerali",
                new XElement(
                    "DatiGeneraliDocumento",
                    new XElement("TipoDocumento", input.DocumentTypeCode),
                    new XElement("Divisa", input.Currency),
                    new XElement("Data", Day(input.IssueDate)),
                    new XElement("Numero", input.Number),
                    input.StampDuty > 0m
                        ? new XElement("DatiBollo", new XElement("BolloVirtuale", "SI"), new XElement("ImportoBollo", Amount(input.StampDuty)))
                        : null,
                    new XElement("ImportoTotaleDocumento", Amount(input.Total))),
                input.RelatedDocument is { } related
                    ? new XElement(
                        "DatiFattureCollegate",
                        new XElement("IdDocumento", related.Number),
                        related.IssueDate is { } day ? new XElement("Data", Day(day)) : null)
                    : null),
            new XElement(
                "DatiBeniServizi",
                input.Lines.Select(line => new XElement(
                    "DettaglioLinee",
                    new XElement("NumeroLinea", line.LineNumber.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Descrizione", line.Description),
                    new XElement("Quantita", Precise(line.Quantity)),
                    new XElement("PrezzoUnitario", Precise(line.UnitPrice)),
                    new XElement("PrezzoTotale", Amount(line.TotalPrice)),
                    new XElement("AliquotaIVA", Amount(line.VatRatePercent)),
                    line.NatureCode is { } nature ? new XElement("Natura", nature) : null)),
                input.Summary.Select(row => new XElement(
                    "DatiRiepilogo",
                    new XElement("AliquotaIVA", Amount(row.VatRatePercent)),
                    row.NatureCode is { } nature ? new XElement("Natura", nature) : null,
                    new XElement("ImponibileImporto", Amount(row.TaxableAmount)),
                    new XElement("Imposta", Amount(row.Tax)),
                    row.VatRatePercent > 0m ? new XElement("EsigibilitaIVA", "I") : null))));

    private static XElement? FiscalId(FatturaPaId? id) =>
        id is null ? null : new XElement("IdFiscaleIVA", new XElement("IdPaese", id.CountryCode), new XElement("IdCodice", id.Code));

    private static XElement? FiscalCode(string? code) => string.IsNullOrWhiteSpace(code) ? null : new XElement("CodiceFiscale", code);

    private static XElement Registry(FatturaPaParty party) =>
        !string.IsNullOrWhiteSpace(party.Denomination)
            ? new XElement("Anagrafica", new XElement("Denominazione", party.Denomination))
            : new XElement("Anagrafica", new XElement("Nome", party.FirstName), new XElement("Cognome", party.LastName));

    private static XElement Address(FatturaPaAddress address) =>
        new(
            "Sede",
            new XElement("Indirizzo", address.Street),
            string.IsNullOrWhiteSpace(address.Civic) ? null : new XElement("NumeroCivico", address.Civic),
            new XElement("CAP", address.PostalCode),
            new XElement("Comune", address.City),
            string.IsNullOrWhiteSpace(address.Province) ? null : new XElement("Provincia", address.Province),
            new XElement("Nazione", address.CountryCode));

    /// <summary>An amount with the two decimals of the schema, in invariant culture.</summary>
    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A quantity or a unit price: two to eight decimals.</summary>
    private static string Precise(decimal value) => value.ToString("0.00######", CultureInfo.InvariantCulture);

    /// <summary>A calendar day as the schema writes it.</summary>
    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
