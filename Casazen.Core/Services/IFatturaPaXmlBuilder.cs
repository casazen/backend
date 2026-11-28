namespace Casazen.Core.Services;

/// <summary>
/// Writes the FatturaPA XML (format FPR12) of a guest or direct-booking document and validates it against the official schema before it is returned
/// (FIS-03, FIS-07, NF-83, NF-87, ADR-019-R6, E06/F01/US02/T03). Owner statements never go through it.
/// </summary>
public interface IFatturaPaXmlBuilder
{
    /// <summary>
    /// The XML of <paramref name="input"/>, written with an XML writer (every text escaped, decimals in invariant culture, dates as calendar days)
    /// and validated against the official schema. 422 <c>fatturapa_xml_invalid</c> when it does not validate (the message lists the first errors
    /// by line and position, no value of the document), 422 <c>fatturapa_schema_unavailable</c> when the official schema is not installed: nothing
    /// is returned unvalidated.
    /// </summary>
    string Build(FatturaPaInput input);
}

public static class FatturaPaErrors
{
    public const string XmlInvalid = "fatturapa_xml_invalid";

    public const string SchemaUnavailable = "fatturapa_schema_unavailable";

    public const int ListedErrors = 5;
}
