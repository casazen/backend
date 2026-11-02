using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>What the host changed of the catalog cancellation policy of a property: the columns of <see cref="Property"/>, together.</summary>
/// <param name="FullRefundHours">Hours before check-in of the full refund; <c>null</c> = the catalog's.</param>
/// <param name="PartialRefundHours">Hours before check-in of the partial refund; <c>null</c> = the catalog's.</param>
/// <param name="PartialRefundPercent">Share refunded in the partial window; <c>null</c> = the catalog's.</param>
/// <param name="RefundType">«Non rimborsabile»: the host never refunds a stay shorter than <see cref="CancellationRefundPolicy.LongStayNightsUnchanged"/> nights.</param>
public readonly record struct HostCancellationOverrides(
    int? FullRefundHours,
    int? PartialRefundHours,
    decimal? PartialRefundPercent,
    HostCancellationRefundType RefundType)
{
    public static HostCancellationOverrides Of(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new HostCancellationOverrides(
            property.CancellationFullRefundHours,
            property.CancellationPartialRefundHours,
            property.CancellationPartialRefundPercent,
            property.CancellationRefundType);
    }
}

/// <summary>
/// The cancellation policy of a property as the public site reads it (DB-03b): what a normal stay (under <see cref="LongStayNights"/> nights)
/// gets, that is the catalog policy with what the host changed of it. A window the policy does not have is <c>null</c>, never a number that
/// means «never» (the 10-year window of «Non rimborsabile» is not shown).
/// </summary>
/// <param name="Slug">Stable identifier of the catalog policy.</param>
/// <param name="Name">Name of the policy (not translated).</param>
/// <param name="Description">
/// The catalog text; <c>null</c> when the host changed the windows (<paramref name="HostOverrideApplies"/>), since it would then say
/// something else.
/// </param>
/// <param name="FullRefundHours">Hours before the start of the check-in day up to which the refund is full; <c>null</c> = never in full.</param>
/// <param name="PartialRefundHours">Hours before the start of the check-in day up to which the partial refund holds; <c>null</c> = there is none.</param>
/// <param name="PartialRefundPercent">The share refunded in the partial window; <c>null</c> = there is none.</param>
/// <param name="HostOverrideApplies">The host changed the windows or the percentage of the catalog policy for stays under <paramref name="LongStayNights"/> nights.</param>
/// <param name="LongStayNights">From this many nights the catalog policy applies as it is, whatever the host changed.</param>
public sealed record PublicCancellationPolicy(
    string Slug,
    string Name,
    string? Description,
    int? FullRefundHours,
    int? PartialRefundHours,
    decimal? PartialRefundPercent,
    bool HostOverrideApplies,
    int LongStayNights);

/// <summary>
/// What cancelling costs for one stay, in Europe/Rome calendar dates that are still ahead (DB-03b): the last day of the full refund and, when
/// the policy has one, of the partial refund, worked out on the policy of that stay (<see cref="CancellationRefundPolicy.EffectivePolicy(Property?, int)"/>)
/// with the same reading as the guest area (<see cref="CancellationRefundPolicy.RefundWindows"/>). A date already past is <c>null</c>: a page
/// never says «gratuita fino a ieri».
/// </summary>
/// <param name="FullRefundUntil">Last day with the full refund, included.</param>
/// <param name="PartialRefundPercent">The share of the partial window; <c>null</c> when it is over or the policy has none.</param>
/// <param name="PartialRefundUntil">Last day with the partial refund, included.</param>
/// <param name="SelfServiceAvailable">
/// The guest can cancel by themselves from the guest area (DB-08a), so the dates above are a promise the system keeps. True for every
/// direct booking since DB-08a; a page shows the dates only when it is true.
/// </param>
public sealed record StayCancellation(
    DateOnly? FullRefundUntil,
    decimal? PartialRefundPercent,
    DateOnly? PartialRefundUntil,
    bool SelfServiceAvailable);

/// <summary>Builds the two public views of a cancellation policy from the one rule (<see cref="CancellationRefundPolicy"/>).</summary>
public static class PublicCancellation
{
    /// <summary>The policy for the public page of a property; <c>null</c> when the property has none.</summary>
    public static PublicCancellationPolicy? PolicyOf(CancellationPolicy? catalog, HostCancellationOverrides overrides)
    {
        if (catalog is null)
            return null;

        var effective = CancellationRefundPolicy.EffectivePolicy(catalog, overrides, nights: 1)!;
        var overridden = !CancellationRefundPolicy.IsCatalogPolicy(catalog, effective);

        int? full = effective.FullRefundHours < CancellationRefundPolicy.NeverFullRefundHours ? effective.FullRefundHours : null;
        var percent = Math.Clamp(effective.PartialRefundPercent, 0m, 100m);

        // A partial window that ends before the full one starts to count is not a window of its own (a policy written upside down).
        var hasPartial = percent > 0m && (full is not { } fullHours || effective.PartialRefundHours < fullHours);

        return new PublicCancellationPolicy(
            catalog.Slug,
            catalog.Name,
            overridden ? null : catalog.Description,
            full,
            hasPartial ? effective.PartialRefundHours : null,
            hasPartial ? percent : null,
            overridden,
            CancellationRefundPolicy.LongStayNightsUnchanged);
    }

    /// <summary>The dates of a stay that checks in on <paramref name="checkInDate"/> under <paramref name="effectivePolicy"/>, from <paramref name="today"/> on.</summary>
    public static StayCancellation StayOf(CancellationPolicy? effectivePolicy, DateTime checkInDate, DateOnly today)
    {
        var windows = CancellationRefundPolicy.RefundWindows(effectivePolicy, checkInDate);
        DateOnly? full = windows.FullRefundUntil is { } fullUntil && fullUntil >= today ? fullUntil : null;
        var partialAhead = windows.PartialRefundUntil is { } partialUntil && partialUntil >= today;

        return new StayCancellation(
            full,
            partialAhead ? windows.PartialRefundPercent : null,
            partialAhead ? windows.PartialRefundUntil : null,
            SelfServiceAvailable: true);
    }
}
