namespace Casazen.Core.Services;

/// <summary>
/// Optional bank link through Stripe Financial Connections, on the same Stripe account as the rest of CasaZen.
/// Linking is not required: the manual bank transfer (IBAN, payment reference, offline declaration) stays.
/// A rent due is paid only when Stripe confirms the PaymentIntent, or when that offline declaration is recorded.
/// </summary>
public interface IBankLinkService
{
    Task<BankLinkSession> CreateOrgSessionAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<BankLinkAccount> AttachOrgAccountAsync(Guid orgId, string accountId, CancellationToken cancellationToken = default);

    Task<BankLinkAccount> GetOrgAccountAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<BankLinkPaymentStarted> StartOrgPaymentAsync(
        Guid orgId,
        StartBankLinkPayment request,
        BankLinkCaller caller,
        CancellationToken cancellationToken = default);

    Task<BankLinkSession> CreateLeaseSessionAsync(Guid installmentId, string token, CancellationToken cancellationToken = default);

    Task<BankLinkAccount> AttachLeaseAccountAsync(
        Guid installmentId, string token, string accountId, CancellationToken cancellationToken = default);

    Task<BankLinkPaymentStarted> StartLeasePaymentAsync(
        Guid installmentId, string token, BankLinkCaller caller, CancellationToken cancellationToken = default);

    Task<RentPaymentNotice?> ApplyStripeEventAsync(BankLinkStripeEvent paymentEvent, CancellationToken cancellationToken = default);
}

public sealed record BankLinkSession(string SessionId, string ClientSecret);

public sealed record BankLinkAccount(
    bool Linked,
    string? AccountId,
    string? InstitutionName,
    string? Last4,
    string? DisplayName);

/// <summary>
/// A transfer the landlord starts. An installment is optional; its amount and currency are the due's.
/// Without an installment, <see cref="IdempotencyKey"/> is required so a retry cannot start a second debit.
/// </summary>
public sealed record StartBankLinkPayment(
    long? AmountCents,
    string? Currency,
    Guid? LeaseId,
    Guid? InstallmentId,
    string? IdempotencyKey,
    Guid? CondominiumId = null,
    Guid? CondominiumInstallmentId = null);

public sealed record BankLinkCaller(string? IpAddress, string? UserAgent);

public sealed record BankLinkPaymentStarted(
    Guid PaymentId,
    string PaymentIntentId,
    string? ClientSecret,
    string StripeStatus,
    string PaymentMethodType,
    bool DueSettled);

public sealed record BankLinkStripeEvent(
    string PaymentIntentId,
    string EventType,
    long AmountCents,
    string? FailureCode);

public static class BankLinkErrorCodes
{
    public const string AccountIdInvalid = "bank_link_account_invalid";
    public const string NotLinked = "bank_link_not_linked";
    public const string AccountHolderMismatch = "bank_link_account_holder_mismatch";
    public const string DebitUnavailable = "bank_link_debit_unavailable";
    public const string AmountInvalid = "bank_link_amount_invalid";
    public const string CurrencyInvalid = "bank_link_currency_invalid";
    public const string InstallmentMismatch = "bank_link_installment_mismatch";
    public const string IdempotencyKeyInvalid = "bank_link_idempotency_key_invalid";
}

public static class BankLinkCharges
{
    public const string Kind = "bank-link-payment";

    public const string SepaDebit = "sepa_debit";

    public const string Currency = "eur";

    public const string InstitutionCountry = "IT";

    /// <summary>
    /// API version that accepts <c>filters[country]</c> (one country). Stripe.net 50 pins
    /// <c>2025-12-15.clover</c>, whose session create only has <c>filters[countries]</c> and rejects
    /// <c>filters[country]</c>. The bank-link session sends this version with that header.
    /// </summary>
    public const string SessionApiVersion = "2026-10-28.endive";
}
