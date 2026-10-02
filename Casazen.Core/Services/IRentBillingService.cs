using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

/// <summary>The landlord's choices for the rent schedule of a lease (LT-06).</summary>
/// <param name="Amount">Installment amount; null = monthly rent of the lease times the months of the cadence.</param>
/// <param name="BillingDayOfMonth">Due day (1-28); null = the day of the lease start (at most 28).</param>
public sealed record ConfigureRentScheduleRequest(RentCadence Cadence, int? BillingDayOfMonth, decimal? Amount);

/// <summary>An installment declared paid by the landlord outside CasaZen (bank transfer, cash…).</summary>
public sealed record MarkRentPaidOfflineRequest(DateOnly PaidOn, string? Note);

/// <summary>One installment as the landlord sees it. <c>IsOverdue</c>: not paid and past its due date (Europe/Rome).</summary>
public sealed record RentInstallmentView(
    Guid Id,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate,
    decimal Amount,
    string Currency,
    RentLedgerStatus Status,
    bool IsOverdue,
    RentPaymentChannel? PaidVia,
    DateOnly? PaidOn,
    string? OfflinePaymentNote,
    DateTime? PaymentRequestedAt,
    string? FailureCode,
    DateTime? LastFailedAt);

/// <summary>The rent schedule of a lease, null while the landlord has not set it up.</summary>
public sealed record RentScheduleView(
    RentCadence Cadence,
    int BillingDayOfMonth,
    decimal Amount,
    string Currency,
    bool IsActive);

/// <summary>
/// Rent page of a lease (LT-06): the schedule, its installments and what the landlord can do.
/// <c>CanConfigure</c>: the lease is signed (rent is due under a signed contract). <c>OnlinePaymentsAvailable</c>: the
/// org's Stripe connected account accepts charges, so the tenant can be asked to pay online; otherwise only offline
/// payments can be recorded. <c>PartialFinalPeriod</c>: the end of the lease falls inside a period, which gets no
/// installment (no pro rata rule is assumed).
/// </summary>
public sealed record RentLedgerView(
    Guid LeaseId,
    decimal MonthlyRent,
    bool CanConfigure,
    bool OnlinePaymentsAvailable,
    bool HasTenantEmail,
    RentScheduleView? Schedule,
    IReadOnlyList<RentInstallmentView> Installments,
    RentPeriod? PartialFinalPeriod);

/// <summary>Where an installment stands for the tenant on the public payment page.</summary>
public enum PublicRentPaymentState
{
    /// <summary>Can be paid online now.</summary>
    Payable,

    /// <summary>The payment is in flight on Stripe: nothing to do.</summary>
    Processing,

    Paid,

    /// <summary>Cancelled, or the landlord no longer accepts online payments: the tenant contacts the landlord.</summary>
    Unavailable,
}

/// <summary>The installment shown to the tenant from the payment link: no personal data.</summary>
public sealed record PublicRentPayment(
    Guid InstallmentId,
    string PropertyName,
    string LandlordName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate,
    decimal Amount,
    string Currency,
    PublicRentPaymentState State,
    bool LastPaymentFailed);

/// <summary>The PaymentIntent the tenant confirms with the Stripe Payment Element, on the landlord's connected account.</summary>
public sealed record PublicRentPaymentSession(
    Guid InstallmentId,
    string ClientSecret,
    string PublishableKey,
    string StripeAccountId);

/// <summary>A Stripe event of a rent PaymentIntent (<c>metadata.kind = rent-charge</c>), without Stripe types.</summary>
/// <param name="EventType"><c>payment_intent.succeeded</c>, <c>.processing</c>, <c>.payment_failed</c> or <c>.canceled</c>.</param>
/// <param name="AccountId">Connected account of the event (null for an event of the platform account).</param>
/// <param name="InstallmentId">From <c>metadata.rentLedgerEntryId</c>.</param>
/// <param name="FailureCode">Code of <c>last_payment_error</c> (e.g. <c>card_declined</c>).</param>
public sealed record RentPaymentIntentEvent(
    string PaymentIntentId,
    string EventType,
    string? AccountId,
    Guid? InstallmentId,
    long AmountCents,
    string? FailureCode);

