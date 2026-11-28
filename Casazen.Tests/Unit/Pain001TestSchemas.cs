using System.Xml;
using System.Xml.Schema;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit;

/// <summary>
/// Stand-ins for the official pain.001 schemas in the tests of the wire service (E06/F05/US01/T01, E16/F01/US01/T01, E16/F01/US02/T01). The official files are in the
/// repository, embedded in the build and pinned by their SHA-256 in docs/runbooks/wire-transfers.md, and the proofs about the file validate against them. The tests that
/// are about what comes after the validation (the sums, the save, the owners, the debtor) do not need the shape of a real file, so they give the service a schema of
/// their own, which also keeps them independent of the official files. These are not copies of the official schema and say nothing about the shape of a real file:
/// <see cref="Accepting()"/> takes any content of a <c>Document</c> and <see cref="Rejecting"/> takes none of what the service writes. Both messages of the service
/// (<c>pain.001.001.09</c> for EUR, <c>pain.001.001.09.ch.03</c> for CHF) are written in the same namespace, so one schema serves both currencies.
/// </summary>
internal static class Pain001TestSchemas
{
    public const string RetiredSepaNamespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    public static string AcceptingXsd => AcceptingXsdFor(Pain001SchemaProvider.IsoNamespace);

    public static string RejectingXsd => RejectingXsdFor(Pain001SchemaProvider.IsoNamespace);

    public static string AcceptingXsdFor(string targetNamespace)
    {
        return Header(targetNamespace) +
               "<xs:element name=\"Document\"><xs:complexType><xs:sequence>" +
               "<xs:any processContents=\"skip\" minOccurs=\"0\" maxOccurs=\"unbounded\"/>" +
               "</xs:sequence></xs:complexType></xs:element></xs:schema>";
    }

    public static string RejectingXsdFor(string targetNamespace)
    {
        return Header(targetNamespace) +
               "<xs:element name=\"Document\"><xs:complexType><xs:sequence>" +
               "<xs:element name=\"NeverWritten\" type=\"xs:string\"/>" +
               "</xs:sequence></xs:complexType></xs:element></xs:schema>";
    }

    public static XmlSchemaSet Accepting()
    {
        return Compile(AcceptingXsdFor(Pain001SchemaProvider.IsoNamespace));
    }

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
