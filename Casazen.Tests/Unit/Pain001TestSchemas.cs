using System.Xml;
using System.Xml.Schema;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit;

/// <summary>
/// Stand-ins for the official pain.001 schemas in the tests of the wire service (E06/F05/US01/T01, E16/F01/US01/T01). The official files are not in the
/// repository until an operator adds them with their checksums (docs/runbooks/wire-transfers.md), and the service refuses a file it cannot validate: a test that
/// wants to reach what comes after the validation (the sums, the save) has to give the service a schema of its own. These are not copies of the official schema and
/// say nothing about the shape of a real file: <see cref="Accepting()"/> takes any content of a <c>Document</c> and <see cref="Rejecting"/> takes none of what the
/// service writes. Each set has a schema for both messages of the service (<c>pain.001.001.03</c> for EUR, <c>pain.001.001.09</c> for CHF), so one set serves both
/// currencies.
/// </summary>
internal static class Pain001TestSchemas
{
    /// <summary>The text of a schema for a <c>Document</c> of the SEPA namespace (<c>pain.001.001.03</c>) with any content.</summary>
    public static string AcceptingXsd => AcceptingXsdFor(Pain001SchemaProvider.SepaNamespace);

    /// <summary>The text of a schema for a <c>Document</c> of the SEPA namespace that must hold an element the service never writes.</summary>
    public static string RejectingXsd => RejectingXsdFor(Pain001SchemaProvider.SepaNamespace);

    /// <summary>The text of a schema for a <c>Document</c> of <paramref name="targetNamespace"/> with any content: nothing the service writes is a schema error.</summary>
    public static string AcceptingXsdFor(string targetNamespace)
    {
        return Header(targetNamespace) +
               "<xs:element name=\"Document\"><xs:complexType><xs:sequence>" +
               "<xs:any processContents=\"skip\" minOccurs=\"0\" maxOccurs=\"unbounded\"/>" +
               "</xs:sequence></xs:complexType></xs:element></xs:schema>";
    }

    /// <summary>The text of a schema for a <c>Document</c> of <paramref name="targetNamespace"/> that must hold an element the service never writes.</summary>
    public static string RejectingXsdFor(string targetNamespace)
    {
        return Header(targetNamespace) +
               "<xs:element name=\"Document\"><xs:complexType><xs:sequence>" +
               "<xs:element name=\"NeverWritten\" type=\"xs:string\"/>" +
               "</xs:sequence></xs:complexType></xs:element></xs:schema>";
    }

    /// <summary>A set that accepts any content of a <c>Document</c> of either message.</summary>
    public static XmlSchemaSet Accepting()
    {
        return Compile(AcceptingXsdFor(Pain001SchemaProvider.SepaNamespace), AcceptingXsdFor(Pain001SchemaProvider.SwissNamespace));
    }

    /// <summary>A set in which every file the service writes, in either message, has schema errors.</summary>
    public static XmlSchemaSet Rejecting()
    {
        return Compile(RejectingXsdFor(Pain001SchemaProvider.SepaNamespace), RejectingXsdFor(Pain001SchemaProvider.SwissNamespace));
    }

    private static string Header(string targetNamespace)
    {
        return "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"" + targetNamespace + "\" elementFormDefault=\"qualified\">";
    }

    private static XmlSchemaSet Compile(params string[] schemas)
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        foreach (var schema in schemas)
        {
            using var text = new StringReader(schema);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
        }

        set.Compile();
        return set;
    }
}