/// <summary>Emails due once a change of an installment is committed (<see cref="IRentBillingService.CompleteAsync"/>).</summary>
public enum RentPaymentNoticeKind
{
    /// <summary>Paid online: the landlords are told.</summary>
    Received,

    /// <summary>An online payment failed: the tenants get a new link to pay again.</summary>
    Failed,
}

public sealed record RentPaymentNotice(Guid InstallmentId, RentPaymentNoticeKind Kind);

/// <summary>What one run of the rent collection job did.</summary>
public sealed record RentCollectionRun(int RequestsSent, int Synchronized, int Errors);

/// <summary>
/// Recurring rent of a long-term lease (LT-06, #269, audit A7-07): schedule generated from the lease, payment request to
/// the tenant with a link, online payment on the landlord's Stripe connected account (state only from Stripe), offline
/// payments declared by the landlord. Authorization of the lease is the caller's (controller); the tenant query filter
/// scopes every read of the authenticated methods.
/// </summary>
public interface IRentBillingService
{
    Task<RentLedgerView> GetLedgerAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<RentLedgerView> ConfigureScheduleAsync(
        Guid leaseId, ConfigureRentScheduleRequest request, CancellationToken cancellationToken = default);

    Task<RentLedgerView> DisableScheduleAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<RentInstallmentView> MarkPaidOfflineAsync(
        Guid leaseId,
        Guid installmentId,
        MarkRentPaidOfflineRequest request,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>Emails the payment link of the installment to the tenants now (a new link: the previous one stops working).</summary>
    Task<RentInstallmentView> SendPaymentRequestAsync(Guid leaseId, Guid installmentId, CancellationToken cancellationToken = default);

    /// <summary>Anonymous: the installment of a payment link; 404 <c>rent_payment_link_invalid</c> for a wrong id or token.</summary>
    Task<PublicRentPayment> GetPublicPaymentAsync(Guid installmentId, string token, CancellationToken cancellationToken = default);

    /// <summary>Anonymous: the PaymentIntent to confirm (created, or the current one reused: never two payable at once).</summary>
    Task<PublicRentPaymentSession> CreatePaymentSessionAsync(
        Guid installmentId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a rent PaymentIntent event inside the webhook event transaction. Pass the notice to
    /// <see cref="CompleteAsync"/> once the event is committed.
    /// </summary>
    Task<RentPaymentNotice?> ApplyPaymentIntentEventAsync(RentPaymentIntentEvent paymentEvent, CancellationToken cancellationToken = default);

    /// <summary>Queues the emails of a committed change; a failure is logged and never undoes the change.</summary>
    Task CompleteAsync(RentPaymentNotice notice, CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily <c>rent-collection</c> job: payment requests of the installments coming due, and installments in flight
    /// read again from Stripe (lost webhook).
    /// </summary>
    Task<RentCollectionRun> RunCollectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes of the rent endpoints (LT-06).</summary>
public static class RentBillingErrorCodes
{
    public const string LeaseNotSigned = "rent_lease_not_signed";
    public const string AmountInvalid = "rent_amount_invalid";
    public const string BillingDayInvalid = "rent_billing_day_invalid";
    public const string CadenceLocked = "rent_cadence_locked";
    public const string ScheduleNotFound = "rent_schedule_not_found";
    public const string InstallmentNotFound = "rent_installment_not_found";
    public const string InstallmentNotPayable = "rent_installment_not_payable";
    public const string InstallmentInFlight = "rent_installment_in_flight";
    public const string OnlinePaymentsUnavailable = "rent_online_payments_unavailable";
    public const string NoTenantEmail = "rent_no_tenant_email";
    public const string PaidOnInvalid = "rent_paid_on_invalid";
    public const string PaymentLinkInvalid = "rent_payment_link_invalid";
    public const string LeaseOutsidePlan = "rent_lease_dates_invalid";
}
