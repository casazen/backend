using System.Xml;
using System.Xml.Schema;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The official schemas of the wire files, loaded once from the resources embedded in this assembly (E06/F05/US01/T01, E16/F01/US01/T01, NF-25, NF-113): the
/// ISO 20022 <c>pain.001.001.03</c> schema of the SEPA credit transfers (EUR, <c>Payments/Schemas/Sepa</c>) and the schema of the Swiss Payment Standards of the
/// Swiss file, <c>pain.001.001.09.ch.03.xsd</c> (CHF, <c>Payments/Schemas/Swiss</c>), each one in its own cached <see cref="XmlSchemaSet"/> and each one with
/// its own target namespace (<see cref="SepaNamespace"/>, <see cref="SwissNamespace"/>). The files are the ones their publishers give, with their source and
/// SHA-256 in <c>docs/runbooks/wire-transfers.md</c>: a schema is never written or rebuilt here.
/// </summary>
/// <remarks>
/// <para>Static, so there is nothing to register. The sets are compiled without a resolver (no import or include is ever fetched, no connection is
/// opened) and are only read afterwards.</para>
/// <para>A build without the schema of a currency has no set for it: <see cref="GetSchemas"/> answers 422 <c>wire_batch_schema_unavailable</c>, never a
/// permissive set, so no wire file of that currency is produced without its validation. The same when the embedded files do not compile, or are not the
/// schema of the message the file is written in (they do not have its target namespace: the reason is in <see cref="LoadProblem"/>): an unusable schema is
/// an unavailable one, and never a file that was not validated.</para>
/// </remarks>
public static class Pain001SchemaProvider
{
    /// <summary>The target namespace of <c>pain.001.001.03</c>: the one of the SEPA schema and of the EUR file CasaZen writes.</summary>
    public const string SepaNamespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    /// <summary>
    /// The target namespace of <c>pain.001.001.09</c>: the one of the CHF file CasaZen writes, <c>pain.001.001.09.ch.03</c> (the Swiss schema restricts the ISO
    /// one and keeps its namespace). A Swiss file that has another one is no schema for the file (<see cref="LoadProblem"/>).
    /// </summary>
    public const string SwissNamespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";

    /// <summary>The prefix of the logical names of the embedded SEPA schema (EUR).</summary>
    public const string SepaResourcePrefix = "Casazen.Infrastructure.Payments.Schemas.Sepa.";

    /// <summary>The prefix of the logical names of the embedded Swiss schema (CHF).</summary>
    public const string SwissResourcePrefix = "Casazen.Infrastructure.Payments.Schemas.Swiss.";

    private static readonly Lazy<SchemaLoad> Sepa = new(() => Load(SepaResourcePrefix, SepaNamespace), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<SchemaLoad> Swiss = new(() => Load(SwissResourcePrefix, SwissNamespace), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the official schema of <paramref name="currency"/> (EUR: SEPA, CHF: Swiss) is embedded in this build and compiles.</summary>
    public static bool HasOfficialSchema(string currency) => Resolve(currency)?.Set is not null;

    /// <summary>
    /// The target namespace of the file of <paramref name="currency"/> (the one its schema has and the one the file is written in): 422
    /// <c>wire_currency_not_supported</c> for a currency that has no wire file.
    /// </summary>
    public static string NamespaceOf(string currency) => Normalize(currency) switch
    {
        WireTransferErrors.SepaCurrency => SepaNamespace,
        WireTransferErrors.SwissCurrency => SwissNamespace,
        _ => throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported"),
    };

    /// <summary>
    /// The compiled schemas of the file of <paramref name="currency"/>: 422 <c>wire_currency_not_supported</c> for a currency that has no wire file,
    /// 422 <c>wire_batch_schema_unavailable</c> when the official schema is not installed (or does not compile).
    /// </summary>
    public static XmlSchemaSet GetSchemas(string currency)
    {
        var load = Resolve(currency) ?? throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported");
        return load.Set ?? throw new DomainRuleException(WireTransferErrors.SchemaUnavailable, "WireBatchSchemaUnavailable", Normalize(currency));
    }

    /// <summary>
    /// Why the schema of <paramref name="currency"/> cannot be used although files are embedded (the compile error, which speaks of the schema and never of
    /// a document); <c>null</c> when it is usable or when no file is embedded at all.
    /// </summary>
    public static string? LoadProblem(string currency) => Resolve(currency)?.Problem;

    /// <summary>The logical names of the embedded schemas, both sets (for the check of the checksums of the runbook).</summary>
    public static IReadOnlyList<string> EmbeddedSchemaNames() =>
        typeof(Pain001SchemaProvider).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(SepaResourcePrefix, StringComparison.Ordinal) || n.StartsWith(SwissResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static SchemaLoad? Resolve(string currency) => Normalize(currency) switch
    {
        WireTransferErrors.SepaCurrency => Sepa.Value,
        WireTransferErrors.SwissCurrency => Swiss.Value,
        _ => null,
    };

    private static string Normalize(string? currency) => (currency ?? string.Empty).Trim().ToUpperInvariant();

    private static SchemaLoad Load(string prefix, string requiredNamespace)
    {
        var assembly = typeof(Pain001SchemaProvider).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        return Load(names, name => assembly.GetManifestResourceStream(name)!, requiredNamespace);
    }

    /// <summary>
    /// Compiles the schema files <paramref name="names"/> (read with <paramref name="open"/>) into one set that has the target namespace
    /// <paramref name="requiredNamespace"/> (the one the file is written in: without it the validation would only warn that it has no schema for the root
    /// element, and the file would pass). No file is no set and no reason; a file that does not compile, or files that are not the schema of that message, are
    /// no set and the reason of it.
    /// </summary>
    internal static SchemaLoad Load(IReadOnlyList<string> names, Func<string, Stream> open, string requiredNamespace)
    {
        if (names.Count == 0)
            return new SchemaLoad(null, null);

        // The embedded schemas are files of this build, never input: no DTD and no resolver, so nothing outside the assembly is ever read.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            var set = new XmlSchemaSet { XmlResolver = null };
            foreach (var name in names)
            {
                using var stream = open(name);
                using var reader = XmlReader.Create(stream, settings);
                set.Add(null, reader);
            }

            set.Compile();
            return set.Contains(requiredNamespace)
                ? new SchemaLoad(set, null)
                : new SchemaLoad(null, $"no schema has the target namespace {requiredNamespace}");
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaException)
        {
            return new SchemaLoad(null, ex.Message);
        }
    }

    /// <summary>The outcome of loading the schemas of a currency: the compiled set, or why there is none (<c>null</c> too when no file is embedded).</summary>
    internal sealed record SchemaLoad(XmlSchemaSet? Set, string? Problem);
}
