using System.Xml;
using System.Xml.Schema;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit;

/// <summary>
/// Stand-ins for the official pain.001 schemas in the tests of the wire service (E06/F05/US01/T01, E16/F01/US01/T01, E16/F01/US02/T01). The official files are not in
/// the repository until an operator adds them with their checksums (docs/runbooks/wire-transfers.md), and the service refuses a file it cannot validate: a test that
/// wants to reach what comes after the validation (the sums, the save) has to give the service a schema of its own. These are not copies of the official schema and
/// say nothing about the shape of a real file: <see cref="Accepting()"/> takes any content of a <c>Document</c> and <see cref="Rejecting"/> takes none of what the
/// service writes. Both messages of the service (<c>pain.001.001.09</c> for EUR, <c>pain.001.001.09.ch.03</c> for CHF) are written in the same namespace, so one
/// schema serves both currencies.
/// </summary>
internal static class Pain001TestSchemas
{
    /// <summary>The namespace of <c>pain.001.001.03</c>: the message CasaZen wrote for EUR before E16/F01/US02/T01 and writes no more. A schema of it is the schema of another message.</summary>
    public const string RetiredSepaNamespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    /// <summary>The text of a schema for a <c>Document</c> of the namespace of the messages of the service (<c>pain.001.001.09</c>) with any content.</summary>
    public static string AcceptingXsd => AcceptingXsdFor(Pain001SchemaProvider.IsoNamespace);

    /// <summary>The text of a schema for a <c>Document</c> of the namespace of the messages of the service that must hold an element the service never writes.</summary>
    public static string RejectingXsd => RejectingXsdFor(Pain001SchemaProvider.IsoNamespace);

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

    /// <summary>A set that accepts any content of a <c>Document</c> of the namespace of the messages of the service: one set serves both currencies.</summary>
    public static XmlSchemaSet Accepting()
    {
        return Compile(AcceptingXsdFor(Pain001SchemaProvider.IsoNamespace));
    }

    /// <summary>A set in which every file the service writes, in either currency, has schema errors.</summary>
    public static XmlSchemaSet Rejecting()
    {
        return Compile(RejectingXsdFor(Pain001SchemaProvider.IsoNamespace));
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
