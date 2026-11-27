using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Hourly job of the booking flow (casazen/backend#978): registers the arrivals and the departures the host no longer has to register by hand, for the orgs whose
/// settings say so (<see cref="IBookingFlowSettingsService"/>, every step automatic by default).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Arrival</b>: a confirmed stay whose arrival day (Europe/Rome) is over and that the host did not mark a no-show. The host has the whole arrival day to
/// register it or to mark the no-show; the arrival is never assumed before the day ends.</item>
/// <item><b>Departure</b>: a checked-in stay (or a confirmed one when the arrival is automatic too: both are registered together) once the check-out time of its
/// property has passed on the departure day (the property's own clock; 10:00 when the property says none). A stay whose check-out wizard the host opened is the
/// host's to close. The property ready for the next guest then follows the flow (at the check-out, or when the cleaning is done).</item>
/// <item><b>Only recent stays</b>: arrival and departure days within <see cref="BookingFlowRules.AutomationLookbackDays"/> days. Switching the automation on, or a job
/// that was down, never closes a backlog of old stays at once: those stay with the host, as before.</item>
/// <item>Every step goes through <see cref="IStayLifecycleService"/>, the one path of the host's actions (the lock of the booking, the rules, the events, the Alloggiati
/// scheduling), recorded as CasaZen's and told to the hosts live. Nothing is sent to the police: the Alloggiati Web communication stays the host's.</item>
/// <item>One stay's failure never stops the others.</item>
/// </list>
/// </remarks>
public sealed class BookingFlowAutomationJob(
    AppDbContext db,
    IBookingFlowSettingsService flowSettings,
    IStayLifecycleService stayLifecycle,
    ILogger<BookingFlowAutomationJob> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>The id of the recurring job.</summary>
    public const string RecurringJobId = "booking-flow-automation";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = _clock.TodayInRome();
        var oldest = today.AddDays(-BookingFlowRules.AutomationLookbackDays);

        var stays = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.NoShowAt == null && b.CheckoutWizardStartedAt == null)
            .Where(b =>
                (b.Status == BookingStatus.Confirmed && b.CheckInDate >= oldest && b.CheckInDate < today) ||
                ((b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn) && b.CheckOutDate >= oldest && b.CheckOutDate <= today))
            .Select(b => new Stay(b.Id, b.OrgId, b.Status, b.CheckInDate, b.CheckOutDate, b.Property.CheckOutBy, b.Property.Timezone))
            .ToListAsync();
        if (stays.Count == 0)
            return;

        var flows = await flowSettings.GetManyAsync(stays.Select(s => s.OrgId).ToList());
        int arrivals = 0, departures = 0;
        foreach (var stay in stays)
        {
            var flow = flows[stay.OrgId];
            try
            {
                switch (StepOf(stay, flow, today, now))
                {
                    case BookingFlowAutomaticStep.Departure:
                        if (await stayLifecycle.CheckOutAutomaticallyAsync(stay.Id, registerArrival: stay.Status == BookingStatus.Confirmed) is not null)
                            departures++;
                        break;
                    case BookingFlowAutomaticStep.Arrival:
                        if (await stayLifecycle.RegisterArrivalAutomaticallyAsync(stay.Id) is not null)
                            arrivals++;
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The booking flow could not move booking {BookingId} of org {OrgId}", stay.Id, stay.OrgId);
            }
        }

        if (arrivals + departures > 0)
            logger.LogInformation("Booking flow: {Arrivals} arrivals and {Departures} departures registered automatically", arrivals, departures);
    }

    /// <summary>What the flow does for <paramref name="stay"/> now: the departure first (it registers the arrival with it), the arrival once its day is over, else nothing.</summary>
    public static BookingFlowAutomaticStep? StepOf(Stay stay, BookingFlowSettingsView flow, DateTime todayInRome, DateTime nowUtc)
    {
        var departureDue = stay.CheckOutDate.Date <= todayInRome.Date && CheckOutInstantUtc(stay) <= nowUtc;
        if (flow.RegistersDeparture && departureDue && (stay.Status == BookingStatus.CheckedIn || flow.RegistersArrival))
            return BookingFlowAutomaticStep.Departure;

        if (flow.RegistersArrival && stay.Status == BookingStatus.Confirmed && stay.CheckInDate.Date < todayInRome.Date)
            return BookingFlowAutomaticStep.Arrival;

        return null;
    }

    /// <summary>The check-out time of the property on the departure day, on the property's clock (Europe/Rome when it has none), in UTC.</summary>
    public static DateTime CheckOutInstantUtc(Stay stay) =>
        RomeCalendar.ToUtc(
            DateOnly.FromDateTime(stay.CheckOutDate),
            stay.CheckOutBy ?? BookingFlowRules.FallbackCheckOutTime,
            RomeCalendar.ZoneOf(stay.Timezone));

    /// <summary>What the job reads of a stay.</summary>
    public sealed record Stay(
        Guid Id,
        Guid OrgId,
        BookingStatus Status,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        TimeOnly? CheckOutBy,
        string? Timezone);
}
