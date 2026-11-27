using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public enum CalendarAlertKind
{
    Conflict,
}

public enum CalendarAlertSeverity
{
    High,
}

public sealed record CalendarItemRef(CalendarItemType Type, Guid Id);

/// <summary>
/// Something on the calendar the host should look at. <see cref="From"/> and <see cref="To"/> are the nights it is about as on the
/// items (<c>To</c> is the first night it no longer is); the text is the client's, from <see cref="Kind"/>.
/// </summary>
/// <param name="Id">Stable: the same situation has the same id on every read (kind and items, never a counter).</param>
/// <param name="Refs">The items it is about: the two that overlap.</param>
public sealed record CalendarAlert(
    string Id,
    CalendarAlertKind Kind,
    CalendarAlertSeverity Severity,
    Guid PropertyId,
    string PropertyName,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CalendarItemRef> Refs);

public sealed record CalendarOccupant(Guid Id, CalendarItemType Type, Guid PropertyId, DateOnly From, DateOnly To);

public sealed record CalendarAlertProperty(Guid Id, string Name);

public sealed record CalendarAlertInput(
    DateOnly Today,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CalendarAlertProperty> Properties,
    IReadOnlyList<CalendarOccupant> Occupants);

/// <summary>
/// The alerts of the host calendar (SR-02a, decision SR-D15: worked out on the server, so the calendar and the Home never disagree).
/// Only <b>conflicts</b> so far: two stays or blocks of a property that share a night (the departure day is free for an arrival).
/// One alert for each pair, for the nights they share, when those nights reach today or later and the range asked for.
/// </summary>
public static class CalendarAlertRules
{
    public static IReadOnlyList<CalendarAlert> Compute(CalendarAlertInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var byProperty = input.Occupants
            .GroupBy(o => o.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CalendarOccupant>)group
                    .OrderBy(o => o.From).ThenBy(o => o.To).ThenBy(o => o.Type).ThenBy(o => o.Id).ToList());

        var alerts = new List<CalendarAlert>();
        foreach (var property in input.Properties)
            alerts.AddRange(Conflicts(property, byProperty.GetValueOrDefault(property.Id) ?? [], input));

        return Sort(alerts);
    }

    public static IReadOnlyList<CalendarAlert> Sort(IEnumerable<CalendarAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        return alerts.OrderBy(a => a.Severity).ThenBy(a => a.From).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
    }

    public static CalendarOccupant StayOccupant(Guid id, Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        return new CalendarOccupant(
            id, CalendarItemType.Booking, propertyId, DateOnly.FromDateTime(checkIn), DateOnly.FromDateTime(checkOut));
    }

    public static CalendarOccupant BlockOccupant(Guid id, Guid propertyId, CalendarBlockSource source, DateTime start, DateTime end)
    {
        return new CalendarOccupant(
            id,
            source == CalendarBlockSource.ICalImport ? CalendarItemType.Ical : CalendarItemType.Block,
            propertyId,
            DateOnly.FromDateTime(start),
            DateOnly.FromDateTime(end));
    }

    public static IReadOnlyList<CalendarAlert> ConflictsOfStay(
        DateOnly today,
        CalendarAlertProperty property,
        Guid stayId,
        DateOnly checkIn,
        DateOnly checkOut,
        IReadOnlyList<CalendarOccupant> occupants)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(occupants);

        return Compute(new CalendarAlertInput(today, checkIn, checkOut, [property], occupants))
            .Where(alert => alert.Refs.Any(item => item.Type == CalendarItemType.Booking && item.Id == stayId))
            .ToList();
    }

    private static IEnumerable<CalendarAlert> Conflicts(
        CalendarAlertProperty property,
        IReadOnlyList<CalendarOccupant> occupants,
        CalendarAlertInput input)
    {
        for (var i = 0; i < occupants.Count; i++)
        {
            var a = occupants[i];
            for (var j = i + 1; j < occupants.Count; j++)
            {
                var b = occupants[j];

                if (b.From >= a.To)
                    break;

                var from = b.From;
                var to = a.To < b.To ? a.To : b.To;
                if (to <= from || to <= input.Today || from > input.To || to <= input.From)
                    continue;

                var first = a.Id.CompareTo(b.Id) <= 0 ? a : b;
                var second = ReferenceEquals(first, a) ? b : a;
                yield return new CalendarAlert(
                    $"conflict-{first.Id:N}-{second.Id:N}",
                    CalendarAlertKind.Conflict,
                    CalendarAlertSeverity.High,
                    property.Id,
                    property.Name,
                    from,
                    to,
                    [new CalendarItemRef(first.Type, first.Id), new CalendarItemRef(second.Type, second.Id)]);
            }
        }
    }
}
