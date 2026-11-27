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
    NewRequest,

    DayBeforeReminder,

    PaymentReceived,
}

public static class SupplierNotificationRules
{
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
