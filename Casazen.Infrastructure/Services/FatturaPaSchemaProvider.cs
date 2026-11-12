using System.Reflection;
using System.Xml;
using System.Xml.Schema;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The official schemas of FatturaPA (format FPR12) and of the XML signature it imports, loaded once from the resources embedded in this assembly
/// (<c>Fiscal/Schemas/*.xsd</c>, NF-83, NF-87, E06/F01/US02/T03) into one cached <see cref="XmlSchemaSet"/>. The files are the ones the Agenzia delle
/// Entrate publishes, with their source and SHA-256 in <c>docs/runbooks/fiscal-documents.md</c>; a schema is never written or rebuilt here.
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
