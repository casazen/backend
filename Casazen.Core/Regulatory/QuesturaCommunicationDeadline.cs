using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Communication to the local public-security authority ("Questura") for a lease with an extra-EU tenant (LT-07, A7-08).
/// Single source of the rule, verified by RS-5 (<c>.claude/context/regulations/fiscale.md</c> L13-L14, Polizia di Stato,
/// class U): whoever lets a property to a foreign citizen or stateless person must communicate it in writing within
/// 48 hours of the delivery of the property (art. 7 D.Lgs. 286/1998). The RLI registration does not replace it.
/// </summary>
/// <remarks>
/// <para>Delivery date: the one declared by the landlord (<see cref="LeaseContract.PropertyDeliveryDate"/>), otherwise the
/// lease start date (documented default: the property is usually delivered on the first day of the lease).</para>
/// <para>CasaZen knows the delivery day, not the hour: the 48 hours end at the latest on the second calendar day after
/// the delivery day (Europe/Rome), shown as the deadline date. From the day after that date the communication is
/// overdue.</para>
/// <para>The item is ticked only by the landlord's explicit declaration (<see cref="LeaseContract.QuesturaCommunicationDate"/>),
/// never by a CasaZen reminder.</para>
/// </remarks>
public static class QuesturaCommunicationDeadline
{
    /// <summary>Hours from the delivery of the property (art. 7 D.Lgs. 286/1998, fiscale.md L13).</summary>
    public const int HoursFromDelivery = 48;

    /// <summary>Calendar days from the delivery day to the day the 48 hours end at the latest.</summary>
    public const int DaysFromDelivery = HoursFromDelivery / 24;

    /// <summary>
    /// The communication applies: at least one tenant is extra-EU and the lease is not rejected (a contract that will not
    /// take effect).
    /// </summary>
    public static bool IsRequired(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.HasExtraEUTenant && lease.Status != LeaseStatus.Rejected;
    }

    /// <summary>
    /// Delivery date of the property as midnight UTC of its Europe/Rome calendar date: the declared one, or the lease start
    /// date when none was declared.
    /// </summary>
    public static DateTime DeliveryDate(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ToStoredDate(RomeCalendar.DateInRome(lease.PropertyDeliveryDate ?? lease.StartDate));
    }

    /// <summary>The day the 48 hours from <paramref name="deliveryDate"/> end at the latest (midnight UTC of the Rome date).</summary>
    public static DateTime Deadline(DateTime deliveryDate) =>
        ToStoredDate(RomeCalendar.DateInRome(deliveryDate).AddDays(DaysFromDelivery));

    /// <inheritdoc cref="Deadline(DateTime)"/>
    public static DateTime Deadline(LeaseContract lease) => Deadline(DeliveryDate(lease));

    /// <summary>Calendar days from <paramref name="todayInRome"/> to <paramref name="date"/>: 0 on that day, negative once passed.</summary>
    public static int DaysUntil(DateTime date, DateTime todayInRome) =>
        RomeCalendar.DateInRome(date).DayNumber - RomeCalendar.DateInRome(todayInRome).DayNumber;

    /// <summary>The Europe/Rome calendar date of <paramref name="value"/>, as midnight UTC (storage convention).</summary>
    public static DateTime ToStoredDate(DateTime value) => ToStoredDate(RomeCalendar.DateInRome(value));

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
