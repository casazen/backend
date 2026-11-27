using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// The tourist tax of the stay as the guest page shows it (<c>GET api/public/checkin/{token}/tourist-tax</c>, E25/F01/US01/T01): the amount, the currency, the
/// tax guest by guest by position (no name, no reason of an exemption) and the state. <see cref="Payable"/> 0 with <see cref="Reason"/> <c>NotComputable</c>:
/// the page says the host will tell the amount.
/// </summary>
public sealed class TouristTaxGuestDto
{
    /// <summary><c>ToCollect</c>, <c>PaidOnline</c>, <c>PaidAtProperty</c>, <c>CollectedByChannel</c>, <c>IncludedInBookingPayment</c> or <c>NotDue</c>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxCollectionStatus Status { get; init; }

    /// <summary>Why nothing can be paid online now: <c>None</c> when <see cref="Payable"/> is above 0.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxNotPayableReason Reason { get; init; }

    /// <summary>What the guest can pay online now, in <see cref="Currency"/>.</summary>
    public decimal Payable { get; init; }

    /// <summary>The tax of the stay the host collects; <c>null</c> when it cannot be calculated.</summary>
    public decimal? AmountDue { get; init; }

    /// <summary>What the guest already paid online.</summary>
    public decimal CollectedAmount { get; init; }

    /// <summary>The currency of the property (<c>EUR</c> or <c>CHF</c>).</summary>
    public string Currency { get; init; } = string.Empty;

    public IReadOnlyList<TouristTaxGuestLineDto> Guests { get; init; } = [];

    /// <summary>The organization asks for the tax before the lock code: the step of the page cannot be skipped.</summary>
    public bool RequiredBeforeAccessCode { get; init; }

    public static TouristTaxGuestDto From(TouristTaxGuestView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new TouristTaxGuestDto
        {
            Status = view.Status,
            Reason = view.Reason,
            Payable = view.Payable,
            AmountDue = view.AmountDue,
            CollectedAmount = view.CollectedAmount,
            Currency = view.Currency,
            Guests = view.Guests.Select(g => new TouristTaxGuestLineDto
            {
                Position = g.Position,
                TaxableNights = g.TaxableNights,
                Amount = g.Amount,
                Exempt = g.Exempt,
            }).ToList(),
            RequiredBeforeAccessCode = view.RequiredBeforeAccessCode,
        };
    }
}

/// <summary>The tax of one guest: a position of the stay, never a person; exempt or not, never why.</summary>
public sealed class TouristTaxGuestLineDto
{
    public int Position { get; init; }
    public int TaxableNights { get; init; }

    /// <summary>Null when the tax of the stay is not calculated.</summary>
    public decimal? Amount { get; init; }

    public bool Exempt { get; init; }
}

/// <summary>
/// What the Payment Element of the guest page needs (<c>POST api/public/checkin/{token}/tourist-tax/payment-intent</c>): the client secret of the intent on
/// the organization's connected account, the platform's publishable key and the account (the Element is opened with <c>stripeAccount</c>).
/// </summary>
public sealed class TouristTaxPaymentIntentDto
{
    public string PaymentIntentId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string PublishableKey { get; init; } = string.Empty;
    public string StripeAccountId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;

    public static TouristTaxPaymentIntentDto From(TouristTaxPaymentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new TouristTaxPaymentIntentDto
        {
            PaymentIntentId = session.PaymentIntentId,
            ClientSecret = session.ClientSecret,
            PublishableKey = session.PublishableKey,
            StripeAccountId = session.StripeAccountId,
            Amount = session.Amount,
            Currency = session.Currency,
        };
    }
}

/// <summary>
/// The collection block of the host's view of the tax (<c>collection</c> of <c>GET api/bookings/{id}/tourist-tax</c>, the answer of
/// <c>POST …/tourist-tax/paid-at-property</c>): what is still to collect, what the guest paid online, what is owed back.
/// </summary>
public sealed class TouristTaxCollectionDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxCollectionStatus Status { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxNotPayableReason Reason { get; init; }

    /// <summary>What is still to collect for the stay, whoever collects it.</summary>
    public decimal ToCollect { get; init; }

    /// <summary>What the guest can pay online now.</summary>
    public decimal OnlinePayable { get; init; }

    /// <summary>What is owed back to the guest: the host gives it back (a «To do» says so), never an automatic refund for an active stay.</summary>
    public decimal RefundDue { get; init; }

    /// <summary>The tax the host collects for the stay as it is now; <c>null</c> when it cannot be calculated.</summary>
    public decimal? AmountDue { get; init; }

    /// <summary>The tax the price of the booking included.</summary>
    public decimal IncludedInBooking { get; init; }

    /// <summary>What the guest paid online.</summary>
    public decimal CollectedAmount { get; init; }

    /// <summary>What was refunded of it.</summary>
    public decimal RefundedAmount { get; init; }

    public string Currency { get; init; } = string.Empty;

    /// <summary>When it was paid online or recorded as paid at the property.</summary>
    public DateTime? CollectedAt { get; init; }

    /// <summary>The organization offers the online payment and its account takes payments.</summary>
    public bool OnlinePaymentAvailable { get; init; }

    public static TouristTaxCollectionDto From(TouristTaxCollectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new TouristTaxCollectionDto
        {
            Status = view.Status,
            Reason = view.Reason,
            ToCollect = view.ToCollect,
            OnlinePayable = view.OnlinePayable,
            RefundDue = view.RefundDue,
            AmountDue = view.AmountDue,
            IncludedInBooking = view.IncludedInBooking,
            CollectedAmount = view.CollectedAmount,
            RefundedAmount = view.RefundedAmount,
            Currency = view.Currency,
            CollectedAt = view.CollectedAt,
            OnlinePaymentAvailable = view.OnlinePaymentAvailable,
        };
    }
}

/// <summary>
/// The setting <c>TouristTaxOnlinePayment</c> of the organization (<c>GET/PUT api/orgs/me/tourist-tax-settings</c>): whether its guests pay the tax online in
/// the check-in and whether the page asks for it before the lock code. <see cref="PaymentsReady"/> is read-only: the connected account takes payments.
/// </summary>
public sealed class TouristTaxSettingsDto
{
    public bool Enabled { get; set; }

    /// <summary>Only with <see cref="Enabled"/>: saved as false otherwise.</summary>
    public bool RequiredBeforeAccessCode { get; set; }

    /// <summary>Read-only: the organization's connected account takes payments (ignored in a PUT).</summary>
    public bool PaymentsReady { get; set; }

    public static TouristTaxSettingsDto From(TouristTaxSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new TouristTaxSettingsDto
        {
            Enabled = view.Enabled,
            RequiredBeforeAccessCode = view.RequiredBeforeAccessCode,
            PaymentsReady = view.PaymentsReady,
        };
    }
}
