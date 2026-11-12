using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// One list of everything collected for a booking and for a period (FIS-05, NF-85, ADR-033-R2, E06/F04/US01/T03): the card payments with the commission,
/// the net and the credit date the provider reported, the payments the channels collected (from Octorate) and the manual payments, each paid or to collect
/// and linked to the fiscal document of its booking. A read model: it writes nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Scope in SQL.</b> The rows are read for the org of the scope and narrowed to the properties it reaches in the query itself: a caller who reaches
/// some properties never gets the lines of the others.</item>
/// <item><b>Never a guess.</b> A figure that is not stored (the fee of a payment not captured yet, the commission of a channel Octorate does not give) is
/// absent from the line; a booking whose OTA amounts are all missing gets no fabricated OTA line.</item>
/// <item><b>Currency.</b> Every amount is in the currency of the property (<see cref="StayCurrency"/>); a provider fee keeps the currency of the balance.</item>
/// </list>
/// </remarks>
public interface IBookingPaymentLedgerService
{
    /// <summary>
    /// The lines of one booking; <c>null</c> when the booking is not one the scope reaches. With an Octorate reservation the individual collections are read
    /// from Octorate (while the connector is on); when that read fails the stored OTA amounts are answered with <see cref="PaymentLedgerBooking.Degraded"/>.
    /// </summary>
    Task<PaymentLedgerBooking?> GetForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default);

    /// <summary>One page of the lines of a period (newest first), with the criteria and the limits of the payment list.</summary>
    Task<PaymentLedgerPage> ListAsync(HostScope scope, PaymentListCriteria criteria, int page, int pageSize, CancellationToken ct = default);
}

/// <summary>What a line is.</summary>
public enum PaymentLedgerKind
{
    Card = 1,
    BankTransfer = 2,
    Cash = 3,
    Ota = 4,
    Other = 5,
}

/// <summary>Whether the money of a line came in.</summary>
public enum PaymentLedgerState
{
    Paid = 1,
    ToCollect = 2,
    Refunded = 3,
    PartiallyRefunded = 4,
    Failed = 5,
    Canceled = 6,
}

/// <summary>Who collected the money of a line.</summary>
public enum PaymentLedgerCollector
{
    Unknown = 0,
    Host = 1,
    Channel = 2,
    Platform = 3,
}

/// <summary>The fiscal document of the booking a line belongs to.</summary>
public sealed record PaymentLedgerDocument(Guid Id, string? Number, string Kind, string Status, string PaymentState);

/// <summary>
/// One line of the ledger. <paramref name="Date"/> is the Europe/Rome day of the settlement (else of the creation; for a line of a channel, the day of the
/// collection Octorate gave, else the check-in). <paramref name="ProviderFee"/>, <paramref name="Net"/> and <paramref name="CreditDate"/> only when stored.
/// </summary>
public sealed record PaymentLedgerLine(
    PaymentLedgerKind Kind,
    Guid BookingId,
    string BookingCode,
    Guid? PaymentId,
    DateOnly Date,
    decimal Gross,
    decimal? ProviderFee,
    decimal? Net,
    string? ProviderCurrency,
    DateOnly? CreditDate,
    string Currency,
    PaymentLedgerState State,
    PaymentLedgerCollector CollectedBy,
    PaymentLedgerDocument? Document);

/// <summary>The ledger of one booking: its lines, its channel and whether the collections of Octorate could not be read.</summary>
public sealed record PaymentLedgerBooking(
    Guid BookingId,
    string BookingCode,
    string Channel,
    string Currency,
    IReadOnlyList<PaymentLedgerLine> Lines,
    bool Degraded);

/// <summary>One page of the lines of a period.</summary>
public sealed record PaymentLedgerPage(IReadOnlyList<PaymentLedgerLine> Items, int TotalCount, int Page, int PageSize)
{
    /// <summary>The most rows of each source a period reads (payments, bookings of a channel); a longer period is narrowed by the caller.</summary>
    public const int MaxRowsPerSource = 5_000;
}
