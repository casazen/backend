using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBookingFlowSettingsService" />
/// <remarks>
/// The defaults are the behaviour CasaZen had before the settings existed, made automatic where the host used to do a step by hand (casazen/backend#978): the link
/// goes <c>CheckIn:SendWindowDays</c> days before the arrival, at most the life of a link (<c>CheckIn:SessionLifetimeDays</c>) so that the link is still open on the arrival
/// day. The reads ignore the tenant filter (the jobs run with no tenant) and always name the org.
/// </remarks>
public sealed class BookingFlowSettingsService(
    AppDbContext db,
    IOptions<GuestCheckInOptions> checkInOptions,
    TimeProvider clock,
    ILogger<BookingFlowSettingsService> logger,
    IActivityLog? activityLog = null) : IBookingFlowSettingsService
{
    public async Task<BookingFlowSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var row = await Rows([orgId]).FirstOrDefaultAsync(cancellationToken);
        return row is null ? Defaults() : ViewOf(row);
    }

    public async Task<IReadOnlyDictionary<Guid, BookingFlowSettingsView>> GetManyAsync(
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orgIds);
        var distinct = orgIds.Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<Guid, BookingFlowSettingsView>();

        var rows = await Rows(distinct).ToListAsync(cancellationToken);
        var defaults = Defaults();
        var views = distinct.ToDictionary(id => id, _ => defaults);
        foreach (var row in rows)
            views[row.OrgId] = ViewOf(row);
        return views;
    }

    public async Task<BookingFlowSettingsView> UpdateAsync(
        Guid orgId,
        BookingFlowSettingsUpdate update,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var row = await db.BookingFlowSettings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        var current = row is null ? Defaults() : ViewOf(row);

        var next = current with
        {
            CheckInLinkMode = update.CheckInLinkMode ?? current.CheckInLinkMode,
            CheckInLinkDaysBefore = update.CheckInLinkDaysBefore ?? current.CheckInLinkDaysBefore,
            ArrivalMode = update.ArrivalMode ?? current.ArrivalMode,
            DepartureMode = update.DepartureMode ?? current.DepartureMode,
            PropertyReadyMode = update.PropertyReadyMode ?? current.PropertyReadyMode,
            PropertyReadyTrigger = update.PropertyReadyTrigger ?? current.PropertyReadyTrigger,
            IsDefault = false,
        };

        // Bounds first: nothing is saved, nothing is logged, when a value is out of them.
        Validate(next);

        if (row is not null && next == current)
            return current;

        var now = clock.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new BookingFlowSettings { OrgId = orgId, CreatedAt = now };
            db.BookingFlowSettings.Add(row);
        }

        row.CheckInLinkMode = next.CheckInLinkMode;
        row.CheckInLinkDaysBefore = next.CheckInLinkDaysBefore;
        row.ArrivalMode = next.ArrivalMode;
        row.DepartureMode = next.DepartureMode;
        row.PropertyReadyMode = next.PropertyReadyMode;
        row.PropertyReadyTrigger = next.PropertyReadyTrigger;
        row.UpdatedByUserId = actorUserId is { Length: > 0 and <= BookingFlowSettings.UserIdMaxLength } ? actorUserId : null;
        row.UpdatedAt = now;
        activityLog?.Record(OrgActivity.Of(orgId, OrgActivityType.OrgSettingsChanged, actorUserId, orgId.ToString()));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Booking flow of org {OrgId} changed by {UserId}: link {LinkMode} {Days} days before, arrival {Arrival}, departure {Departure}, property ready {Ready} ({Trigger})",
            orgId,
            actorUserId ?? "none",
            row.CheckInLinkMode,
            row.CheckInLinkDaysBefore,
            row.ArrivalMode,
            row.DepartureMode,
            row.PropertyReadyMode,
            row.PropertyReadyTrigger);
        return ViewOf(row);
    }

    private IQueryable<BookingFlowSettings> Rows(IReadOnlyCollection<Guid> orgIds) =>
        db.BookingFlowSettings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => orgIds.Contains(s.OrgId));

    private int MaxDays => Math.Clamp(
        checkInOptions.Value.SessionLifetimeDays,
        GuestCheckInOptions.MinSessionLifetimeDays,
        GuestCheckInOptions.MaxSessionLifetimeDays);

    private BookingFlowSettingsView Defaults() => BookingFlowRules.Defaults(checkInOptions.Value.SendWindowDays, MaxDays);

    private BookingFlowSettingsView ViewOf(BookingFlowSettings row) => new(
        row.CheckInLinkMode,
        row.CheckInLinkDaysBefore,
        MaxDays,
        row.ArrivalMode,
        row.DepartureMode,
        row.PropertyReadyMode,
        row.PropertyReadyTrigger,
        IsDefault: false);

    private void Validate(BookingFlowSettingsView view)
    {
        if (view.CheckInLinkDaysBefore < 0 || view.CheckInLinkDaysBefore > MaxDays)
        {
            throw new DomainRuleException(BookingFlowErrorCodes.DaysOutOfRange, "BookingFlowDaysOutOfRange", MaxDays);
        }

        if (!Enum.IsDefined(view.CheckInLinkMode)
            || !Enum.IsDefined(view.ArrivalMode)
            || !Enum.IsDefined(view.DepartureMode)
            || !Enum.IsDefined(view.PropertyReadyMode)
            || !Enum.IsDefined(view.PropertyReadyTrigger))
        {
            throw new DomainRuleException(BookingFlowErrorCodes.ValueInvalid, "BookingFlowValueInvalid");
        }
    }
}
