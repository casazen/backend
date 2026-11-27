using System.Xml;
using System.Xml.Schema;

namespace Casazen.Core.Utilities;

/// <summary>
/// Validates an XML text against a set of XML schemas (NF-83, NF-87, E06/F01/US02/T03): the FatturaPA documents against the official schema of the
/// Agenzia delle Entrate, and any other generated XML (the pain.001 wire files reuse it). Generic: it knows no schema of its own.
/// </summary>
/// <remarks>
/// The reader never resolves anything: <see cref="DtdProcessing.Prohibit"/> (a DTD is an error, so no entity can be expanded) and no
/// <see cref="XmlResolver"/>, so no external entity, no schema location and no network connection is ever opened. The schemas come from the
/// caller's <see cref="XmlSchemaSet"/> only (compile it without a resolver too).
/// </remarks>
public static class XsdValidator
{
    /// <summary>
    /// The errors of <paramref name="xml"/> against <paramref name="schemas"/>, in the order they were found (line and position of each); empty when the
    /// document is valid. A text that is not well-formed is one error at the position the parser stopped.
    /// </summary>
    public static IReadOnlyList<XsdValidationError> Validate(string xml, XmlSchemaSet schemas)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(schemas);

        var errors = new List<XsdValidationError>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
            Schemas = schemas,
        };
        settings.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
                errors.Add(new XsdValidationError(e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0, e.Message));
        };

        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            errors.Add(new XsdValidationError(ex.LineNumber, ex.LinePosition, ex.Message));
        }

        return errors;
    }
}

/// <summary>
/// One error of <see cref="XsdValidator.Validate"/>. <see cref="Message"/> is the parser's text, which can quote a value of the document: it is for the
/// developer, never for a log line or an answer that may carry personal data (use the line and the position there).
/// </summary>
public sealed record XsdValidationError(int Line, int Position, string Message);
