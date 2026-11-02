using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Leases;

/// <summary>
/// The ISTAT update of a rent worked out (LR-OPS-1c): the rent the lease has today, the variation of the FOI index the landlord typed
/// (<c>FoiPercent</c>), the share of it the contract recognizes (<c>SharePercent</c>), what that makes in percent of the rent
/// (<c>AppliedPercent</c> = FoiPercent × SharePercent / 100, exact), the increase in euros and the new rent, both rounded to the cent.
/// </summary>
public sealed record LeaseIstatCalculation(
    decimal CurrentRent, decimal FoiPercent, decimal SharePercent, decimal AppliedPercent, decimal Increase, decimal NewRent);

/// <summary>What the landlord can do about the ISTAT update of the rent today (<see cref="LeaseIstatRules.Facts"/>).</summary>
/// <param name="Available">
/// The lease is in force, not ended, has a rent, its period is not over and it is <b>not</b> under the cedolare secca (which suspends every
/// update for as long as the option lasts): a calculation and a letter make sense.
/// </param>
/// <param name="CurrentRent">The monthly rent of the lease today.</param>
/// <param name="DefaultSharePercent">The share the window suggests; the contract (or the agreement of the comune) states the real one.</param>
/// <param name="MaxSharePercent">The most a share can be: the whole variation.</param>
/// <param name="MinEffectiveOn">The first day the update can start from: today or the start of the lease, never in the past.</param>
/// <param name="MaxEffectiveOn">The last day of the period the lease is in.</param>
public sealed record LeaseIstatFacts(
    bool Available, decimal CurrentRent, decimal DefaultSharePercent, decimal MaxSharePercent, DateOnly MinEffectiveOn, DateOnly MaxEffectiveOn);

/// <summary>Why a pair of percentages cannot be calculated (<see cref="LeaseIstatRules.Check"/>).</summary>
public enum LeaseIstatProblem
{
    None = 0,

    /// <summary>A percentage is not a number between zero (excluded) and its maximum, or has more than two decimals.</summary>
    PercentInvalid = 1,
}

/// <summary>
/// The rules of the ISTAT update of a rent (LR-OPS-1c), pure. The landlord types the variation of the FOI index (CasaZen never calls an
/// external service for it) and the share of it the contract recognizes; the rent is the one of the lease. The update only ever increases the
/// rent: the law speaks of «variazioni in aumento», so a variation that is zero or negative leaves nothing to ask for. Under the cedolare
/// secca the update is suspended for as long as the option lasts (D.Lgs. 23/2011, art. 3): nothing is calculated then.
/// </summary>
/// <remarks>
/// The default share (75) is the one of the reference agreement and of the sample draft of the project, <b>to be confirmed per contract and
/// per agreement</b> (decision LR-D29); the contract states the real share and the landlord can type any between zero and the whole variation.
/// The figures are exposed by <c>GET api/leases/{id}/operations</c>: they are not written in the interface.
/// </remarks>
public static class LeaseIstatRules
{
    /// <summary>The share suggested in the window: 75 % of the variation (the reference agreement and the sample draft; to confirm per contract).</summary>
    public const decimal DefaultSharePercent = 75m;

    /// <summary>The most a share can be: the whole variation of the index.</summary>
    public const decimal MaxSharePercent = 100m;

    /// <summary>A sanity bound for the variation of the index typed by the landlord, a guard against a typo and not a figure of law.</summary>
    public const decimal MaxFoiPercent = 100m;

    /// <summary>The cedolare secca suspends the right to ask for any update, the ISTAT one included, for as long as the option lasts.</summary>
    public static bool UpdatesWaived(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.TaxRegime == LeaseTaxRegime.CedolareSecca;
    }

    /// <summary>Whether the update can be calculated for this lease today, with the figures and the days the window needs.</summary>
    public static LeaseIstatFacts Facts(LeaseContract lease, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var start = RomeCalendar.DateInRome(lease.StartDate);
        var periodEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        var available = LeaseRenewal.IsInForce(lease.Status)
                        && lease.TerminatedOn is null
                        && !UpdatesWaived(lease)
                        && lease.MonthlyRent > 0
                        && periodEnd >= today;

        // Not retroactive: an update starts from the day it is asked for, never from a day that has passed.
        var earliest = today > start ? today : start;
        return new LeaseIstatFacts(available, lease.MonthlyRent, DefaultSharePercent, MaxSharePercent, earliest, periodEnd);
    }

    /// <summary>Whether the two percentages typed can be calculated: both above zero, the share not above the whole variation, at most two decimals each.</summary>
    public static LeaseIstatProblem Check(decimal foiPercent, decimal sharePercent) =>
        IsPercent(foiPercent, MaxFoiPercent) && IsPercent(sharePercent, MaxSharePercent) ? LeaseIstatProblem.None : LeaseIstatProblem.PercentInvalid;

    private static bool IsPercent(decimal value, decimal max) => value > 0 && value <= max && decimal.Round(value, 2) == value;

    /// <summary>
    /// The new rent: the rent plus <c>rent × FoiPercent × SharePercent / 10000</c>, the increase rounded once to the cent (half away from
    /// zero), so that the new rent has the cents of a rent. The percentages are exact decimals: nothing is rounded before the end.
    /// </summary>
    public static LeaseIstatCalculation Calculate(decimal currentRent, decimal foiPercent, decimal sharePercent)
    {
        if (currentRent <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentRent), currentRent, "The rent must be above zero");
        if (Check(foiPercent, sharePercent) != LeaseIstatProblem.None)
            throw new ArgumentOutOfRangeException(nameof(foiPercent), foiPercent, "The percentages must be above zero, the share at most the whole variation");

        var applied = foiPercent * sharePercent / 100m;
        var increase = decimal.Round(currentRent * applied / 100m, 2, MidpointRounding.AwayFromZero);
        return new LeaseIstatCalculation(currentRent, foiPercent, sharePercent, applied, increase, currentRent + increase);
    }
}
