using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Links a Financial Connections account for the landlord (the org) or for the tenant of one lease, and starts the
/// debit Stripe can create from that account. Nothing here removes the IBAN, the payment reference or the offline
/// declaration, and nothing here marks a due paid.
/// </summary>
public sealed class BankLinkService(
    AppDbContext db,
    IStripeBillingService billing,
    IStripeBankLinkGateway gateway,
    IStripeService stripe,
    IRentBillingService rent,
    ILogger<BankLinkService> logger) : IBankLinkService
{
    private const long MaxAmountCents = 100_000_000;
    private static readonly HashSet<string> Cancelable = new(StringComparer.Ordinal)
    {
        "requires_payment_method",
        "requires_confirmation",
        "requires_action",
    };

    public async Task<BankLinkSession> CreateOrgSessionAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await LoadOrgAsync(orgId, cancellationToken);
        org.StripeCustomerId = await billing.EnsureCustomerAsync(org, cancellationToken);
        org.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await gateway.CreateSessionAsync(org.StripeCustomerId, cancellationToken);
    }

    public async Task<BankLinkAccount> AttachOrgAccountAsync(Guid orgId, string accountId, CancellationToken cancellationToken = default)
    {
        var org = await LoadOrgAsync(orgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(org.StripeCustomerId))
            throw new DomainRuleException(BankLinkErrorCodes.NotLinked, "BankLinkNotLinked");

        var account = await RequireHolderAsync(accountId, org.StripeCustomerId, cancellationToken);
        org.FinancialConnectionsAccountId = account.Id;
        org.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Org {OrgId} linked Financial Connections account {AccountId}", org.Id, account.Id);
        return ToAccount(account);
    }

    public async Task<BankLinkAccount> GetOrgAccountAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await LoadOrgAsync(orgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(org.FinancialConnectionsAccountId))
            return new BankLinkAccount(false, null, null, null, null);

        try
        {
            var account = await gateway.GetAccountAsync(org.FinancialConnectionsAccountId, cancellationToken);
            return ToAccount(account);
        }
        catch (Exception ex) when (ex is DomainRuleException or PaymentProcessingException)
        {
            logger.LogWarning(ex, "Linked account of org {OrgId} could not be read from Stripe", org.Id);
            return new BankLinkAccount(true, org.FinancialConnectionsAccountId, null, null, null);
        }
    }

    public async Task<BankLinkPaymentStarted> StartOrgPaymentAsync(
        Guid orgId, StartBankLinkPayment request, BankLinkCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = await LoadOrgAsync(orgId, cancellationToken);
        var (leaseId, installment, amount, currency) = await ResolveAmountAsync(org.Id, request, cancellationToken);
        return await StartAsync(
            org.Id,
            leaseId,
            installment,
            org.StripeCustomerId,
            org.FinancialConnectionsAccountId,
            org.DisplayName,
            BankLinkPayer.Landlord,
            amount,
            currency,
            IdempotencyKey(request.IdempotencyKey, installment),
            caller,
            cancellationToken);
    }

    public async Task<BankLinkSession> CreateLeaseSessionAsync(
        Guid installmentId, string token, CancellationToken cancellationToken = default)
    {
        var entry = await LoadByTokenAsync(installmentId, token, cancellationToken);
        var lease = entry.LeaseContract;
        lease.StripeCustomerId = await gateway.EnsureCustomerAsync(
            lease.StripeCustomerId,
            $"casazen-lease-bank-{lease.Id:N}",
            new Dictionary<string, string> { ["orgId"] = lease.OrgId.ToString(), ["leaseId"] = lease.Id.ToString() },
            "Conduttore",
            cancellationToken);
        lease.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await gateway.CreateSessionAsync(lease.StripeCustomerId, cancellationToken);
    }

    public async Task<BankLinkAccount> AttachLeaseAccountAsync(
        Guid installmentId, string token, string accountId, CancellationToken cancellationToken = default)
    {
        var entry = await LoadByTokenAsync(installmentId, token, cancellationToken);
        var lease = entry.LeaseContract;
        if (string.IsNullOrWhiteSpace(lease.StripeCustomerId))
            throw new DomainRuleException(BankLinkErrorCodes.NotLinked, "BankLinkNotLinked");

        var account = await RequireHolderAsync(accountId, lease.StripeCustomerId, cancellationToken);
        lease.FinancialConnectionsAccountId = account.Id;
        lease.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Lease {LeaseId} linked Financial Connections account {AccountId}",
            lease.Id,
            account.Id);
        return ToAccount(account);
    }

    public async Task<BankLinkPaymentStarted> StartLeasePaymentAsync(
        Guid installmentId, string token, BankLinkCaller caller, CancellationToken cancellationToken = default)
    {
        var entry = await LoadByTokenAsync(installmentId, token, cancellationToken);
        EnsureOpen(entry);
        var lease = entry.LeaseContract;
        return await StartAsync(
            entry.OrgId,
            lease.Id,
            entry,
            lease.StripeCustomerId,
            lease.FinancialConnectionsAccountId,
            "Conduttore",
            BankLinkPayer.Tenant,
            RentCharges.ToCents(entry.AmountDue),
            RentCharges.Currency,
            $"bank-link:{entry.Id:N}:{entry.PaymentIntentCount + 1}",
            caller,
            cancellationToken);
    }

    public async Task<RentPaymentNotice?> ApplyStripeEventAsync(
        BankLinkStripeEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        var row = await db.BankLinkPayments
            .SingleOrDefaultAsync(p => p.StripePaymentIntentId == paymentEvent.PaymentIntentId, cancellationToken);
        if (row is null)
        {
            logger.LogWarning("No bank-link payment for {PaymentIntentId}: ignored", paymentEvent.PaymentIntentId);
            return null;
        }

        if (row.Status != BankLinkPaymentStatus.Succeeded && StatusOf(paymentEvent.EventType) is { } status)
        {
            row.Status = status;
            row.UpdatedAt = UtcNow;
        }

        RentPaymentNotice? notice = null;
        if (row.RentLedgerEntryId is Guid installmentId)
        {
            notice = await rent.ApplyLinkedBankPaymentAsync(
                new RentPaymentIntentEvent(
                    paymentEvent.PaymentIntentId,
                    paymentEvent.EventType,
                    null,
                    installmentId,
                    paymentEvent.AmountCents,
                    paymentEvent.FailureCode),
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return notice;
    }

    private async Task<BankLinkPaymentStarted> StartAsync(
        Guid orgId,
        Guid? leaseId,
        RentLedgerEntry? installment,
        string? customerId,
        string? accountId,
        string billingName,
        BankLinkPayer payer,
        long amountCents,
        string currency,
        string idempotencyKey,
        BankLinkCaller caller,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(accountId))
            throw new DomainRuleException(BankLinkErrorCodes.NotLinked, "BankLinkNotLinked");

        var linked = await gateway.GetAccountAsync(accountId, cancellationToken);
        if (!string.Equals(linked.CustomerId, customerId, StringComparison.Ordinal))
            throw new DomainRuleException(BankLinkErrorCodes.AccountHolderMismatch, "BankLinkAccountHolderMismatch");
        if (!StripeBankLinkGateway.CanDebit(linked.SupportedPaymentMethodTypes, currency))
            throw new DomainRuleException(BankLinkErrorCodes.DebitUnavailable, "BankLinkDebitUnavailable");

        if (installment is not null)
            await CancelOpenCardIntentAsync(installment, cancellationToken);

        var debit = await gateway.StartDebitAsync(
            new BankDebitCommand(
                customerId,
                linked.Id,
                linked.SupportedPaymentMethodTypes,
                amountCents,
                currency,
                idempotencyKey,
                billingName,
                caller.IpAddress,
                caller.UserAgent,
                Metadata(orgId, leaseId, installment, payer)),
            cancellationToken);

        var existing = await db.BankLinkPayments
            .SingleOrDefaultAsync(p => p.StripePaymentIntentId == debit.PaymentIntentId, cancellationToken);
        if (existing is not null)
            return Started(existing, debit.ClientSecret, debit.Status);

        var row = new BankLinkPayment
        {
            OrgId = orgId,
            LeaseContractId = leaseId,
            RentLedgerEntryId = installment?.Id,
            Payer = payer,
            FinancialConnectionsAccountId = linked.Id,
            StripePaymentIntentId = debit.PaymentIntentId,
            StripePaymentMethodId = debit.PaymentMethodId,
            PaymentMethodType = debit.PaymentMethodType,
            AmountCents = amountCents,
            Currency = currency,
            Status = BankLinkPaymentStatus.Processing,
            CreatedAt = UtcNow,
            UpdatedAt = UtcNow,
        };
        db.BankLinkPayments.Add(row);
        if (installment is not null)
        {
            installment.PaymentIntentCount++;
            installment.StripePaymentIntentId = debit.PaymentIntentId;
            installment.ConnectedAccountId = null;
            installment.Status = RentLedgerStatus.Processing;
            installment.ChargedAt ??= UtcNow;
            installment.UpdatedAt = UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Bank-link payment {PaymentId} started for org {OrgId} ({PaymentIntentId}, {PaymentMethodType})",
            row.Id,
            orgId,
            debit.PaymentIntentId,
            debit.PaymentMethodType);
        return Started(row, debit.ClientSecret, debit.Status);
    }

    private async Task CancelOpenCardIntentAsync(RentLedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.StripePaymentIntentId is null || entry.ConnectedAccountId is null)
            return;

        var current = await stripe.GetPaymentIntentAsync(entry.StripePaymentIntentId, entry.ConnectedAccountId, cancellationToken);
        if (current.Status is "succeeded")
            throw new DomainConflictException(RentBillingErrorCodes.InstallmentNotPayable, "RentInstallmentAlreadyPaid");
        if (current.Status is "processing")
            throw new DomainConflictException(RentBillingErrorCodes.InstallmentInFlight, "RentInstallmentInFlight");
        if (current.Status is not null && Cancelable.Contains(current.Status))
        {
            await stripe.CancelPaymentIntentAsync(
                entry.StripePaymentIntentId,
                entry.ConnectedAccountId,
                $"bank-link-cancel:{entry.Id:N}:{entry.StripePaymentIntentId}",
                cancellationToken);
        }
    }

    private async Task<(Guid? LeaseId, RentLedgerEntry? Installment, long Amount, string Currency)> ResolveAmountAsync(
        Guid orgId, StartBankLinkPayment request, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (request.InstallmentId is not Guid installmentId)
        {
            if (request.AmountCents is not long amount || amount is <= 0 or > MaxAmountCents)
                throw new DomainRuleException(BankLinkErrorCodes.AmountInvalid, "BankLinkAmountInvalid");
            var currency = NormalizeCurrency(request.Currency);
            return (request.LeaseId, null, amount, currency);
        }

        if (request.LeaseId is not Guid leaseId)
            throw new DomainRuleException(BankLinkErrorCodes.InstallmentMismatch, "BankLinkInstallmentMismatch");

        var entry = await db.RentLedgerEntries
            .SingleOrDefaultAsync(e => e.Id == installmentId && e.LeaseContractId == leaseId && e.OrgId == orgId, cancellationToken)
            ?? throw new NotFoundException("Rent installment not found")
            {
                Code = RentBillingErrorCodes.InstallmentNotFound,
                MessageKey = "RentInstallmentNotFound",
            };
        EnsureOpen(entry);
        var due = RentCharges.ToCents(entry.AmountDue);
        if (request.AmountCents is long given && given != due)
            throw new DomainRuleException(BankLinkErrorCodes.AmountInvalid, "BankLinkAmountInvalid");
        if (request.Currency is not null && !string.Equals(NormalizeCurrency(request.Currency), RentCharges.Currency, StringComparison.Ordinal))
            throw new DomainRuleException(BankLinkErrorCodes.CurrencyInvalid, "BankLinkCurrencyInvalid");
        return (leaseId, entry, due, RentCharges.Currency);
    }

    private async Task<LinkedFinancialAccount> RequireHolderAsync(
        string accountId, string customerId, CancellationToken cancellationToken)
    {
        if (!StripeBankLinkGateway.IsAccountId(accountId))
            throw new DomainRuleException(BankLinkErrorCodes.AccountIdInvalid, "BankLinkAccountIdInvalid");

        var account = await gateway.GetAccountAsync(accountId, cancellationToken);
        if (!string.Equals(account.CustomerId, customerId, StringComparison.Ordinal))
            throw new DomainRuleException(BankLinkErrorCodes.AccountHolderMismatch, "BankLinkAccountHolderMismatch");
        if (string.Equals(account.Status, "disconnected", StringComparison.Ordinal))
            throw new DomainRuleException(BankLinkErrorCodes.AccountIdInvalid, "BankLinkAccountIdInvalid");
        return account;
    }

    private async Task<Org> LoadOrgAsync(Guid orgId, CancellationToken cancellationToken) =>
        await db.Orgs.SingleOrDefaultAsync(o => o.Id == orgId, cancellationToken)
        ?? throw new NotFoundException("Organization not found") { Code = "not_found", MessageKey = "OrganizationNotFound" };

    private async Task<RentLedgerEntry> LoadByTokenAsync(Guid installmentId, string token, CancellationToken cancellationToken)
    {
        var entry = await db.RentLedgerEntries
            .Include(e => e.LeaseContract)
            .SingleOrDefaultAsync(e => e.Id == installmentId, cancellationToken);
        if (entry is null || !CheckoutOutcomes.TokenMatches(entry.PaymentTokenHash, token))
        {
            throw new NotFoundException("Rent payment link does not match an installment")
            {
                Code = RentBillingErrorCodes.PaymentLinkInvalid,
                MessageKey = "RentPaymentLinkInvalid",
            };
        }

        return entry;
    }

    private static void EnsureOpen(RentLedgerEntry entry)
    {
        switch (entry.Status)
        {
            case RentLedgerStatus.Paid:
                throw new DomainConflictException(RentBillingErrorCodes.InstallmentNotPayable, "RentInstallmentAlreadyPaid");
            case RentLedgerStatus.Processing:
                throw new DomainConflictException(RentBillingErrorCodes.InstallmentInFlight, "RentInstallmentInFlight");
            case RentLedgerStatus.Cancelled:
                throw new DomainConflictException(RentBillingErrorCodes.InstallmentNotPayable, "RentInstallmentNotPayable");
        }
    }

    private static string IdempotencyKey(string? requested, RentLedgerEntry? installment)
    {
        if (installment is not null)
            return $"bank-link:{installment.Id:N}:{installment.PaymentIntentCount + 1}";
        ValidateIdempotencyKey(requested);
        return string.IsNullOrWhiteSpace(requested) ? $"bank-link:{Guid.NewGuid():N}" : requested.Trim();
    }

    private static void ValidateIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (key.Length is < 8 or > 64 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new DomainRuleException(BankLinkErrorCodes.IdempotencyKeyInvalid, "BankLinkIdempotencyKeyInvalid");
    }

    private static string NormalizeCurrency(string? currency)
    {
        var value = currency?.Trim().ToLowerInvariant();
        if (value is not { Length: 3 } || value.Any(c => !char.IsAsciiLetter(c)))
            throw new DomainRuleException(BankLinkErrorCodes.CurrencyInvalid, "BankLinkCurrencyInvalid");
        return value;
    }

    private Dictionary<string, string> Metadata(Guid orgId, Guid? leaseId, RentLedgerEntry? installment, BankLinkPayer payer)
    {
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = BankLinkCharges.Kind,
            ["orgId"] = orgId.ToString(),
            ["payer"] = payer.ToString().ToLowerInvariant(),
        };
        if (leaseId is Guid lease)
            metadata["leaseId"] = lease.ToString();
        if (installment is not null)
            metadata[RentCharges.InstallmentMetadataKey] = installment.Id.ToString();
        return metadata;
    }

    private static BankLinkPaymentStatus? StatusOf(string eventType) => eventType switch
    {
        "payment_intent.succeeded" => BankLinkPaymentStatus.Succeeded,
        "payment_intent.processing" => BankLinkPaymentStatus.Processing,
        "payment_intent.payment_failed" => BankLinkPaymentStatus.Failed,
        "payment_intent.canceled" => BankLinkPaymentStatus.Canceled,
        _ => null,
    };

    private static BankLinkAccount ToAccount(LinkedFinancialAccount account) =>
        new(true, account.Id, account.InstitutionName, account.Last4, account.DisplayName);

    private static BankLinkPaymentStarted Started(BankLinkPayment row, string? clientSecret, string stripeStatus) =>
        new(row.Id, row.StripePaymentIntentId, clientSecret, stripeStatus, row.PaymentMethodType, DueSettled: false);

    private static DateTime UtcNow => DateTime.UtcNow;
}
