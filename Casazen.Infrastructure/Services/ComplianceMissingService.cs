using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed class ComplianceMissingService(
    AppDbContext db,
    IPropertyComplianceStatusService complianceStatus,
    IStayGuestService stayGuests) : IComplianceMissingService
{
    private const string AlloggiatiField = "alloggiati";

    public async Task<ComplianceSummaryResult> DescribeAsync(
        ComplianceSummaryResult summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var properties = await DescribePropertiesAsync(summary.PropertiesPending.Items, cancellationToken);
        var stays = await DescribeStaysAsync(summary.GuestCheckInsIncomplete.Items, cancellationToken);

        return summary with
        {
            PropertiesPending = Describe(
                summary.PropertiesPending,
                item => properties.GetValueOrDefault(item.Id) ?? [new ComplianceMissing(ComplianceMissingCodes.ActivationIncomplete)]),
            GuestCheckInsIncomplete = Describe(
                summary.GuestCheckInsIncomplete,
                item => stays.GetValueOrDefault(item.Id) ?? [new ComplianceMissing(ComplianceMissingCodes.GuestDataIncomplete)]),
            CheckoutsDue = Describe(
                summary.CheckoutsDue,
                _ => [new ComplianceMissing(ComplianceMissingCodes.CheckoutNotClosed, "checkOut")]),
            AlloggiatiFailures = Describe(
                summary.AlloggiatiFailures,
                _ => [new ComplianceMissing(ComplianceMissingCodes.AlloggiatiFailed, AlloggiatiField)]),
            AlloggiatiManualRequired = Describe(
                summary.AlloggiatiManualRequired,
                _ => [new ComplianceMissing(ComplianceMissingCodes.AlloggiatiNotSent, AlloggiatiField)]),
            TurnoversPending = Describe(
                summary.TurnoversPending,
                _ => [new ComplianceMissing(ComplianceMissingCodes.PropertyReadyNotConfirmed, "propertyReady")]),
        };
    }

    private static ComplianceSummarySection Describe(
        ComplianceSummarySection section,
        Func<ComplianceSummaryItem, IReadOnlyList<ComplianceMissing>> missing) =>
        new(section.Count, section.Items.Select(item => item with { Missing = missing(item) }).ToList());

    private async Task<Dictionary<Guid, IReadOnlyList<ComplianceMissing>>> DescribePropertiesAsync(
        IReadOnlyList<ComplianceSummaryItem> items,
        CancellationToken cancellationToken)
    {
        var described = new Dictionary<Guid, IReadOnlyList<ComplianceMissing>>();
        var ids = items.Select(item => item.Id).Take(IComplianceMissingService.MaxDetailedProperties).ToList();
        if (ids.Count == 0)
            return described;

        var properties = await db.Properties.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        foreach (var property in properties)
        {
            var steps = await complianceStatus.GetBlockingStepsAsync(property, cancellationToken);
            var blockers = steps
                .Where(step => step.Blocker)
                .SelectMany(step => step.Blockers.Select(blocker => new ComplianceMissing(blocker.Code, step.Id)))
                .ToList();

            described[property.Id] = blockers.Count > 0
                ? blockers
                : [new ComplianceMissing(ComplianceMissingCodes.ActivationNotConfirmed)];
        }

        return described;
    }

    // The fields of the Alloggiati record that the guests of each stay lack, all the stays in one read.
    private async Task<Dictionary<Guid, IReadOnlyList<ComplianceMissing>>> DescribeStaysAsync(
        IReadOnlyList<ComplianceSummaryItem> items,
        CancellationToken cancellationToken)
    {
        var described = new Dictionary<Guid, IReadOnlyList<ComplianceMissing>>();
        var ids = items.Select(item => item.Id).ToList();
        if (ids.Count == 0)
            return described;

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => ids.Contains(b.Id))
            .ToListAsync(cancellationToken);
        var guestsOfBooking = await stayGuests.GetForBookingsAsync(bookings, cancellationToken);

        foreach (var booking in bookings)
        {
            var guests = guestsOfBooking[booking.Id];
            var missing = new List<ComplianceMissing>();

            if (AlloggiatiRecordRules.CompositionErrors(guests.Select(g => g.Type).ToList()).Count > 0)
                missing.Add(new ComplianceMissing(ComplianceMissingCodes.GuestCompositionInvalid, "guests"));

            missing.AddRange(guests
                .SelectMany(guest => AlloggiatiRecordRules.MissingFields(guest))
                .GroupBy(field => field)
                .Select(field => new ComplianceMissing(ComplianceMissingCodes.GuestFieldMissing, field.Key, field.Count())));

            if (missing.Count > 0)
                described[booking.Id] = missing;
        }

        return described;
    }
}
