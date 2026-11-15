using System.Reflection;
using System.Xml;
using System.Xml.Schema;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The official schemas of FatturaPA (format FPR12) and of the XML signature it imports, loaded once from the resources embedded in this assembly
/// (<c>Fiscal/Schemas/*.xsd</c>, NF-83, NF-87, E06/F01/US02/T03, E17/F01/US01/T03) into one cached <see cref="XmlSchemaSet"/>. The files are the ones their publishers
/// give, byte for byte (<see cref="OfficialFiles"/>: the source, the version and the SHA-256, also in <c>docs/runbooks/fiscal-documents.md</c>); a schema is
/// never written or rebuilt here.
/// </summary>
/// <remarks>
/// The set is compiled without a resolver: the import of the signature schema is satisfied by the embedded file, never fetched. A build without the
/// schema files has no set: <see cref="GetSchemas"/> answers 422 <c>fatturapa_schema_unavailable</c> and no XML is produced, never an XML that was
/// not validated.
/// </remarks>
public sealed class FatturaPaSchemaProvider
{
    /// <summary>The prefix of the logical names of the embedded schemas.</summary>
    public const string ResourcePrefix = "Casazen.Infrastructure.Fiscal.Schemas.";

    /// <summary>The target namespace of FatturaPA 1.2.</summary>
    public const string FatturaPaNamespace = "http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2";

    private static readonly Lazy<XmlSchemaSet?> Cached = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The official files embedded in the build (E17/F01/US01/T03), taken from their publishers on 2026-11-14: the schema of the ordinary invoice FPR12 version
    /// 1.2.3 (the documentation valid from 2025-04-01, Specifiche tecniche FatturaPA 1.4) of the Agenzia delle Entrate, and the W3C XML signature schema it imports
    /// (its <c>schemaLocation</c>). A test checks the SHA-256 of every embedded file against these.
    /// </summary>
    public static IReadOnlyList<FatturaPaSchemaFile> OfficialFiles { get; } =
    [
        new(
            "Schema_VFPR12_v1.2.3.xsd",
            "1.2.3",
            "https://www.fatturapa.gov.it/export/documenti/fatturapa/v1.4/Schema_VFPR12_v1.2.3.xsd",
            "152944f6eef9f5d69ef6e955ee173b32142b00a8c1c5222fc97dfab5910e8a8c"),
        new(
            "xmldsig-core-schema.xsd",
            "W3C Recommendation 2002-02-12",
            "https://www.w3.org/TR/2002/REC-xmldsig-core-20020212/xmldsig-core-schema.xsd",
            "35cf8197da812c85e40d57891b35c94187569ed474a2dac813ce5090dafcd35c"),
    ];

    /// <summary>True when the official schema of FatturaPA is embedded in this build.</summary>
    public static bool HasOfficialSchema => Cached.Value is not null;

    /// <summary>The compiled schemas; 422 <c>fatturapa_schema_unavailable</c> when they are not installed.</summary>
    public XmlSchemaSet GetSchemas() =>
        Cached.Value ?? throw new DomainRuleException(FatturaPaErrors.SchemaUnavailable, "FatturaPaSchemaUnavailable");

    private static XmlSchemaSet? Load()
    {
        var assembly = typeof(FatturaPaSchemaProvider).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return null;

        var set = new XmlSchemaSet { XmlResolver = null };

        // The embedded schemas are files of this build, never input: the internal DTD of the W3C signature schema is parsed (its entities are
        // internal), nothing external is ever resolved and the expansion is bounded. The documents themselves are read with the DTD prohibited.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 4096 };

        // The signature schema first: the FatturaPA schema imports its namespace.
        foreach (var name in names.OrderBy(n => n.Contains("xmldsig", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = XmlReader.Create(stream, settings);
            set.Add(null, reader);
        }

        set.Compile();
        return set.Contains(FatturaPaNamespace) ? set : null;
    }

    /// <summary>The names of the embedded schemas (for the runbook check of the checksums).</summary>
    public static IReadOnlyList<string> EmbeddedSchemaNames() =>
        Assembly.GetAssembly(typeof(FatturaPaSchemaProvider))!.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
}

/// <summary>An official schema file embedded in the build: its file name, version, the address it was taken from and the SHA-256 of its bytes (lower case hex).</summary>
public sealed record FatturaPaSchemaFile(string FileName, string Version, string SourceUrl, string Sha256);
