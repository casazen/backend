using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

/// <inheritdoc cref="IGuestPortalOverviewService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Sections</b>: every registered <see cref="IGuestPortalSection"/> is asked, one after the other, in order; one that is not offered
/// (<c>null</c>) is left out, one that throws is logged with the id of the link and its key and left out, never a 500. A key registered twice keeps
/// the first.</item>
/// <item><b>Next step</b>: the registered <see cref="IGuestPortalNextStep"/> are asked by priority, lowest first; the first step that opens a section of
/// the overview wins. One that throws is logged and skipped.</item>
/// <item><b>Features</b>: the paid services need every <see cref="IGuestPortalPolicy"/> to agree (one that throws counts as a «no»); the services of
/// the suppliers need them and <c>Features:GuestPortalSupplierServices</c>; the automatic replies <c>Features:GuestPortalAutoReplies</c>.</item>
/// <item><b>Reserved data</b>: the summary carries the name and the town of the house, never its address; nothing reserved is read here.</item>
/// </list>
/// </remarks>
public sealed class GuestPortalOverviewService(
    AppDbContext db,
    IEnumerable<IGuestPortalSection> sections,
    IEnumerable<IGuestPortalNextStep> nextSteps,
    IEnumerable<IGuestPortalPolicy> policies,
    IFeatureFlags featureFlags,
    ILogger<GuestPortalOverviewService> logger,
    IConfiguration? configuration = null) : IGuestPortalOverviewService
{
    public async Task<GuestPortalOverview?> GetAsync(PortalContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var facts = await (
                from b in db.Bookings.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                join p in db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]) on b.PropertyId equals p.Id
                join o in db.Orgs on b.OrgId equals o.Id
                where b.Id == context.BookingId && b.OrgId == context.OrgId && p.OrgId == context.OrgId
                select new { b.BookingCode, b.NumberOfGuests, PropertyName = p.Name, p.City, Org = o })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (facts is null)
            return null;

        var stay = new GuestPortalStay(
            facts.PropertyName,
            facts.City,
            IsoDay(context.CheckInDate),
            IsoDay(context.CheckOutDate),
            Math.Max(0, (context.CheckOutDate.Date - context.CheckInDate.Date).Days),
            facts.NumberOfGuests,
            StatusWord(context.Status),
            BookingCodes.Format(facts.BookingCode),
            context.IsOta,
            context.IsDirect);

        var views = await SectionsAsync(context, cancellationToken);
        var nextStep = await NextStepAsync(context, views, cancellationToken);
        var paidServices = await CanShowPaidServicesAsync(context, cancellationToken);
        var features = new GuestPortalFeatures(
            paidServices,
            paidServices && featureFlags.IsEnabled(FeatureFlags.GuestPortalSupplierServices)
                && configuration?.GetValue<bool>(GuestSupplierOrders.SimulationSetting) == true,
            featureFlags.IsEnabled(FeatureFlags.GuestPortalAutoReplies));

        return new GuestPortalOverview(
            stay,
            GuestPortalBranding.From(facts.Org),
            context.Language,
            views,
            nextStep,
            context.CanSeeReserved,
            context.CheckInCompleted,
            features);
    }

    /// <summary>A day of the stay as <c>yyyy-MM-dd</c>: the date of the stored day, never a time.</summary>
    internal static string IsoDay(DateTime day) =>
        DateOnly.FromDateTime(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The word of the status of the stay: <c>confirmed</c>, <c>checkedIn</c>, <c>checkedOut</c>, <c>cancelled</c>.</summary>
    internal static string StatusWord(BookingStatus status) => status switch
    {
        BookingStatus.Confirmed => "confirmed",
        BookingStatus.CheckedIn => "checkedIn",
        BookingStatus.CheckedOut => "checkedOut",
        BookingStatus.Cancelled => "cancelled",
        _ => "pending",
    };

    private async Task<List<GuestPortalSectionView>> SectionsAsync(PortalContext context, CancellationToken cancellationToken)
    {
        var views = new List<GuestPortalSectionView>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections.OrderBy(s => s.Order).ThenBy(s => s.Key, StringComparer.Ordinal))
        {
            GuestPortalSectionStatus? status;
            try
            {
                status = await section.GetStateAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Guest portal section {SectionKey} failed for link {LinkId}: left out of the overview", section.Key, context.LinkId);
                continue;
            }

            if (status is null)
                continue;

            if (!keys.Add(section.Key))
            {
                logger.LogWarning("Guest portal section {SectionKey} is registered twice: {SectionType} is left out", section.Key, section.GetType().Name);
                continue;
            }

            views.Add(new GuestPortalSectionView(section.Key, section.Order, GuestPortalSectionStates.WireName(status.State), status.Badge));
        }

        return views;
    }

    private async Task<GuestPortalStep?> NextStepAsync(
        PortalContext context,
        IReadOnlyCollection<GuestPortalSectionView> views,
        CancellationToken cancellationToken)
    {
        foreach (var proposer in nextSteps.OrderBy(p => p.Priority))
        {
            GuestPortalStep? step;
            try
            {
                step = await proposer.GetAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Guest portal next step {Proposer} failed for link {LinkId}: skipped", proposer.GetType().Name, context.LinkId);
                continue;
            }

            // A step always opens a section the guest sees.
            if (step is not null && views.Any(v => string.Equals(v.Key, step.Section, StringComparison.Ordinal)))
                return step;
        }

        return null;
    }

    private async Task<bool> CanShowPaidServicesAsync(PortalContext context, CancellationToken cancellationToken)
    {
        foreach (var policy in policies)
        {
            try
            {
                if (!await policy.CanShowPaidServicesAsync(context, cancellationToken))
                    return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Guest portal policy {Policy} failed for link {LinkId}: the paid services are hidden", policy.GetType().Name, context.LinkId);
                return false;
            }
        }

        return true;
    }
}
