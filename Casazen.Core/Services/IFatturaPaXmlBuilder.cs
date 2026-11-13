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

/// <summary>The stable codes of the errors of the XML, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class FatturaPaErrors
{
    /// <summary>422: the XML does not validate against the official schema.</summary>
    public const string XmlInvalid = "fatturapa_xml_invalid";

    /// <summary>422: the official schema is not installed in this build, so no XML can be validated (and none is produced).</summary>
    public const string SchemaUnavailable = "fatturapa_schema_unavailable";

    /// <summary>How many errors the message of <see cref="XmlInvalid"/> lists.</summary>
    public const int ListedErrors = 5;
}
