using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>All callers supply a server-authorized org/lease pair; public callers must prove the installment token.
/// The lease lock is shared with manual payments, termination, offline settlement and webhook processing.</summary>
public sealed class TenantRentSepaService(AppDbContext db, IStripeRentSepaGateway gateway, IStripeService stripe,
    IConfiguration config, ILogger<TenantRentSepaService> logger, IRentBillingService rent, TimeProvider? timeProvider = null) : ITenantRentSepaService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private DateOnly Today => clock.TodayInRomeAsDateOnly();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> Lock(Guid leaseId, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.RentLease, leaseId.ToString("N")));

    private async Task<LeaseContract> Load(Guid org, Guid lease, CancellationToken ct) =>
        await db.LeaseContracts.IgnoreQueryFilters().Include(l => l.Org).Include(l => l.RentSchedule)
            .SingleOrDefaultAsync(l => l.Id == lease && l.OrgId == org, ct) ?? throw new NotFoundException("Lease not found");
    private static RentSchedule Schedule(LeaseContract lease) => lease.RentSchedule
        ?? throw new DomainRuleException("rent_schedule_not_found", "RentScheduleNotFound");
    private void EnsureActive(LeaseContract lease)
    {
        if (!TenantSepaAuthorization.CanDebit(lease, Today) || !Schedule(lease).IsActive || !lease.Org.ConnectChargesEnabled
            || !string.Equals(Schedule(lease).Currency, "eur", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(lease.Org.StripeConnectedAccountId))
            throw new DomainConflictException("rent_sepa_unavailable", "RentSepaUnavailable");
    }
    private TenantSepaSettings View(LeaseContract lease, string user)
    {
        var s = Schedule(lease);
        var active = TenantSepaAuthorization.CanDebit(lease, Today) && s.IsActive && lease.Org.ConnectChargesEnabled;
        return new(s.SepaAuthorizedAt is not null && s.SepaPaymentMethodId is not null && active,
            s.SepaAutomaticEnabled && active, active,
            RomeCalendar.DateInRome(lease.CurrentTermEndDate), TenantSepaAuthorization.BlockedFrom(lease),
            TenantSepaAuthorization.TransactionFee,
            s.SepaAuthorizedByUserId is null || s.SepaAuthorizedByUserId == user);
    }
    public async Task<TenantSepaSettings> GetAsync(Guid orgId, Guid leaseId, string userId, CancellationToken ct = default)
    {
        await using var tx = await Lock(leaseId, ct);
        var lease = await Load(orgId, leaseId, ct);
        var s = Schedule(lease);
        // Recover a completed setup after a redirect, closed tab or lost browser response. This never enables autopay.
        if (s.SepaAuthorizedAt is null && s.SepaSetupIntentId is not null && s.SepaAuthorizedByUserId == userId
            && s.LandlordStripeAccountId == lease.Org.StripeConnectedAccountId)
        {
            var setup = await gateway.ReadSetupAsync(s.LandlordStripeAccountId!, s.SepaSetupIntentId, ct);
            if (setup.Status == "succeeded" && setup.CustomerId == s.SepaCustomerId && setup.PaymentMethod?.Type == "sepa_debit"
                && setup.OnBehalfOfId == s.LandlordStripeAccountId && setup.Metadata.GetValueOrDefault("leaseId") == leaseId.ToString()
                && setup.Metadata.GetValueOrDefault("userId") == userId)
            {
                s.SepaPaymentMethodId = setup.PaymentMethodId;
                s.MandateReference = setup.MandateId;
                s.SepaAuthorizedAt = Now;
                await db.SaveChangesAsync(ct);
            }
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return View(lease, userId);
    }

    public async Task<TenantSepaSession> SetupAsync(Guid orgId, Guid leaseId, string userId, string email, CancellationToken ct = default)
    {
        await using var tx = await Lock(leaseId, ct);
        var lease = await Load(orgId, leaseId, ct);
        EnsureActive(lease);
        var s = Schedule(lease);
        if (s.SepaAuthorizedByUserId is not null && s.SepaAuthorizedByUserId != userId)
            throw new DomainConflictException("rent_sepa_other_holder", "RentSepaOtherHolder");
        var account = lease.Org.StripeConnectedAccountId!;
        if (s.LandlordStripeAccountId is not null && s.LandlordStripeAccountId != account)
            throw new DomainConflictException("rent_sepa_account_changed", "RentSepaAccountChanged");
        SetupIntent setup;
        if (s.SepaSetupIntentId is not null)
            setup = await gateway.ReadSetupAsync(account, s.SepaSetupIntentId, ct);
        else
        {
            setup = await gateway.SetupAsync(account, leaseId, userId, email, $"rent-sepa-setup:{leaseId:N}:{userId}:{s.SepaSetupGeneration}", ct);
            s.SepaSetupIntentId = setup.Id;
            s.SepaCustomerId = setup.CustomerId;
            s.LandlordStripeAccountId = account;
            s.SepaAuthorizedByUserId = userId;
            await db.SaveChangesAsync(ct);
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return new(setup.ClientSecret, config["Stripe:PublishableKey"] ?? "", "");
    }

    public async Task<TenantSepaSettings> SetAutomaticAsync(Guid orgId, Guid leaseId, string userId, bool enabled, CancellationToken ct = default)
    {
        await using var tx = await Lock(leaseId, ct);
        var lease = await Load(orgId, leaseId, ct);
        var s = Schedule(lease);
        if (s.SepaAuthorizedByUserId != userId)
            throw new DomainConflictException("rent_sepa_other_holder", "RentSepaOtherHolder");
        // Disabling remains possible after expiry and does not revoke manual payment of valid dues.
        if (enabled || s.SepaAuthorizedAt is null)
        {
            EnsureActive(lease);
            if (enabled && s.Cadence != RentCadence.Monthly)
                throw new DomainRuleException("rent_sepa_monthly_required", "RentSepaMonthlyRequired");
            if (s.SepaSetupIntentId is null || s.LandlordStripeAccountId != lease.Org.StripeConnectedAccountId)
                throw new DomainConflictException("rent_sepa_not_authorized", "RentSepaNotAuthorized");
            var setup = await gateway.ReadSetupAsync(s.LandlordStripeAccountId!, s.SepaSetupIntentId, ct);
            if (setup.Status != "succeeded" || setup.CustomerId != s.SepaCustomerId || setup.PaymentMethod?.Type != "sepa_debit"
                || setup.Metadata.GetValueOrDefault("leaseId") != leaseId.ToString()
                || setup.Metadata.GetValueOrDefault("userId") != userId || setup.OnBehalfOfId != lease.Org.StripeConnectedAccountId)
                throw new DomainConflictException("rent_sepa_not_authorized", "RentSepaNotAuthorized");
            s.SepaPaymentMethodId = setup.PaymentMethodId;
            s.MandateReference = setup.MandateId;
            s.SepaAuthorizedAt ??= Now;
        }
        s.SepaAutomaticEnabled = enabled;
        // Do not automatically collect historical arrears when enabling automatic payment.
        s.SepaAutomaticFrom = enabled ? Today : null;
        s.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return View(lease, userId);
    }

    public async Task<TenantSepaSession> PayAsync(Guid orgId, Guid leaseId, Guid installmentId, CancellationToken ct = default, string? userId = null)
    {
        await using var tx = await Lock(leaseId, ct);
        var lease = await Load(orgId, leaseId, ct);
        EnsureActive(lease);
        var entry = await Entry(orgId, leaseId, installmentId, ct);
        var schedule = Schedule(lease);
        var saved = userId is not null && schedule.SepaAuthorizedByUserId == userId && schedule.SepaAuthorizedAt is not null
            && schedule.SepaPaymentMethodId is not null && schedule.SepaCustomerId is not null
            && schedule.LandlordStripeAccountId == lease.Org.StripeConnectedAccountId;
        var pi = await Prepare(lease, entry, ct, saved);
        if (saved) pi = await gateway.AttachAsync(pi.Id, schedule.SepaCustomerId!, schedule.SepaPaymentMethodId!, ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new(pi.ClientSecret, config["Stripe:PublishableKey"] ?? "", "", saved);
    }
    public async Task<TenantSepaSession> PayPublicAsync(Guid installmentId, string token, CancellationToken ct = default)
    {
        var entry = await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.Id == installmentId, ct);
        if (entry is null || !CheckoutOutcomes.TokenMatches(entry.PaymentTokenHash, token))
            throw new NotFoundException("Rent payment link invalid") { Code = RentBillingErrorCodes.PaymentLinkInvalid, MessageKey = "RentPaymentLinkInvalid" };
        await using var tx = await Lock(entry.LeaseContractId, ct);
        // Recheck the token under the payment lock; reminders may rotate it.
        entry = await Entry(entry.OrgId, entry.LeaseContractId, installmentId, ct);
        if (!CheckoutOutcomes.TokenMatches(entry.PaymentTokenHash, token))
            throw new NotFoundException("Rent payment link invalid") { Code = RentBillingErrorCodes.PaymentLinkInvalid, MessageKey = "RentPaymentLinkInvalid" };
        var session = await PayAsync(entry.OrgId, entry.LeaseContractId, installmentId, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return session;
    }
    private async Task<RentLedgerEntry> Entry(Guid org, Guid lease, Guid id, CancellationToken ct)
    {
        var e = await db.RentLedgerEntries.IgnoreQueryFilters().SingleOrDefaultAsync(e => e.Id == id && e.OrgId == org && e.LeaseContractId == lease, ct)
            ?? throw new NotFoundException("Installment not found");
        await db.Entry(e).ReloadAsync(ct);
        return e;
    }
    private async Task<PaymentIntent> Prepare(LeaseContract lease, RentLedgerEntry entry, CancellationToken ct, bool allowSaved = false)
    {
        if (entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled
            || entry.PeriodStart > RomeCalendar.DateInRome(lease.CurrentTermEndDate) || entry.AmountDue <= 0)
            throw new DomainConflictException(RentBillingErrorCodes.InstallmentNotPayable, "RentInstallmentNotPayable");
        if (entry.Status == RentLedgerStatus.Processing)
            throw new DomainConflictException(RentBillingErrorCodes.InstallmentInFlight, "RentInstallmentInFlight");
        var account = lease.Org.StripeConnectedAccountId!;
        var amount = RentCharges.ToCents(entry.AmountDue + TenantSepaAuthorization.TransactionFee);
        if (entry.StripePaymentIntentId is not null)
        {
            var current = await stripe.GetPaymentIntentAsync(entry.StripePaymentIntentId, entry.ConnectedAccountId, ct);
            if (current.Status is "processing" or "succeeded")
                throw new DomainConflictException(RentBillingErrorCodes.InstallmentInFlight, "RentInstallmentInFlight");
            if (current.Status != "canceled")
            {
                if (entry.ConnectedAccountId is null && current.TransferData?.DestinationId == account && (allowSaved || current.CustomerId is null) && current.Amount == amount && entry.TransactionFee == 1m)
                    return current;
                await stripe.CancelPaymentIntentAsync(current.Id, entry.ConnectedAccountId,
                    RentCharges.CancellationIdempotencyKey(entry.Id, current.Id), ct);
            }
        }
        entry.PaymentIntentCount++;
        var created = await gateway.CreateAsync(account, lease.Id, entry.Id, amount,
            RentCharges.CreationIdempotencyKey(entry.Id, entry.PaymentIntentCount), ct);
        entry.StripePaymentIntentId = created.Id;
        entry.ConnectedAccountId = null;
        entry.TransactionFee = TenantSepaAuthorization.TransactionFee;
        entry.ChargedAt ??= Now;
        entry.UpdatedAt = Now;
        return created;
    }

    private async Task ExpireAsync(CancellationToken ct)
    {
        var candidates = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.RentSchedule != null && l.RentSchedule.IsActive && l.CurrentTermEndDate < Now)
            .Select(l => new { l.Id, l.OrgId }).ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            db.ChangeTracker.Clear();
            await using var tx = await Lock(candidate.Id, ct);
            var lease = await Load(candidate.OrgId, candidate.Id, ct);
            if (Today < TenantSepaAuthorization.BlockedFrom(lease)
                || LeaseRenewal.IsDue(lease, clock.TodayInRome())) continue;
            var schedule = Schedule(lease);
            schedule.SepaAutomaticEnabled = false;
            var open = await db.RentLedgerEntries.IgnoreQueryFilters()
                .Where(e => e.OrgId == lease.OrgId && e.LeaseContractId == lease.Id
                    && (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed)
                    && e.StripePaymentIntentId != null).ToListAsync(ct);
            foreach (var entry in open)
            {
                var pi = await stripe.GetPaymentIntentAsync(entry.StripePaymentIntentId!, entry.ConnectedAccountId, ct);
                if (pi.Status is "requires_payment_method" or "requires_confirmation" or "requires_action")
                    await stripe.CancelPaymentIntentAsync(pi.Id, entry.ConnectedAccountId,
                        RentCharges.CancellationIdempotencyKey(entry.Id, pi.Id), ct);
            }
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var today = Today;
        await ExpireAsync(ct);
        var candidates = await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => (e.RentSchedule.SepaAutomaticEnabled && e.RentSchedule.IsActive && e.DueDate <= today && e.Status == RentLedgerStatus.Scheduled)
                || (e.Status == RentLedgerStatus.Processing && e.SepaAutomaticAttemptedAt != null))
            .Select(e => new { e.Id, e.OrgId, e.LeaseContractId }).ToListAsync(ct);
        foreach (var c in candidates)
        {
            try
            {
                db.ChangeTracker.Clear();
                await using var tx = await Lock(c.LeaseContractId, ct);
                var lease = await Load(c.OrgId, c.LeaseContractId, ct);
                var s = Schedule(lease);
                var entry = await Entry(c.OrgId, lease.Id, c.Id, ct);
                PaymentIntent pi;
                if (entry.Status == RentLedgerStatus.Processing && entry.SepaAutomaticAttemptedAt is not null)
                {
                    pi = await stripe.GetPaymentIntentAsync(entry.StripePaymentIntentId!, entry.ConnectedAccountId, ct);
                    if (pi.Status == "requires_confirmation")
                    {
                        // A process may stop after reservation but before confirmation. Resume only while consent is valid.
                        if (!s.SepaAutomaticEnabled || !TenantSepaAuthorization.CanDebit(lease, today)
                            || !s.IsActive || s.LandlordStripeAccountId != lease.Org.StripeConnectedAccountId)
                        {
                            await stripe.CancelPaymentIntentAsync(pi.Id, entry.ConnectedAccountId,
                                RentCharges.CancellationIdempotencyKey(entry.Id, pi.Id), ct);
                            entry.Status = TenantSepaAuthorization.CanDebit(lease, today) ? RentLedgerStatus.Scheduled : RentLedgerStatus.Cancelled;
                            entry.StripePaymentIntentId = null;
                            await db.SaveChangesAsync(ct);
                            if (tx is not null) await tx.CommitAsync(ct);
                            continue;
                        }
                    }
                }
                else
                {
                    var eligible = TenantSepaAuthorization.CanAutomate(lease, s, entry, today);
                    if (!eligible || entry.StripePaymentIntentId is not null
                        || s.LandlordStripeAccountId != lease.Org.StripeConnectedAccountId) continue;
                    EnsureActive(lease);
                    pi = await Prepare(lease, entry, ct);
                    entry.SepaAutomaticAttemptedAt = Now;
                    entry.Status = RentLedgerStatus.Processing;
                    await db.SaveChangesAsync(ct);
                }
                if (tx is not null) { await tx.CommitAsync(ct); await tx.DisposeAsync(); }
                await using var confirmationLock = await Lock(lease.Id, ct);
                db.ChangeTracker.Clear();
                lease = await Load(c.OrgId, c.LeaseContractId, ct);
                s = Schedule(lease);
                entry = await Entry(c.OrgId, lease.Id, c.Id, ct);
                if (!s.SepaAutomaticEnabled || !TenantSepaAuthorization.CanDebit(lease, today) || !s.IsActive)
                {
                    if (pi.Status is "requires_confirmation" or "requires_payment_method")
                    {
                        await stripe.CancelPaymentIntentAsync(pi.Id, entry.ConnectedAccountId,
                            RentCharges.CancellationIdempotencyKey(entry.Id, pi.Id), ct);
                        entry.Status = TenantSepaAuthorization.CanDebit(lease, today) ? RentLedgerStatus.Scheduled : RentLedgerStatus.Cancelled;
                        entry.StripePaymentIntentId = null;
                        await db.SaveChangesAsync(ct);
                    }
                    if (confirmationLock is not null) await confirmationLock.CommitAsync(ct);
                    continue;
                }
                // Durable reservation blocks manual payment; the confirmation key is stable across process restarts.
                if (pi.Status == "requires_confirmation" || pi.Status == "requires_payment_method" && pi.LastPaymentError is null)
                {
                    try { pi = await gateway.ConfirmAsync(s.LandlordStripeAccountId!, pi.Id, s.SepaCustomerId!, s.SepaPaymentMethodId!, ct); }
                    catch (StripeException ex) when (ex.StripeError?.PaymentIntent is not null)
                    { pi = ex.StripeError.PaymentIntent; }
                }
                var type = pi.Status switch { "succeeded" => "payment_intent.succeeded", "requires_payment_method" => "payment_intent.payment_failed", "requires_action" => "payment_intent.payment_failed", "canceled" => "payment_intent.canceled", _ => "payment_intent.processing" };

                entry = await Entry(c.OrgId, lease.Id, c.Id, ct);
                var notice = new RentPaymentEventApplier(clock, logger).Apply(entry,
                    new(pi.Id, type, entry.ConnectedAccountId, entry.Id, pi.Amount, pi.LastPaymentError?.Code));
                await db.SaveChangesAsync(ct);
                if (confirmationLock is not null) { await confirmationLock.CommitAsync(ct); await confirmationLock.DisposeAsync(); }
                if (notice is not null) await rent.CompleteAsync(notice, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { logger.LogError(ex, "SEPA collection failed for installment {InstallmentId}; reserved payments await Stripe reconciliation", c.Id); }
        }
    }
}
