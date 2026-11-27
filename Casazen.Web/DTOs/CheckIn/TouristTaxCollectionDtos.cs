using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;

namespace Casazen.Web.DTOs.CheckIn;

public sealed class TouristTaxGuestDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxCollectionStatus Status { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxNotPayableReason Reason { get; init; }

    public decimal Payable { get; init; }

    public decimal? AmountDue { get; init; }

    public decimal CollectedAmount { get; init; }

    public string Currency { get; init; } = string.Empty;

    public IReadOnlyList<TouristTaxGuestLineDto> Guests { get; init; } = [];

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

public sealed class TouristTaxGuestLineDto
{
    public int Position { get; init; }
    public int TaxableNights { get; init; }

    public decimal? Amount { get; init; }

    public bool Exempt { get; init; }
}

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

public sealed class TouristTaxCollectionDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxCollectionStatus Status { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TouristTaxNotPayableReason Reason { get; init; }

    public decimal ToCollect { get; init; }

    public decimal OnlinePayable { get; init; }

    public decimal RefundDue { get; init; }

    public decimal? AmountDue { get; init; }

    public decimal IncludedInBooking { get; init; }

    public decimal CollectedAmount { get; init; }

    public decimal RefundedAmount { get; init; }

    public string Currency { get; init; } = string.Empty;

    public DateTime? CollectedAt { get; init; }

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

public sealed class TouristTaxSettingsDto
{
    public bool Enabled { get; set; }

    /// <summary>Only with <see cref="Enabled"/>: saved as false otherwise.</summary>
    public bool RequiredBeforeAccessCode { get; set; }

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
