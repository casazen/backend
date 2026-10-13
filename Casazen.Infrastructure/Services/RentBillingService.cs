using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IRentBillingService"/>
/// <remarks>
/// <para>
/// Payment model (coherent with the direct checkout, BK-02/BK-08): <b>direct charge on the landlord org's Stripe connected
/// account</b>, no application fee. The tenant pays on-session from a personal link (public page with the Stripe Payment
/// Element, like the checkout outcome page): one PaymentIntent per installment at a time, created with an idempotency key
/// and reused while payable. An installment is <see cref="RentLedgerStatus.Paid"/> only when Stripe reports
/// <c>succeeded</c> (webhook, or the job reading a payment in flight) or when the landlord declares an offline payment.
/// </para>
/// <para>
/// Every change of the installments of a lease runs under the <see cref="PostgresAdvisoryLocks.Scope.RentLease"/> lock of
/// that lease (READ COMMITTED transaction; the webhook joins its event transaction), held for the Stripe calls: the
/// tenant's payment session, an offline payment, the webhook and the job take turns. Emails are queued after the commit
/// (FD-13). Logs carry lease, installment and Stripe ids only.
/// </para>
/// </remarks>
public sealed partial class RentBillingService(
    AppDbContext db,
    IStripeService stripeService,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IConfiguration configuration,
    ILogger<RentBillingService> logger,
    TimeProvider? timeProvider = null) : IRentBillingService
{
    private const decimal MaxInstallmentAmount = 1_000_000m;
    private const int MaxNoteLength = 500;
    private const string PaymentIntentUnexpectedStateCode = "payment_intent_unexpected_state";

    /// <summary>PaymentIntent statuses the tenant can still pay: nothing was collected.</summary>
    private static readonly HashSet<string> PayableStatuses = new(StringComparer.Ordinal)
    {
        "requires_payment_method",
        "requires_confirmation",
        "requires_action",
    };

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => _clock.TodayInRomeAsDateOnly();

    // ---------------------------------------------------------------- landlord

    public async Task<RentLedgerView> GetLedgerAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        var entries = await db.RentLedgerEntries
            .AsNoTracking()
            .Where(e => e.LeaseContractId == leaseId)
            .OrderBy(e => e.PeriodStart)
            .ToListAsync(cancellationToken);
        return BuildView(lease, entries);
    }

    public async Task<RentLedgerView> ConfigureScheduleAsync(
        Guid leaseId, ConfigureRentScheduleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        if (!CanConfigure(lease.Status))
            throw new DomainRuleException(RentBillingErrorCodes.LeaseNotSigned, "RentLeaseNotSigned");
        if (!Enum.IsDefined(request.Cadence))
            throw new ArgumentOutOfRangeException(nameof(request), request.Cadence, "Unknown rent cadence");

        var leaseStart = RomeCalendar.DateInRome(lease.StartDate);
        var leaseEnd = RomeCalendar.DateInRome(lease.EndDate);
        if (leaseEnd < leaseStart)
            throw new DomainRuleException(RentBillingErrorCodes.LeaseOutsidePlan, "RentLeaseDatesInvalid");

        var billingDay = request.BillingDayOfMonth ?? RentInstallmentPlan.DefaultBillingDay(leaseStart);
        if (!RentInstallmentPlan.IsValidBillingDay(billingDay))
        {
            throw new DomainRuleException(
                RentBillingErrorCodes.BillingDayInvalid,
                "RentBillingDayInvalid",
                RentInstallmentPlan.MinBillingDay,
                RentInstallmentPlan.MaxBillingDay);
        }

        var amount = request.Amount ?? RentInstallmentPlan.DefaultAmount(lease.MonthlyRent, request.Cadence);
        if (amount <= 0 || amount > MaxInstallmentAmount || decimal.Round(amount, 2) != amount)
            throw new DomainRuleException(RentBillingErrorCodes.AmountInvalid, "RentAmountInvalid");

        var plan = RentInstallmentPlan.Build(leaseStart, leaseEnd, request.Cadence, billingDay, amount);

        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);
        var schedule = await db.RentSchedules.SingleOrDefaultAsync(s => s.LeaseContractId == leaseId, cancellationToken);
        var entries = await db.RentLedgerEntries
            .Where(e => e.LeaseContractId == leaseId)
            .ToListAsync(cancellationToken);

        if (schedule is not null && schedule.Cadence != request.Cadence && entries.Any(e => !IsRegenerable(e)))
            throw new DomainRuleException(RentBillingErrorCodes.CadenceLocked, "RentCadenceLocked");

        var now = UtcNow;
        if (schedule is null)
        {
            schedule = new RentSchedule
            {
                LeaseContractId = lease.Id,
                OrgId = lease.OrgId,
                CreatedAt = now,
            };
            db.RentSchedules.Add(schedule);
        }

        schedule.Cadence = request.Cadence;
        schedule.BillingDayOfMonth = billingDay;
        schedule.Amount = amount;
        schedule.Currency = RentCharges.Currency;
        schedule.IsActive = true;
        schedule.NextRunDate = plan.Installments.Count > 0 ? plan.Installments[0].DueDate : leaseStart;
        schedule.UpdatedAt = now;

        Regenerate(lease, schedule, entries, plan, now);

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Rent schedule of lease {LeaseId} set: {Cadence}, due day {BillingDay}, {Installments} installments",
            leaseId,
            request.Cadence,
            billingDay,
            plan.Installments.Count);
        return await GetLedgerAsync(leaseId, cancellationToken);
    }

    public async Task<RentLedgerView> DisableScheduleAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        await LoadLeaseAsync(leaseId, cancellationToken);

        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);
        var schedule = await db.RentSchedules.SingleOrDefaultAsync(s => s.LeaseContractId == leaseId, cancellationToken)
            ?? throw ScheduleNotFound();
        var open = await db.RentLedgerEntries
            .Where(e => e.LeaseContractId == leaseId &&
                        (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed))
            .ToListAsync(cancellationToken);

        var now = UtcNow;
        var notices = new List<RentPaymentNotice>();
        foreach (var entry in open)
        {
            // A PaymentIntent the tenant could still pay is canceled first; one paid meanwhile wins.
            if (entry.StripePaymentIntentId is not null && entry.ConnectedAccountId is not null)
            {
                var current = await CancelIfPayableAsync(entry, cancellationToken);
                if (current.Status != "canceled")
                {
                    if (ApplyStripeStatus(entry, current) is { } notice)
                        notices.Add(notice);
                    continue;
                }
            }

            entry.Status = RentLedgerStatus.Cancelled;
            entry.StripePaymentIntentId = null;
            entry.PaymentTokenHash = null;
            entry.UpdatedAt = now;
        }

        schedule.IsActive = false;
        schedule.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Rent schedule of lease {LeaseId} disabled", leaseId);
        foreach (var notice in notices)
            await CompleteAsync(notice, cancellationToken);
        return await GetLedgerAsync(leaseId, cancellationToken);
    }

    public async Task<RentInstallmentView> MarkPaidOfflineAsync(
        Guid leaseId,
        Guid installmentId,
        MarkRentPaidOfflineRequest request,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        var leaseStart = RomeCalendar.DateInRome(lease.StartDate);
        if (request.PaidOn > Today || request.PaidOn < leaseStart.AddYears(-1))
            throw new DomainRuleException(RentBillingErrorCodes.PaidOnInvalid, "RentPaidOnInvalid");

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
            throw new DomainRuleException(RentBillingErrorCodes.PaidOnInvalid, "RentPaidOnInvalid");

        RentInstallmentView view;
        RentPaymentNotice? notice = null;
        DomainException? refusal = null;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
            switch (entry.Status)
            {
                case RentLedgerStatus.Paid:
                    throw AlreadyPaid();
                case RentLedgerStatus.Processing:
                    throw InFlight();
                case RentLedgerStatus.Cancelled:
                    throw NotPayable();
            }

            if (entry.StripePaymentIntentId is not null && entry.ConnectedAccountId is not null)
            {
                // The tenant must not be able to pay online an installment declared paid: cancel the PaymentIntent
                // first. Paid or in flight on Stripe meanwhile: that state is recorded and the declaration refused.
                var current = await CancelIfPayableAsync(entry, cancellationToken);
                if (current.Status != "canceled")
                {
                    notice = ApplyStripeStatus(entry, current);
                    refusal = entry.Status == RentLedgerStatus.Paid ? AlreadyPaid() : InFlight();
                }
            }

            if (refusal is null)
            {
                var now = UtcNow;
                entry.Status = RentLedgerStatus.Paid;
                entry.PaidVia = RentPaymentChannel.Offline;
                entry.PaidOn = request.PaidOn;
                entry.PaidAt = now;
                entry.MarkedPaidByUserId = userId;
                entry.OfflinePaymentNote = note;
                entry.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            view = ToView(entry, Today);
        }

        if (notice is not null)
            await CompleteAsync(notice, cancellationToken);
        if (refusal is not null)
            throw refusal;

        logger.LogInformation("Rent installment {InstallmentId} of lease {LeaseId} declared paid offline", installmentId, leaseId);
        return view;
    }

    public async Task<RentInstallmentView> SendPaymentRequestAsync(
        Guid leaseId, Guid installmentId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        if (!OnlinePaymentsAvailable(lease.Org))
            throw new DomainRuleException(RentBillingErrorCodes.OnlinePaymentsUnavailable, "RentOnlinePaymentsUnavailable");
        if (Tenants(lease).Count == 0)
            throw new DomainRuleException(RentBillingErrorCodes.NoTenantEmail, "RentNoTenantEmail");

        string token;
        RentInstallmentView view;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
            var schedule = await db.RentSchedules.AsNoTracking().SingleAsync(s => s.Id == entry.RentScheduleId, cancellationToken);
            EnsurePayable(entry, schedule);

            token = IssueToken(entry);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            view = ToView(entry, Today);
        }

        if (!QueuePaymentRequest(lease, view, token))
        {
            await ResetRequestAsync(installmentId, cancellationToken);
            throw new DomainRuleException("rent_payment_request_not_sent", "RentPaymentRequestNotSent");
        }

        logger.LogInformation("Rent payment request of installment {InstallmentId} (lease {LeaseId}) queued", installmentId, leaseId);
        return view;
    }

    // ---------------------------------------------------------------- tenant (anonymous)

    public async Task<PublicRentPayment> GetPublicPaymentAsync(
        Guid installmentId, string token, CancellationToken cancellationToken = default)
    {
        var entry = await LoadByTokenAsync(installmentId, token, cancellationToken);
        return new PublicRentPayment(
            entry.Id,
            entry.LeaseContract.Property?.Name ?? string.Empty,
            entry.Org.Name,
            entry.PeriodStart,
            entry.PeriodEnd,
            entry.DueDate,
            entry.AmountDue,
            RentCharges.Currency.ToUpperInvariant(),
            PublicStateOf(entry),
            entry.Status == RentLedgerStatus.Failed);
    }

    public async Task<PublicRentPaymentSession> CreatePaymentSessionAsync(
        Guid installmentId, string token, CancellationToken cancellationToken = default)
    {
        var found = await LoadByTokenAsync(installmentId, token, cancellationToken);
        var leaseId = found.LeaseContractId;

        PublicRentPaymentSession? session = null;
        RentPaymentNotice? notice = null;
        DomainException? refusal = null;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            // Read again under the lock: the webhook, the job or the landlord may have changed it meanwhile.
            var entry = await db.RentLedgerEntries
                .Include(e => e.Org)
                .Include(e => e.RentSchedule)
                .SingleAsync(e => e.Id == installmentId, cancellationToken);
            await db.Entry(entry).ReloadAsync(cancellationToken);
            if (!CheckoutOutcomes.TokenMatches(entry.PaymentTokenHash, token))
                throw LinkInvalid();

            switch (PublicStateOf(entry))
            {
                case PublicRentPaymentState.Paid:
                    throw AlreadyPaid();
                case PublicRentPaymentState.Processing:
                    throw InFlight();
                case PublicRentPaymentState.Unavailable:
                    throw NotPayable();
            }

            var account = entry.Org.StripeConnectedAccountId!;
            var amountCents = RentCharges.ToCents(entry.AmountDue);
            PaymentIntent? paymentIntent = null;
            if (entry.StripePaymentIntentId is not null)
            {
                var current = await stripeService.GetPaymentIntentAsync(
                    entry.StripePaymentIntentId, entry.ConnectedAccountId ?? account, cancellationToken);
                if (current.Status is not null && PayableStatuses.Contains(current.Status))
                {
                    if (current.Amount == amountCents && string.Equals(entry.ConnectedAccountId ?? account, account, StringComparison.Ordinal))
                    {
                        paymentIntent = current;
                    }
                    else
                    {
                        // The amount (or the landlord's account) changed since: that PaymentIntent is canceled.
                        current = await CancelIfPayableAsync(entry, cancellationToken);
                    }
                }

                if (paymentIntent is null && current.Status != "canceled")
                {
                    notice = ApplyStripeStatus(entry, current);
                    refusal = entry.Status == RentLedgerStatus.Paid ? AlreadyPaid() : InFlight();
                }
            }

            if (refusal is null && paymentIntent is null)
            {
                entry.PaymentIntentCount++;
                paymentIntent = await stripeService.CreateConnectedAccountPaymentIntentAsync(
                    account,
                    amountCents,
                    RentCharges.Currency,
                    new Dictionary<string, string>
                    {
                        ["kind"] = RentCharges.Kind,
                        [RentCharges.InstallmentMetadataKey] = entry.Id.ToString(),
                        ["leaseId"] = entry.LeaseContractId.ToString(),
                        ["orgId"] = entry.OrgId.ToString(),
                    },
                    RentCharges.CreationIdempotencyKey(entry.Id, entry.PaymentIntentCount),
                    $"Rent {entry.PeriodStart:yyyy-MM-dd} - {entry.PeriodEnd:yyyy-MM-dd}",
                    cancellationToken);
                entry.StripePaymentIntentId = paymentIntent.Id;
                entry.ConnectedAccountId = account;
                entry.ChargedAt ??= UtcNow;
                entry.UpdatedAt = UtcNow;
                logger.LogInformation(
                    "Rent installment {InstallmentId}: payment intent {PaymentIntentId} created on {AccountId}",
                    entry.Id,
                    paymentIntent.Id,
                    account);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            if (refusal is null)
            {
                session = new PublicRentPaymentSession(
                    entry.Id,
                    paymentIntent!.ClientSecret,
                    configuration["Stripe:PublishableKey"] ?? string.Empty,
                    entry.ConnectedAccountId ?? account);
            }
        }

        if (notice is not null)
            await CompleteAsync(notice, cancellationToken);
        if (refusal is not null)
            throw refusal;
        return session!;
    }

    // ---------------------------------------------------------------- webhooks

    public async Task<RentPaymentNotice?> ApplyPaymentIntentEventAsync(
        RentPaymentIntentEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        if (string.IsNullOrWhiteSpace(paymentEvent.AccountId))
        {
            // Rent PaymentIntents are always created on the landlord's connected account.
            logger.LogWarning(
                "Rent event {EventType} for {PaymentIntentId} from the platform account: ignored",
                paymentEvent.EventType,
                paymentEvent.PaymentIntentId);
            return null;
        }

        var installmentId = paymentEvent.InstallmentId ?? await db.RentLedgerEntries
            .Where(e => e.StripePaymentIntentId == paymentEvent.PaymentIntentId)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var leaseId = installmentId is { } id
            ? await db.RentLedgerEntries.Where(e => e.Id == id).Select(e => (Guid?)e.LeaseContractId).FirstOrDefaultAsync(cancellationToken)
            : null;
        if (installmentId is null || leaseId is null)
        {
            logger.LogWarning("No rent installment for payment intent {PaymentIntentId}: ignored", paymentEvent.PaymentIntentId);
            return null;
        }

        var ownTransaction = await LockLeaseAsync(leaseId.Value, cancellationToken);
        if (ownTransaction is not null)
        {
            await ownTransaction.DisposeAsync();
            throw new InvalidOperationException("Rent payment events must be applied inside the webhook event transaction.");
        }

        var entry = await db.RentLedgerEntries.SingleAsync(e => e.Id == installmentId, cancellationToken);
        await db.Entry(entry).ReloadAsync(cancellationToken);
        if (entry.ConnectedAccountId is { } expected &&
            !string.Equals(expected, paymentEvent.AccountId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rent payment intent {PaymentIntentId} reported by account {EventAccount}, expected {ExpectedAccount}: ignored",
                paymentEvent.PaymentIntentId,
                paymentEvent.AccountId,
                expected);
            return null;
        }

        var notice = ApplyEvent(entry, paymentEvent);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Rent payment intent {PaymentIntentId} of installment {InstallmentId}: {EventType} applied (status {Status})",
            paymentEvent.PaymentIntentId,
            entry.Id,
            paymentEvent.EventType,
            entry.Status);
        return notice;
    }

    /// <summary>
    /// Applies an event to an installment. Only the current PaymentIntent moves it, except a success, which is money
    /// received in any case: recorded once, and logged as an error when the installment was already paid another way.
    /// </summary>
    private RentPaymentNotice? ApplyEvent(RentLedgerEntry entry, RentPaymentIntentEvent paymentEvent)
    {
        var isCurrent = string.Equals(entry.StripePaymentIntentId, paymentEvent.PaymentIntentId, StringComparison.Ordinal);
        var now = UtcNow;
        switch (paymentEvent.EventType)
        {
            case "payment_intent.succeeded":
                if (entry.Status == RentLedgerStatus.Paid)
                {
                    if (!(entry.PaidVia == RentPaymentChannel.Stripe && isCurrent))
                    {
                        logger.LogError(
                            "Rent installment {InstallmentId} already paid ({PaidVia}) received payment intent {PaymentIntentId}: refund one of them from the Stripe dashboard",
                            entry.Id,
                            entry.PaidVia,
                            paymentEvent.PaymentIntentId);
                    }

                    return null;
                }

                if (entry.Status == RentLedgerStatus.Cancelled)
                {
                    logger.LogWarning(
                        "Rent installment {InstallmentId} was cancelled but payment intent {PaymentIntentId} succeeded: recorded as paid",
                        entry.Id,
                        paymentEvent.PaymentIntentId);
                }

                entry.Status = RentLedgerStatus.Paid;
                entry.PaidVia = RentPaymentChannel.Stripe;
                entry.PaidOn = Today;
                entry.PaidAt = now;
                entry.StripePaymentIntentId = paymentEvent.PaymentIntentId;
                entry.FailureCode = null;
                entry.UpdatedAt = now;
                return new RentPaymentNotice(entry.Id, RentPaymentNoticeKind.Received);

            case "payment_intent.processing":
                if (!isCurrent || entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled)
                    return null;
                entry.Status = RentLedgerStatus.Processing;
                entry.UpdatedAt = now;
                return null;

            case "payment_intent.payment_failed":
                if (!isCurrent || entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled)
                    return null;
                var wasInFlight = entry.Status == RentLedgerStatus.Processing;
                entry.Status = RentLedgerStatus.Failed;
                entry.FailureCode = Truncate(paymentEvent.FailureCode, 100);
                entry.LastFailedAt = now;
                entry.UpdatedAt = now;
                // On-session failures are shown to the tenant on the page; a payment that fails after it was accepted
                // (e.g. a SEPA debit returned) gets an email with a new link.
                return wasInFlight ? new RentPaymentNotice(entry.Id, RentPaymentNoticeKind.Failed) : null;

            case "payment_intent.canceled":
                if (!isCurrent || entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Cancelled)
                    return null;
                entry.StripePaymentIntentId = null;
                entry.Status = entry.Status == RentLedgerStatus.Failed ? RentLedgerStatus.Failed : RentLedgerStatus.Scheduled;
                entry.UpdatedAt = now;
                return null;

            default:
                return null;
        }
    }

    public async Task CompleteAsync(RentPaymentNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        try
        {
            var entry = await db.RentLedgerEntries
                .Include(e => e.LeaseContract).ThenInclude(l => l.Property)
                .Include(e => e.LeaseContract).ThenInclude(l => l.Parties)
                .Include(e => e.Org)
                .SingleOrDefaultAsync(e => e.Id == notice.InstallmentId, cancellationToken);
            if (entry is null)
                return;

            var propertyName = entry.LeaseContract.Property?.Name ?? string.Empty;
            if (notice.Kind == RentPaymentNoticeKind.Received)
            {
                var content = EmailTemplates.RentPaymentReceived(
                    EmailTemplates.DefaultCulture,
                    propertyName,
                    entry.PeriodStart,
                    entry.PeriodEnd,
                    entry.AmountDue,
                    entry.PaidOn ?? Today,
                    links.HostLease(entry.LeaseContractId));
                foreach (var landlord in Recipients(entry.LeaseContract, PartyRole.Landlord))
                    emailQueue.Enqueue(landlord.ContactEmail, content, EmailTemplates.Names.RentPaymentReceived);
                return;
            }

            if (entry.Status != RentLedgerStatus.Failed)
                return;

            var token = IssueToken(entry);
            await db.SaveChangesAsync(cancellationToken);
            var payUrl = links.RentPayment(entry.Id, token);
            foreach (var tenant in Recipients(entry.LeaseContract, PartyRole.Tenant))
            {
                emailQueue.Enqueue(
                    tenant.ContactEmail,
                    EmailTemplates.RentPaymentFailed(
                        EmailTemplates.DefaultCulture,
                        FullName(tenant),
                        propertyName,
                        entry.PeriodStart,
                        entry.PeriodEnd,
                        entry.AmountDue,
                        payUrl),
                    EmailTemplates.Names.RentPaymentFailed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Emails of rent installment {InstallmentId} ({Kind}) not queued", notice.InstallmentId, notice.Kind);
        }
    }

    // ---------------------------------------------------------------- job

    public async Task<RentCollectionRun> RunCollectionAsync(CancellationToken cancellationToken = default)
    {
        var today = Today;
        var requestUntil = today.AddDays(RentCharges.GetRequestDaysBeforeDue(configuration));

        // Installments coming due, not requested yet, of an active schedule whose org accepts online payments. Only the
        // ones that were not already due when generated: older ones are the landlord's call (mark paid, or send the link).
        var due = await db.RentLedgerEntries
            .AsNoTracking()
            .Where(e => e.Status == RentLedgerStatus.Scheduled &&
                        e.PaymentRequestedAt == null &&
                        e.DueDate <= requestUntil &&
                        e.RentSchedule.IsActive &&
                        e.Org.StripeConnectedAccountId != null &&
                        e.Org.ConnectChargesEnabled &&
                        e.LeaseContract.Status != LeaseStatus.Rejected &&
                        e.LeaseContract.PartiesAnonymizedAt == null)
            .Select(e => new { e.Id, e.LeaseContractId, e.DueDate, e.CreatedAt })
            .ToListAsync(cancellationToken);

        int sent = 0, synchronized = 0, errors = 0;
        foreach (var candidate in due.Where(c => c.DueDate >= RomeCalendar.DateInRome(c.CreatedAt)).OrderBy(c => c.DueDate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await RequestFromJobAsync(candidate.LeaseContractId, candidate.Id, cancellationToken))
                    sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Rent payment request of installment {InstallmentId} failed; the next run retries it", candidate.Id);
            }
        }

        var inFlight = await db.RentLedgerEntries
            .AsNoTracking()
            .Where(e => e.Status == RentLedgerStatus.Processing && e.StripePaymentIntentId != null && e.ConnectedAccountId != null)
            .Select(e => new { e.Id, e.LeaseContractId })
            .ToListAsync(cancellationToken);
        foreach (var candidate in inFlight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await SynchronizeAsync(candidate.LeaseContractId, candidate.Id, cancellationToken))
                    synchronized++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Rent installment {InstallmentId} in flight not read from Stripe; the next run retries it", candidate.Id);
            }
        }

        return new RentCollectionRun(sent, synchronized, errors);
    }

    private async Task<bool> RequestFromJobAsync(Guid leaseId, Guid installmentId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        if (Tenants(lease).Count == 0)
        {
            logger.LogInformation("Rent payment request of installment {InstallmentId} skipped: no tenant email", installmentId);
            return false;
        }

        string token;
        RentInstallmentView view;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
            if (entry.Status != RentLedgerStatus.Scheduled || entry.PaymentRequestedAt is not null)
                return false;

            token = IssueToken(entry);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            view = ToView(entry, Today);
        }

        if (QueuePaymentRequest(lease, view, token))
            return true;

        // Not queued (email provider not configured, queue down): tried again at the next run.
        await ResetRequestAsync(installmentId, cancellationToken);
        return false;
    }

    /// <summary>A payment Stripe reported in flight, read again in case its webhook was lost.</summary>
    private async Task<bool> SynchronizeAsync(Guid leaseId, Guid installmentId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        RentPaymentNotice? notice;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            var entry = await db.RentLedgerEntries.SingleAsync(e => e.Id == installmentId, cancellationToken);
            if (entry.Status != RentLedgerStatus.Processing || entry.StripePaymentIntentId is null || entry.ConnectedAccountId is null)
                return false;

            var current = await stripeService.GetPaymentIntentAsync(entry.StripePaymentIntentId, entry.ConnectedAccountId, cancellationToken);
            if (current.Status == "processing")
                return false;

            notice = ApplyStripeStatus(entry, current);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        if (notice is not null)
            await CompleteAsync(notice, cancellationToken);
        return true;
    }

    // ---------------------------------------------------------------- helpers

    private static bool CanConfigure(LeaseStatus status) =>
        !RliRegistrationDeadline.IsBeforeFullSignature(status) && status != LeaseStatus.Rejected;

    private static bool OnlinePaymentsAvailable(Org org) =>
        !string.IsNullOrWhiteSpace(org.StripeConnectedAccountId) && org.ConnectChargesEnabled;

    /// <summary>An installment the schedule may rewrite: not paid, not in flight, no PaymentIntent of the tenant.</summary>
    private static bool IsRegenerable(RentLedgerEntry entry) =>
        (entry.Status is RentLedgerStatus.Scheduled or RentLedgerStatus.Cancelled) && entry.StripePaymentIntentId is null;

    /// <summary>
    /// Brings the installments in line with <paramref name="plan"/>: a rewritable installment of a planned period is
    /// updated in place (its link keeps working), one of a period no longer planned is removed, a missing period is added.
    /// Installments paid, in flight or with a PaymentIntent are never touched.
    /// </summary>
    private void Regenerate(LeaseContract lease, RentSchedule schedule, List<RentLedgerEntry> entries, RentPlan plan, DateTime now)
    {
        var planned = plan.Installments.ToDictionary(i => i.PeriodStart);
        foreach (var entry in entries.Where(IsRegenerable))
        {
            if (!planned.TryGetValue(entry.PeriodStart, out var installment))
            {
                db.RentLedgerEntries.Remove(entry);
                continue;
            }

            entry.PeriodEnd = installment.PeriodEnd;
            entry.DueDate = installment.DueDate;
            entry.AmountDue = installment.Amount;
            if (entry.Status == RentLedgerStatus.Cancelled)
            {
                // Re-enabled: a new payment request will be sent.
                entry.Status = RentLedgerStatus.Scheduled;
                entry.PaymentRequestedAt = null;
                entry.PaymentTokenHash = null;
            }

            entry.UpdatedAt = now;
        }

        var existing = entries.Select(e => e.PeriodStart).ToHashSet();
        foreach (var installment in plan.Installments.Where(i => !existing.Contains(i.PeriodStart)))
        {
            db.RentLedgerEntries.Add(new RentLedgerEntry
            {
                OrgId = lease.OrgId,
                LeaseContractId = lease.Id,
                RentSchedule = schedule,
                PeriodStart = installment.PeriodStart,
                PeriodEnd = installment.PeriodEnd,
                DueDate = installment.DueDate,
                AmountDue = installment.Amount,
                Status = RentLedgerStatus.Scheduled,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private static void EnsurePayable(RentLedgerEntry entry, RentSchedule schedule)
    {
        switch (entry.Status)
        {
            case RentLedgerStatus.Paid:
                throw AlreadyPaid();
            case RentLedgerStatus.Processing:
                throw InFlight();
            case RentLedgerStatus.Cancelled:
                throw NotPayable();
        }

        if (!schedule.IsActive)
            throw NotPayable();
    }

    private static PublicRentPaymentState PublicStateOf(RentLedgerEntry entry) => entry.Status switch
    {
        RentLedgerStatus.Paid => PublicRentPaymentState.Paid,
        RentLedgerStatus.Processing => PublicRentPaymentState.Processing,
        RentLedgerStatus.Scheduled or RentLedgerStatus.Failed
            when entry.RentSchedule.IsActive && OnlinePaymentsAvailable(entry.Org) => PublicRentPaymentState.Payable,
        _ => PublicRentPaymentState.Unavailable,
    };

    /// <summary>
    /// The installment's PaymentIntent canceled when the tenant could still pay it; otherwise its current state on Stripe
    /// (succeeded, processing, already canceled). A payment completed between the read and the cancellation wins.
    /// </summary>
    private async Task<PaymentIntent> CancelIfPayableAsync(RentLedgerEntry entry, CancellationToken cancellationToken)
    {
        var paymentIntentId = entry.StripePaymentIntentId!;
        var account = entry.ConnectedAccountId!;
        var current = await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken);
        if (current.Status is null || !PayableStatuses.Contains(current.Status))
            return current;

        try
        {
            return await stripeService.CancelPaymentIntentAsync(
                paymentIntentId,
                account,
                RentCharges.CancellationIdempotencyKey(entry.Id, paymentIntentId),
                cancellationToken);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == PaymentIntentUnexpectedStateCode)
        {
            return await stripeService.GetPaymentIntentAsync(paymentIntentId, account, cancellationToken);
        }
    }

    /// <summary>Records the state Stripe reports for the installment's current PaymentIntent (read, not from an event).</summary>
    private RentPaymentNotice? ApplyStripeStatus(RentLedgerEntry entry, PaymentIntent paymentIntent)
    {
        var eventType = paymentIntent.Status switch
        {
            "succeeded" => "payment_intent.succeeded",
            "processing" => "payment_intent.processing",
            "canceled" => "payment_intent.canceled",
            "requires_payment_method" when paymentIntent.LastPaymentError is not null => "payment_intent.payment_failed",
            _ => null,
        };
        return eventType is null
            ? null
            : ApplyEvent(entry, new RentPaymentIntentEvent(
                paymentIntent.Id,
                eventType,
                entry.ConnectedAccountId,
                entry.Id,
                paymentIntent.Amount,
                paymentIntent.LastPaymentError?.Code));
    }

    private string IssueToken(RentLedgerEntry entry)
    {
        var token = CheckoutOutcomes.NewToken();
        entry.PaymentTokenHash = CheckoutOutcomes.HashToken(token);
        entry.PaymentRequestedAt = UtcNow;
        entry.UpdatedAt = UtcNow;
        return token;
    }

    /// <summary>Queues the payment request to every tenant; true when at least one email was queued.</summary>
    private bool QueuePaymentRequest(LeaseContract lease, RentInstallmentView installment, string token)
    {
        var payUrl = links.RentPayment(installment.Id, token);
        var queued = false;
        foreach (var tenant in Tenants(lease))
        {
            var content = EmailTemplates.RentPaymentRequest(
                EmailTemplates.DefaultCulture,
                FullName(tenant),
                lease.Property?.Name ?? string.Empty,
                lease.Org.Name,
                installment.PeriodStart,
                installment.PeriodEnd,
                installment.DueDate,
                installment.Amount,
                payUrl);
            queued |= emailQueue.Enqueue(tenant.ContactEmail, content, EmailTemplates.Names.RentPaymentRequest);
        }

        return queued;
    }

    private async Task ResetRequestAsync(Guid installmentId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var entry = await db.RentLedgerEntries.SingleAsync(e => e.Id == installmentId, cancellationToken);
        entry.PaymentRequestedAt = null;
        entry.PaymentTokenHash = null;
        entry.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<Party> Tenants(LeaseContract lease) => Recipients(lease, PartyRole.Tenant);

    /// <summary>Parties of <paramref name="role"/> with an address, not anonymized (LT-12), one per address.</summary>
    private static List<Party> Recipients(LeaseContract lease, PartyRole role) =>
        lease.Parties
            .Where(p => p.Role == role && p.AnonymizedAt == null && !string.IsNullOrWhiteSpace(p.ContactEmail))
            .OrderBy(p => p.Position)
            .DistinctBy(p => p.ContactEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FullName(Party party) => $"{party.FirstName} {party.LastName}".Trim();

    /// <summary>The lease with org, property, parties and schedule, read-only (the tenant filter scopes it).</summary>
    private async Task<LeaseContract> LoadLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Org)
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.RentSchedule)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken);
        return lease ?? throw new NotFoundException("Lease not found") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };
    }

    private async Task<RentLedgerEntry> FindEntryAsync(Guid leaseId, Guid installmentId, CancellationToken cancellationToken)
    {
        var entry = await db.RentLedgerEntries
            .SingleOrDefaultAsync(e => e.Id == installmentId && e.LeaseContractId == leaseId, cancellationToken)
            ?? throw new NotFoundException("Rent installment not found")
            {
                Code = RentBillingErrorCodes.InstallmentNotFound,
                MessageKey = "RentInstallmentNotFound",
            };
        // Read again under the lock (the entity may have been tracked before it).
        await db.Entry(entry).ReloadAsync(cancellationToken);
        return entry;
    }

    /// <summary>Anonymous: the token is the only access check; a wrong id or token get the same 404.</summary>
    private async Task<RentLedgerEntry> LoadByTokenAsync(Guid installmentId, string token, CancellationToken cancellationToken)
    {
        var entry = await db.RentLedgerEntries
            .AsNoTracking()
            .Include(e => e.LeaseContract).ThenInclude(l => l.Property)
            .Include(e => e.Org)
            .Include(e => e.RentSchedule)
            .FirstOrDefaultAsync(e => e.Id == installmentId, cancellationToken);
        if (entry is null || !CheckoutOutcomes.TokenMatches(entry.PaymentTokenHash, token))
            throw LinkInvalid();
        return entry;
    }

    private Task<IDbContextTransaction?> LockLeaseAsync(Guid leaseId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.RentLease, leaseId.ToString("N")));

    private RentLedgerView BuildView(LeaseContract lease, IReadOnlyList<RentLedgerEntry> entries)
    {
        var schedule = lease.RentSchedule;
        RentPeriod? partial = null;
        if (schedule is not null && RentInstallmentPlan.IsValidBillingDay(schedule.BillingDayOfMonth) && schedule.Amount > 0)
        {
            var start = RomeCalendar.DateInRome(lease.StartDate);
            var end = RomeCalendar.DateInRome(lease.EndDate);
            if (end >= start)
                partial = RentInstallmentPlan.Build(start, end, schedule.Cadence, schedule.BillingDayOfMonth, schedule.Amount).PartialFinalPeriod;
        }

        var today = Today;
        return new RentLedgerView(
            lease.Id,
            lease.MonthlyRent,
            CanConfigure(lease.Status),
            OnlinePaymentsAvailable(lease.Org),
            Tenants(lease).Count > 0,
            schedule is null
                ? null
                : new RentScheduleView(schedule.Cadence, schedule.BillingDayOfMonth, schedule.Amount, schedule.Currency.ToUpperInvariant(), schedule.IsActive),
            entries.Select(e => ToView(e, today)).ToList(),
            partial);
    }

    private static RentInstallmentView ToView(RentLedgerEntry entry, DateOnly today) => new(
        entry.Id,
        entry.PeriodStart,
        entry.PeriodEnd,
        entry.DueDate,
        entry.AmountDue,
        RentCharges.Currency.ToUpperInvariant(),
        entry.Status,
        RentInstallmentRules.IsOverdue(entry.Status, entry.DueDate, today),
        entry.PaidVia,
        entry.PaidOn,
        entry.OfflinePaymentNote,
        entry.PaymentRequestedAt,
        entry.FailureCode,
        entry.LastFailedAt);

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static NotFoundException ScheduleNotFound() =>
        new("Rent schedule not found") { Code = RentBillingErrorCodes.ScheduleNotFound, MessageKey = "RentScheduleNotFound" };

    private static NotFoundException LinkInvalid() =>
        new("Rent payment link does not match an installment")
        {
            Code = RentBillingErrorCodes.PaymentLinkInvalid,
            MessageKey = "RentPaymentLinkInvalid",
        };

    private static DomainConflictException AlreadyPaid() =>
        new(RentBillingErrorCodes.InstallmentNotPayable, "RentInstallmentAlreadyPaid");

    private static DomainConflictException InFlight() =>
        new(RentBillingErrorCodes.InstallmentInFlight, "RentInstallmentInFlight");

    private static DomainConflictException NotPayable() =>
        new(RentBillingErrorCodes.InstallmentNotPayable, "RentInstallmentNotPayable");
}
