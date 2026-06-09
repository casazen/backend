using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Shared tier catalogue and property limits. Values align with <see cref="EntitlementService"/>
/// defaults; <c>spec-saas-billing</c> will attach Stripe Price ids later.
/// </summary>
public static class PlanCatalog
{
    public sealed record Entry(
        PlanTier Tier,
        string DisplayName,
        int MaxProperties,
        string Description);

    private static readonly Entry[] Entries =
    [
        new(PlanTier.Starter, "Starter", 3, "Fino a 3 proprietà — ideale per iniziare."),
        new(PlanTier.Pro, "Pro", 50, "Fino a 50 proprietà — per operatori in crescita."),
        new(PlanTier.Scale, "Scale", int.MaxValue, "Proprietà illimitate — per agenzie e PM."),
    ];

    public static IReadOnlyList<Entry> All => Entries;

    public static int MaxPropertiesFor(PlanTier tier) =>
        Entries.First(e => e.Tier == tier).MaxProperties;

    public static bool TryParseTier(string? value, out PlanTier tier)
    {
        tier = default;
        if (!Enum.TryParse(value, ignoreCase: true, out PlanTier parsed))
            return false;

        if (!Entries.Any(e => e.Tier == parsed))
            return false;

        tier = parsed;
        return true;
    }
}
