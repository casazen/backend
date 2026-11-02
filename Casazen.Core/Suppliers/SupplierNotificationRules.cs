using Casazen.Core.Entities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The notices to a supplier that its settings switch on and off (SP-16d, <c>PUT api/supplier/settings</c> of SP-16a): the three switches
/// of the card «Notifiche» of the console. They govern the <b>informative</b> notices only; the operational ones (a cancellation, the answer
/// to a proposed time or to a quote, an expiry, a payment that failed or was refunded, a reminder from the host) always arrive, since the
/// supplier has to act on them.
/// </summary>
public enum SupplierNotificationKind
{
    /// <summary>A new request to answer (e-mail and push), whether it came from a host or from the public showcase.</summary>
    NewRequest,

    /// <summary>The reminder of the job of tomorrow, at 18:00 of the day before (e-mail and push).</summary>
    DayBeforeReminder,

    /// <summary>The payment of a job arrived: the customer marked it as paid, or it was paid through CasaZen (e-mail and push).</summary>
    PaymentReceived,
}

/// <summary>The rule of the three switches, as a pure function: what a supplier that has not saved its settings gets, and what a row says.</summary>
public static class SupplierNotificationRules
{
    /// <summary>
    /// True when the supplier wants <paramref name="kind"/>. A supplier has no <see cref="SupplierSettings"/> row until it saves a rule or
    /// its working hours (the row is lazy): without one the real defaults hold, which are all <b>on</b>, exactly what the console shows
    /// and what the columns say.
    /// </summary>
    public static bool Allows(SupplierSettings? settings, SupplierNotificationKind kind) =>
        settings is null
        || kind switch
        {
            SupplierNotificationKind.NewRequest => settings.NotifyNewRequests,
            SupplierNotificationKind.DayBeforeReminder => settings.NotifyDayBeforeReminder,
            SupplierNotificationKind.PaymentReceived => settings.NotifyPaymentMarked,
            _ => true,
        };
}
