namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the guest of a booking the host entered is to pay it (<see cref="Booking.HostPaymentArrangement"/>, the payment step of the new-booking
/// wizard, NF-74, E08/F01/US01/T05). Stored as its number and append-only. Only the payment link sends something to the guest: the other
/// arrangements record what was agreed and leave the balance to collect with "Registra incasso".
/// </summary>
public enum HostPaymentArrangement
{
    /// <summary>Nothing agreed in CasaZen: the booking shows its balance like any other.</summary>
    None = 0,

    /// <summary>The guest received a secure link to pay the balance online, on the host's Stripe account.</summary>
    PaymentLink = 1,

    /// <summary>The guest pays at the property: the host records the income by hand.</summary>
    OnSite = 2,

    /// <summary>The guest pays by bank transfer: the host records the income by hand.</summary>
    BankTransfer = 3,
}
