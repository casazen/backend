using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Validation;

namespace Casazen.Core.Services;

/// <summary>
/// Shared tier catalogue and property limits. Values align with <see cref="EntitlementService"/>
/// defaults; <c>spec-saas-billing</c> will attach Stripe Price ids later.
/// </summary>
public static class PlanCatalog
{
    /// <param name="Tier">The plan.</param>
    /// <param name="DisplayName">Name shown to the customer.</param>
    /// <param name="MaxProperties">Properties of the org (<see cref="int.MaxValue"/> = unlimited).</param>
    /// <param name="MaxSeats">
    /// People of the org: active members plus pending invitations (AM-02, decision D13; <see cref="int.MaxValue"/> =
    /// unlimited). The limit in force can be overridden per tier with <c>Entitlement:Tiers:{Tier}:MaxSeats</c>
    /// (<see cref="IEntitlementService.ResolveMaxSeats"/>).
    /// </param>
    /// <param name="Description">One-line description of the plan.</param>
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
        new(PlanTier.Scale, "Scale", int.MaxValue, int.MaxValue, "Proprietà illimitate — per agenzie e PM."),
    ];

    public static IReadOnlyList<Entry> All => Entries;

    public static int MaxPropertiesFor(PlanTier tier) =>
        Entries.First(e => e.Tier == tier).MaxProperties;

    /// <summary>Default number of people a tier allows: Starter 2, Pro 10, Scale unlimited (<see cref="int.MaxValue"/>).</summary>
    public static int MaxSeatsFor(PlanTier tier) =>
        Entries.First(e => e.Tier == tier).MaxSeats;

    /// <summary>Position of the tier in the catalogue, lowest first: moving to a higher rank is an upgrade.</summary>
    public static int Rank(PlanTier tier) => Array.FindIndex(Entries, e => e.Tier == tier);

    public static bool TryParseTier(string? value, out PlanTier tier)
    {
        tier = default;
        // Names only (PL-07): Enum.TryParse would also take "1" or "Starter,Pro".
        if (!EnumNames.TryParseDefined(value, out PlanTier parsed))
            return false;

        if (!Entries.Any(e => e.Tier == parsed))
            return false;

        tier = parsed;
        return true;
    }
}
