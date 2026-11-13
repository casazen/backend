using System.Xml;
using System.Xml.Schema;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit;

/// <summary>
/// Stand-ins for the official pain.001 schemas in the tests of the wire service (E06/F05/US01/T01). The official files are not in the repository until an
/// operator adds them with their checksums (docs/runbooks/wire-transfers.md), and the service refuses a file it cannot validate: a test that wants to reach what
/// comes after the validation (the sums, the save) has to give the service a schema of its own. These are not copies of the official schema and say nothing about
/// the shape of a real file: <see cref="Accepting"/> takes any content of a <c>Document</c> and <see cref="Rejecting"/> takes none of what the service writes.
/// </summary>
internal static class Pain001TestSchemas
{
    private const string Header =
        "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"" + Pain001SchemaProvider.Namespace + "\" elementFormDefault=\"qualified\">";

    /// <summary>The text of a schema for a <c>Document</c> of the pain.001 namespace with any content: nothing the service writes is a schema error.</summary>
    public const string AcceptingXsd =
        Header +
        "<xs:element name=\"Document\"><xs:complexType><xs:sequence>" +
        "<xs:any processContents=\"skip\" minOccurs=\"0\" maxOccurs=\"unbounded\"/>" +
        "</xs:sequence></xs:complexType></xs:element></xs:schema>";

    /// <summary>The text of a schema for a <c>Document</c> that must hold an element the service never writes: every file the service writes has errors.</summary>
    public const string RejectingXsd =
        Header +
        "<xs:element name=\"Document\"><xs:complexType><xs:sequence>" +
        "<xs:element name=\"NeverWritten\" type=\"xs:string\"/>" +
        "</xs:sequence></xs:complexType></xs:element></xs:schema>";

    /// <summary>A set that accepts any content of a <c>Document</c> of the pain.001 namespace.</summary>
    public static XmlSchemaSet Accepting() => Compile(AcceptingXsd);

    /// <summary>A set in which every file the service writes has schema errors.</summary>
    public static XmlSchemaSet Rejecting() => Compile(RejectingXsd);

    private static XmlSchemaSet Compile(string schema)
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        using var text = new StringReader(schema);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(null, reader);
        set.Compile();
        return set;
    }
}
