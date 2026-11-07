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

    /// <summary>The tenant of the installment's lease (the payment-link token is the access check).</summary>
    Task<BankLinkSession> CreateLeaseSessionAsync(Guid installmentId, string token, CancellationToken cancellationToken = default);

    Task<BankLinkAccount> AttachLeaseAccountAsync(
        Guid installmentId, string token, string accountId, CancellationToken cancellationToken = default);

    Task<BankLinkPaymentStarted> StartLeasePaymentAsync(
        Guid installmentId, string token, BankLinkCaller caller, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a <c>bank-link-payment</c> PaymentIntent event inside the webhook transaction. A linked rent installment
    /// is paid only for <c>payment_intent.succeeded</c>.
    /// </summary>
    Task<RentPaymentNotice?> ApplyStripeEventAsync(BankLinkStripeEvent paymentEvent, CancellationToken cancellationToken = default);
}

/// <summary>What the browser needs to open Financial Connections. No bank number.</summary>
public sealed record BankLinkSession(string SessionId, string ClientSecret);

/// <summary>The linked account as CasaZen stores and shows it: the Stripe id, and display fields read from Stripe.</summary>
public sealed record BankLinkAccount(
    bool Linked,
    string? AccountId,
    string? InstitutionName,
    string? Last4,
    string? DisplayName);

/// <summary>A transfer the landlord starts. An installment is optional; its amount and currency are the due's.</summary>
public sealed record StartBankLinkPayment(long? AmountCents, string? Currency, Guid? LeaseId, Guid? InstallmentId, string? IdempotencyKey);

/// <summary>The caller address Stripe stores on an online mandate. Not a bank detail.</summary>
public sealed record BankLinkCaller(string? IpAddress, string? UserAgent);

/// <summary>
/// A debit that was started. <see cref="DueSettled"/> is false here: the due is settled by the webhook (or the offline
/// declaration), never by the act of starting the PaymentIntent.
/// </summary>
public sealed record BankLinkPaymentStarted(
    Guid PaymentId,
    string PaymentIntentId,
    string? ClientSecret,
    string StripeStatus,
    string PaymentMethodType,
    bool DueSettled);

/// <summary>A Stripe event of a bank-link PaymentIntent, without Stripe types.</summary>
public sealed record BankLinkStripeEvent(
    string PaymentIntentId,
    string EventType,
    long AmountCents,
    string? FailureCode);

/// <summary>Stable error codes of the bank-link endpoints.</summary>
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

/// <summary><c>metadata.kind</c> of a PaymentIntent debited from a linked Financial Connections account.</summary>
public static class BankLinkCharges
{
    public const string Kind = "bank-link-payment";

    /// <summary>SEPA Direct Debit. The euro method used for a linked Italian or EU account. Not <c>us_bank_account</c>.</summary>
    public const string SepaDebit = "sepa_debit";

    public const string Currency = "eur";

    /// <summary>Institutions offered in the link flow: Italy, so the list is Intesa and the other Italian banks, not US banks.</summary>
    public const string InstitutionCountry = "IT";
}
