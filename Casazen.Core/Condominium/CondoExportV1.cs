using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Condominium;

/// <summary>
/// One consistent <c>condo-export-v1</c> snapshot (CondoLedger). Amounts in the file are integer cents.
/// This is a copy into staging, not a sync back.
/// </summary>
public static class CondoExportV1
{
    public const string Format = "condo-export-v1";

    public sealed record Payment(string SourceId, long AmountCents, DateOnly PaidOn);

    public sealed record Year(
        string SourceId,
        string Label,
        DateOnly Start,
        DateOnly End,
        long OrdinaryCents,
        long ExtraordinaryCents,
        long FinalCents,
        long PriorCents,
        long PaymentCents,
        long SettlementCents,
        int InstallmentCount,
        long InstallmentTotalCents,
        CondominiumInstallmentCadence? Cadence,
        IReadOnlyList<Payment> Payments);

    public sealed record House(
        string SourceId,
        string Name,
        string? Location,
        int FiscalStartMonth,
        IReadOnlyList<Year> Years);

    public sealed record Snapshot(string ContentHash, IReadOnlyList<House> Houses);

    public static Snapshot Parse(string json)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json) ?? throw new FormatException("empty");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The snapshot is not JSON.", ex);
        }

        if (root["format"]?.GetValue<string>() != Format)
            throw new FormatException("The snapshot is not condo-export-v1.");

        var hash = root["contentHash"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(hash))
            hash = Hash(root);
        else if (!string.Equals(hash, Hash(root), StringComparison.Ordinal))
            throw new FormatException("The snapshot hash does not match its contents.");

        var houses = new List<House>();
        foreach (var house in root["houses"]?.AsArray() ?? throw new FormatException("houses"))
        {
            if (house is null)
                continue;
            var sourceId = Required(house, "id");
            var name = Required(house, "name");
            var month = house["fiscalStartMonth"]?.GetValue<int>() ?? 0;
            if (!FiscalYear.IsValidStartMonth(month))
                throw new FormatException("fiscalStartMonth");

            var periods = Index(house["fiscalPeriods"]?.AsArray(), "id");
            var years = periods.Select(pair => YearOf(pair.Key, pair.Value, house)).ToList();
            houses.Add(new House(sourceId, name, house["location"]?.GetValue<string>(), month, years));
        }

        return new Snapshot(hash, houses);
    }

    public static string Hash(JsonNode root)
    {
        var copy = root.DeepClone();
        if (copy is JsonObject obj)
            obj.Remove("contentHash");
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(copy)))).ToLowerInvariant();
        return "sha256:" + hex;
    }

    private static Year YearOf(string id, JsonNode period, JsonNode house)
    {
        var start = DateOnly.Parse(Required(period, "startDate"));
        var end = DateOnly.Parse(Required(period, "endDate"));
        long ordinary = 0, extraordinary = 0, final = 0;
        foreach (var due in house["dues"]?.AsArray() ?? [])
        {
            if (due is null || due["fiscalPeriodId"]?.GetValue<string>() != id)
                continue;
            var amount = Cents(due["amountCents"]);
            var kind = due["kind"]?.GetValue<string>();
            if (kind == "final")
                final += amount;
            else if (due["voice"]?.GetValue<string>() == "extraordinary")
                extraordinary += amount;
            else
                ordinary += amount;
        }

        long prior = 0;
        foreach (var row in house["priorBalances"]?.AsArray() ?? [])
        {
            if (row is not null && row["fiscalPeriodId"]?.GetValue<string>() == id)
                prior += Cents(row["amountCents"]);
        }

        var payments = new List<Payment>();
        long paid = 0;
        foreach (var row in house["payments"]?.AsArray() ?? [])
        {
            if (row is null || row["fiscalPeriodId"]?.GetValue<string>() != id)
                continue;
            var amount = Cents(row["amountCents"]);
            paid += amount;
            var paidOn = DateOnly.TryParse(row["paidOn"]?.GetValue<string>(), out var day) ? day : start;
            payments.Add(new Payment(Required(row, "id"), amount, paidOn));
        }

        var installments = (house["installments"]?.AsArray() ?? []).Where(row => row?["fiscalPeriodId"]?.GetValue<string>() == id).ToList();
        var installmentTotal = installments.Sum(row => Cents(row?["totalCents"]));
        var settlement = Conguaglio.Compute(FromCents(final), FromCents(extraordinary), FromCents(prior), FromCents(paid));
        return new Year(
            id,
            Required(period, "label"),
            start,
            end,
            ordinary,
            extraordinary,
            final,
            prior,
            paid,
            settlement is null ? 0 : ToCents(settlement.Amount),
            installments.Count,
            installmentTotal,
            CadenceOf(installments.Count),
            payments);
    }

    public static CondominiumInstallmentCadence? CadenceOf(int count) => count switch
    {
        12 => CondominiumInstallmentCadence.Monthly,
        6 => CondominiumInstallmentCadence.Bimonthly,
        2 => CondominiumInstallmentCadence.Semiannual,
        1 => CondominiumInstallmentCadence.Single,
        _ => null,
    };

    public static decimal FromCents(long cents) => cents / 100m;

    public static long ToCents(decimal amount) => (long)CondominiumMoney.Round(amount * 100m);

    private static Dictionary<string, JsonNode> Index(JsonArray? rows, string idProperty)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var row in rows ?? [])
        {
            if (row is null)
                continue;
            map[Required(row, idProperty)] = row;
        }

        return map;
    }

    private static string Required(JsonNode node, string name)
    {
        var value = node[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException(name);
        return value;
    }

    private static long Cents(JsonNode? node)
    {
        if (node is null)
            return 0;
        if (node is JsonValue value && value.TryGetValue<long>(out var whole))
            return whole;
        if (node is JsonValue number && number.TryGetValue<decimal>(out var amount))
            return ToCents(amount);
        throw new FormatException("cents");
    }

    private static string Canonical(JsonNode? node)
    {
        if (node is null)
            return "null";
        if (node is JsonArray array)
            return "[" + string.Join(',', array.Select(Canonical)) + "]";
        if (node is JsonObject obj)
        {
            var pairs = obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value));
            return "{" + string.Join(',', pairs) + "}";
        }

        return node.ToJsonString();
    }
}
