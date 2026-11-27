using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Validation;

namespace Casazen.Core.Services;

public static class PlanCatalog
{
    public sealed record Entry(
        PlanTier Tier,
        string DisplayName,
        int MaxProperties,
        int MaxSeats,
        string Description);

    private static readonly Entry[] Entries =
    [
        new(PlanTier.Starter, "Starter", 3, 2, "Fino a 3 proprietà — ideale per iniziare."),
        new(PlanTier.Pro, "Pro", 50, 10, "Fino a 50 proprietà — per operatori in crescita."),
        new(PlanTier.Scale, "Scale", int.MaxValue, int.MaxValue, "Proprietà illimitate — per operatori in crescita."),
        new(PlanTier.Agency, "Agency", int.MaxValue, int.MaxValue, "Per property manager: fatturazione per camera."),
    ];

    public static IReadOnlyList<Entry> All => Entries;

    public static int MaxPropertiesFor(PlanTier tier) =>
        Entries.First(e => e.Tier == tier).MaxProperties;

    public static int MaxSeatsFor(PlanTier tier) =>
        Entries.First(e => e.Tier == tier).MaxSeats;

    public static int Rank(PlanTier tier) => Array.FindIndex(Entries, e => e.Tier == tier);

    public static bool TryParseTier(string? value, out PlanTier tier)
    {
        tier = default;
        if (!EnumNames.TryParseDefined(value, out PlanTier parsed))
            return false;

        if (!Entries.Any(e => e.Tier == parsed))
            return false;

        tier = parsed;
        return true;
    }
}
