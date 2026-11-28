using System.Xml;
using System.Xml.Schema;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The official schemas of the wire files, loaded once from the resources embedded in this assembly (E06/F05/US01/T01, E16/F01/US01/T01, E16/F01/US02/T01, NF-25,
/// NF-113): the ISO 20022 <c>pain.001.001.09</c> schema of the customer credit transfer, from the ISO 20022 message catalogue, for the SEPA file (EUR,
/// <c>Payments/Schemas/Iso</c>) and the schema of the Swiss Payment Standards of the Swiss file, <c>pain.001.001.09.ch.03.xsd</c> (CHF, <c>Payments/Schemas/Swiss</c>),
/// each one in its own cached <see cref="XmlSchemaSet"/>. The two messages are written in the same target namespace (<see cref="IsoNamespace"/>, <see cref="SwissNamespace"/>:
/// the Swiss schema restricts the ISO one and keeps its namespace) and are told apart by their schema, so the namespace of a file never says which schema it was
/// validated against: the currency does. The files are the ones their publishers give, with their source and SHA-256 in <c>docs/runbooks/wire-transfers.md</c>: a
/// schema is never written or rebuilt here.
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
    public const string IsoNamespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";

    public const string SwissNamespace = IsoNamespace;

    public const string IsoResourcePrefix = "Casazen.Infrastructure.Payments.Schemas.Iso.";

    public const string SwissResourcePrefix = "Casazen.Infrastructure.Payments.Schemas.Swiss.";

    private static readonly Lazy<SchemaLoad> Iso = new(() => Load(IsoResourcePrefix, IsoNamespace), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<SchemaLoad> Swiss = new(() => Load(SwissResourcePrefix, SwissNamespace), LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool HasOfficialSchema(string currency) => Resolve(currency)?.Set is not null;

    public static string NamespaceOf(string currency) => Normalize(currency) switch
    {
        WireTransferErrors.SepaCurrency => IsoNamespace,
        WireTransferErrors.SwissCurrency => SwissNamespace,
        _ => throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported"),
    };

    public static XmlSchemaSet GetSchemas(string currency)
    {
        var load = Resolve(currency) ?? throw new DomainRuleException(WireTransferErrors.CurrencyNotSupported, "WireCurrencyNotSupported");
        return load.Set ?? throw new DomainRuleException(WireTransferErrors.SchemaUnavailable, "WireBatchSchemaUnavailable", Normalize(currency));
    }

    public static string? LoadProblem(string currency) => Resolve(currency)?.Problem;

    public static IReadOnlyList<string> EmbeddedSchemaNames() =>
        typeof(Pain001SchemaProvider).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(IsoResourcePrefix, StringComparison.Ordinal) || n.StartsWith(SwissResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static SchemaLoad? Resolve(string currency) => Normalize(currency) switch
    {
        WireTransferErrors.SepaCurrency => Iso.Value,
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

    internal static SchemaLoad Load(IReadOnlyList<string> names, Func<string, Stream> open, string requiredNamespace)
    {
        if (names.Count == 0)
            return new SchemaLoad(null, null);

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

    internal sealed record SchemaLoad(XmlSchemaSet? Set, string? Problem);
}
