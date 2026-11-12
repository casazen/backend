using System.Collections.Concurrent;
using System.Text.Json;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Maintenance the tenant of a lease asks for. The org boundary is the lease's <see cref="LeaseContract.OrgId"/>
/// (the same filter as the rest of the lease module). Each change appends a <see cref="LeaseEvent"/> with the request
/// id and the outcome, never the text of the request or a name. Payment reuses the euro SEPA debit of a linked
/// Financial Connections account: the request stays unpaid until that PaymentIntent succeeds.
/// </summary>
public sealed class LeaseMaintenanceService(
    AppDbContext db,
    IStripeBankLinkGateway gateway,
    IFileStorage storage,
    ILogger<LeaseMaintenanceService> logger) : ILeaseMaintenanceService
{
    private const long MaxAmountCents = 100_000_000;

    /// <summary>Same-process gate for one request. PostgreSQL also takes <see cref="PostgresAdvisoryLocks.Scope.MaintenancePayment"/>.</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> PayGates = new();

    private static readonly JsonSerializerOptions PhotosJson = new(JsonSerializerDefaults.Web);

    public async Task<LeaseMaintenanceAccess?> FindLeaseAsync(Guid orgId, Guid leaseId, CancellationToken cancellationToken = default)
    {
        var row = await db.LeaseContracts.AsNoTracking()
            .Where(l => l.Id == leaseId && l.OrgId == orgId)
            .Select(l => new { l.Id, l.OrgId, l.PropertyId, OwnerId = l.Property.OwnerId })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new LeaseMaintenanceAccess(row.Id, row.OrgId, row.PropertyId, row.OwnerId);
    }

    public async Task<LeaseMaintenanceRequestView> CreateAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, CreateLeaseMaintenanceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var lease = await RequireLeaseAsync(caller.OrgId, leaseId, cancellationToken);
        await RequireTenantAsync(lease, caller, cancellationToken);

        if (command.Photos.Count > LeaseMaintenanceRules.MaxPhotos)
            throw new DomainRuleException("maintenance_too_many_photos", "MaintenanceTooManyPhotos");

        var title = LeaseMaintenanceRules.CleanTitle(command.Title);
        var description = LeaseMaintenanceRules.CleanDescription(command.Description);
        var now = DateTime.UtcNow;
        var request = new LeaseMaintenanceRequest
        {
            OrgId = lease.OrgId,
            LeaseContractId = lease.Id,
            Title = title,
            Description = description,
            Urgency = command.Urgency,
            Status = LeaseMaintenanceStatus.InAttesa,
            CreatedByUserId = caller.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var stored = new List<StoredPhoto>();
        foreach (var photo in command.Photos)
        {
            var header = LeaseMaintenanceRules.ReadPhoto(photo.Content);
            var id = Guid.NewGuid();
            var key = StorageKeys.MaintenancePhoto(lease.OrgId, lease.Id, request.Id, $"{id:N}{header.Extension}");
            await using var stream = new MemoryStream(photo.Content, writable: false);
            await storage.PutAsync(StorageBucket.Private, key, stream, header.ContentType, cancellationToken);
            stored.Add(new StoredPhoto(id, SafeFileName(photo.FileName, header), header.ContentType, key));
        }

        request.PhotosJson = JsonSerializer.Serialize(stored, PhotosJson);
        db.LeaseMaintenanceRequests.Add(request);
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.MaintenanceRequested,
            OccurredAt = now,
            Payload = $"{request.Id:N}:{request.Urgency}",
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Maintenance request {RequestId} opened on lease {LeaseId} of org {OrgId}",
            request.Id, lease.Id, lease.OrgId);
        return ToView(request);
    }

    public async Task<LeaseMaintenanceListView> ListAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await RequireLeaseAsync(caller.OrgId, leaseId, cancellationToken);
        if (!caller.CanReadLease && !await IsTenantAsync(lease, caller, cancellationToken))
            throw new DomainForbiddenException("maintenance_forbidden", "MaintenanceForbidden");

        var rows = await db.LeaseMaintenanceRequests.AsNoTracking()
            .Where(r => r.LeaseContractId == lease.Id && r.OrgId == lease.OrgId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return new LeaseMaintenanceListView(rows.Select(ToView).ToList());
    }

    public async Task<LeaseMaintenancePhotoFile> ReadPhotoAsync(
        LeaseMaintenanceCaller caller, Guid leaseId, Guid requestId, Guid photoId, CancellationToken cancellationToken = default)
    {
        var lease = await RequireLeaseAsync(caller.OrgId, leaseId, cancellationToken);
        if (!caller.CanReadLease && !await IsTenantAsync(lease, caller, cancellationToken))
            throw new DomainForbiddenException("maintenance_forbidden", "MaintenanceForbidden");

        var request = await RequireRequestAsync(lease, requestId, cancellationToken);
        var photos = JsonSerializer.Deserialize<List<StoredPhoto>>(request.PhotosJson, PhotosJson) ?? [];
        var photo = photos.SingleOrDefault(p => p.Id == photoId)
            ?? throw new NotFoundException("Maintenance photo not found")
            {
                Code = "maintenance_photo_not_found",
                MessageKey = "MaintenancePhotoNotFound",
            };
        var stream = await storage.OpenReadAsync(StorageBucket.Private, photo.StorageKey, cancellationToken)
            ?? throw new NotFoundException("Maintenance photo not found")
            {
                Code = "maintenance_photo_not_found",
                MessageKey = "MaintenancePhotoNotFound",
            };
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return new LeaseMaintenancePhotoFile(photo.FileName, photo.ContentType, buffer.ToArray());
        }
    }

    public async Task<LeaseMaintenanceRequestView> DecideAsync(
        LeaseMaintenanceCaller caller,
        Guid leaseId,
        Guid requestId,
        LeaseMaintenanceDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!caller.CanManageLease)
            throw new DomainForbiddenException("maintenance_forbidden", "MaintenanceForbidden");

        var lease = await RequireLeaseAsync(caller.OrgId, leaseId, cancellationToken);
        var request = await RequireRequestAsync(lease, requestId, cancellationToken);
        if (request.Status != LeaseMaintenanceStatus.InAttesa)
            throw new DomainConflictException("maintenance_already_decided", "MaintenanceAlreadyDecided");

        var now = DateTime.UtcNow;
        switch (command.Decision)
        {
            case LeaseMaintenanceDecision.Rejected:
                var reason = LeaseMaintenanceRules.CleanReason(command.Reason)
                    ?? throw new DomainRuleException("maintenance_reason_required", "MaintenanceReasonRequired");
                request.Status = LeaseMaintenanceStatus.Rifiutata;
                request.RejectionReason = reason;
                break;
            case LeaseMaintenanceDecision.Approved:
                if (command.AmountCents is not long cents || cents is <= 0 or > MaxAmountCents)
                    throw new DomainRuleException(BankLinkErrorCodes.AmountInvalid, "BankLinkAmountInvalid");
                request.Status = LeaseMaintenanceStatus.Approvata;
                request.AmountCents = cents;
                request.Currency = BankLinkCharges.Currency;
                request.RejectionReason = null;
                break;
            default:
                throw new DomainRuleException("maintenance_decision_invalid", "MaintenanceDecisionInvalid");
        }

        request.DecidedByUserId = caller.UserId;
        request.DecidedAt = now;
        request.UpdatedAt = now;
        request.PaidAt = null;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.MaintenanceDecided,
            OccurredAt = now,
            Payload = $"{request.Id:N}:{command.Decision}",
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Maintenance request {RequestId} of lease {LeaseId} decided {Decision} by user {UserId}",
            request.Id, lease.Id, command.Decision, caller.UserId);
        return ToView(request);
    }

    public async Task<LeaseMaintenancePaymentView> PayAsync(
        LeaseMaintenanceCaller caller,
        Guid leaseId,
        Guid requestId,
        LeaseMaintenancePaymentCommand command,
        BankLinkCaller bankCaller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(bankCaller);
        if (!caller.CanDebitOrg)
            throw new DomainForbiddenException("maintenance_forbidden", "MaintenanceForbidden");

        var lease = await RequireLeaseAsync(caller.OrgId, leaseId, cancellationToken);
        var request = await RequireRequestAsync(lease, requestId, cancellationToken);
        if (request.Status == LeaseMaintenanceStatus.Pagata)
            return await ExistingPaymentAsync(request, cancellationToken);

        LeaseMaintenanceRules.EnsurePayable(request.Status);
        if (request.Status != LeaseMaintenanceStatus.Approvata)
            throw new DomainConflictException("maintenance_not_payable", "MaintenanceNotPayable");
        if (request.AmountCents is not long stored || command.AmountCents != stored)
            throw new DomainRuleException("maintenance_amount_mismatch", "MaintenanceAmountMismatch");

        var gate = PayGates.GetOrAdd(request.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, (PostgresAdvisoryLocks.Scope.MaintenancePayment, request.Id.ToString("N")));
            await db.Entry(request).ReloadAsync(cancellationToken);
            if (request.Status == LeaseMaintenanceStatus.Pagata)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return await ExistingPaymentAsync(request, cancellationToken);
            }

            LeaseMaintenanceRules.EnsurePayable(request.Status);
            if (request.Status != LeaseMaintenanceStatus.Approvata)
                throw new DomainConflictException("maintenance_not_payable", "MaintenanceNotPayable");
            if (request.AmountCents is not long lockedAmount || command.AmountCents != lockedAmount)
                throw new DomainRuleException("maintenance_amount_mismatch", "MaintenanceAmountMismatch");

            var open = await OpenPaymentAsync(request, cancellationToken);
            if (open?.Status == BankLinkPaymentStatus.Succeeded)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return ToPayment(request, open, clientSecret: null, stripeStatus: "succeeded");
            }

            if (open?.Status == BankLinkPaymentStatus.Processing)
            {
                var openKey = DebitKey(request.Id, await FailedAttemptsAsync(request, cancellationToken));
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                var again = await gateway.ConfirmDebitAsync(
                    open.StripePaymentIntentId, openKey, bankCaller.IpAddress, bankCaller.UserAgent, cancellationToken);
                return ToPayment(request, open, again.ClientSecret, again.Status);
            }

            var org = await db.Orgs.SingleAsync(o => o.Id == lease.OrgId, cancellationToken);
            if (string.IsNullOrWhiteSpace(org.StripeCustomerId) || string.IsNullOrWhiteSpace(org.FinancialConnectionsAccountId))
                throw new DomainRuleException(BankLinkErrorCodes.NotLinked, "BankLinkNotLinked");

            var linked = await gateway.GetAccountAsync(org.FinancialConnectionsAccountId, cancellationToken);
            if (!string.Equals(linked.CustomerId, org.StripeCustomerId, StringComparison.Ordinal))
                throw new DomainRuleException(BankLinkErrorCodes.AccountHolderMismatch, "BankLinkAccountHolderMismatch");

            var idempotencyKey = DebitKey(request.Id, await FailedAttemptsAsync(request, cancellationToken));
            var created = await gateway.CreateDebitAsync(
                new BankDebitCommand(
                    org.StripeCustomerId,
                    linked.Id,
                    linked.SupportedPaymentMethodTypes,
                    lockedAmount,
                    BankLinkCharges.Currency,
                    idempotencyKey,
                    org.DisplayName,
                    bankCaller.IpAddress,
                    bankCaller.UserAgent,
                    new Dictionary<string, string>
                    {
                        ["kind"] = BankLinkCharges.Kind,
                        ["orgId"] = lease.OrgId.ToString(),
                        ["leaseId"] = lease.Id.ToString(),
                        ["payer"] = BankLinkPayer.Landlord.ToString().ToLowerInvariant(),
                        ["maintenanceRequestId"] = request.Id.ToString(),
                    }),
                cancellationToken);

            var row = await db.BankLinkPayments
                .SingleOrDefaultAsync(p => p.StripePaymentIntentId == created.PaymentIntentId, cancellationToken);
            if (row is { Status: BankLinkPaymentStatus.Failed or BankLinkPaymentStatus.Canceled })
                throw new DomainConflictException("maintenance_not_payable", "MaintenanceNotPayable");

            if (row is null)
            {
                var now = DateTime.UtcNow;
                row = new BankLinkPayment
                {
                    OrgId = lease.OrgId,
                    LeaseContractId = lease.Id,
                    LeaseMaintenanceRequestId = request.Id,
                    Payer = BankLinkPayer.Landlord,
                    FinancialConnectionsAccountId = linked.Id,
                    StripePaymentIntentId = created.PaymentIntentId,
                    StripePaymentMethodId = created.PaymentMethodId,
                    PaymentMethodType = created.PaymentMethodType,
                    AmountCents = lockedAmount,
                    Currency = BankLinkCharges.Currency,
                    Status = BankLinkPaymentStatus.Processing,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.BankLinkPayments.Add(row);
                request.BankLinkPaymentId = row.Id;
                request.UpdatedAt = now;
                db.LeaseEvents.Add(new LeaseEvent
                {
                    LeaseContractId = lease.Id,
                    EventType = LeaseEventType.MaintenancePaymentStarted,
                    OccurredAt = now,
                    Payload = $"{request.Id:N}:{lockedAmount}",
                });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    if (transaction is not null)
                        await transaction.RollbackAsync(cancellationToken);
                    var winner = await OpenPaymentAsync(request, cancellationToken)
                        ?? throw new DomainConflictException("maintenance_not_payable", "MaintenanceNotPayable");
                    var winnerKey = DebitKey(request.Id, await FailedAttemptsAsync(request, cancellationToken));
                    var joined = await gateway.ConfirmDebitAsync(
                        winner.StripePaymentIntentId, winnerKey, bankCaller.IpAddress, bankCaller.UserAgent, cancellationToken);
                    return ToPayment(request, winner, joined.ClientSecret, joined.Status);
                }
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            var confirmed = await gateway.ConfirmDebitAsync(
                row.StripePaymentIntentId, idempotencyKey, bankCaller.IpAddress, bankCaller.UserAgent, cancellationToken);
            logger.LogInformation(
                "Maintenance payment {PaymentId} started for request {RequestId} ({PaymentIntentId})",
                row.Id, request.Id, confirmed.PaymentIntentId);
            return ToPayment(request, row, confirmed.ClientSecret, confirmed.Status);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stable for one attempt of a request: <c>maint-{requestId}</c>, then <c>maint-{requestId}-{n}</c> after
    /// <paramref name="failedAttempts"/> failed or canceled rows are already stored. Not a fresh id per call.
    /// </summary>
    private static string DebitKey(Guid requestId, int failedAttempts) =>
        failedAttempts == 0 ? $"maint-{requestId:N}" : $"maint-{requestId:N}-{failedAttempts}";

    private Task<int> FailedAttemptsAsync(LeaseMaintenanceRequest request, CancellationToken cancellationToken) =>
        db.BankLinkPayments.CountAsync(
            p => p.LeaseMaintenanceRequestId == request.Id
                && p.OrgId == request.OrgId
                && (p.Status == BankLinkPaymentStatus.Failed || p.Status == BankLinkPaymentStatus.Canceled),
            cancellationToken);

    public async Task MarkPaidFromBankAsync(
        Guid orgId, Guid requestId, long amountCents, CancellationToken cancellationToken = default)
    {
        var request = await db.LeaseMaintenanceRequests
            .SingleOrDefaultAsync(r => r.Id == requestId && r.OrgId == orgId, cancellationToken);
        if (request is null || request.Status == LeaseMaintenanceStatus.Pagata)
            return;
        if (request.Status != LeaseMaintenanceStatus.Approvata)
            return;

        var now = DateTime.UtcNow;
        request.Status = LeaseMaintenanceStatus.Pagata;
        request.AmountCents = amountCents;
        request.PaidAt = now;
        request.UpdatedAt = now;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = request.LeaseContractId,
            EventType = LeaseEventType.MaintenancePaid,
            OccurredAt = now,
            Payload = $"{request.Id:N}:{amountCents}",
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Maintenance request {RequestId} of org {OrgId} paid by bank debit", request.Id, orgId);
    }

    private async Task<LeaseMaintenancePaymentView> ExistingPaymentAsync(
        LeaseMaintenanceRequest request, CancellationToken cancellationToken)
    {
        var row = await OpenPaymentAsync(request, cancellationToken)
            ?? throw new DomainConflictException("maintenance_not_payable", "MaintenanceNotPayable");
        return ToPayment(request, row, clientSecret: null, stripeStatus: StripeStatus(row));
    }

    private async Task<BankLinkPayment?> OpenPaymentAsync(LeaseMaintenanceRequest request, CancellationToken cancellationToken) =>
        await db.BankLinkPayments
            .Where(p => p.LeaseMaintenanceRequestId == request.Id
                && p.OrgId == request.OrgId
                && (p.Status == BankLinkPaymentStatus.Processing || p.Status == BankLinkPaymentStatus.Succeeded))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<LeaseContract> RequireLeaseAsync(Guid orgId, Guid leaseId, CancellationToken cancellationToken) =>
        await db.LeaseContracts.SingleOrDefaultAsync(l => l.Id == leaseId && l.OrgId == orgId, cancellationToken)
        ?? throw new NotFoundException("Lease not found") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };

    private async Task<LeaseMaintenanceRequest> RequireRequestAsync(
        LeaseContract lease, Guid requestId, CancellationToken cancellationToken) =>
        await db.LeaseMaintenanceRequests.SingleOrDefaultAsync(
            r => r.Id == requestId && r.LeaseContractId == lease.Id && r.OrgId == lease.OrgId, cancellationToken)
        ?? throw new NotFoundException("Maintenance request not found")
        {
            Code = "maintenance_request_not_found",
            MessageKey = "MaintenanceRequestNotFound",
        };

    private async Task RequireTenantAsync(LeaseContract lease, LeaseMaintenanceCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.EmailVerified || string.IsNullOrWhiteSpace(caller.Email))
            throw new DomainForbiddenException("maintenance_email_not_verified", "MaintenanceEmailNotVerified");
        if (!await IsTenantAsync(lease, caller, cancellationToken))
            throw new DomainForbiddenException("maintenance_not_tenant", "MaintenanceNotTenant");
    }

    private async Task<bool> IsTenantAsync(LeaseContract lease, LeaseMaintenanceCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.EmailVerified || string.IsNullOrWhiteSpace(caller.Email))
            return false;
        var email = caller.Email.Trim();
        var emails = await db.Parties.AsNoTracking()
            .Where(p => p.LeaseContractId == lease.Id && p.Role == PartyRole.Tenant && p.AnonymizedAt == null)
            .Select(p => p.ContactEmail)
            .ToListAsync(cancellationToken);
        return emails.Any(existing => string.Equals(existing.Trim(), email, StringComparison.OrdinalIgnoreCase));
    }

    private static LeaseMaintenanceRequestView ToView(LeaseMaintenanceRequest request)
    {
        var photos = JsonSerializer.Deserialize<List<StoredPhoto>>(request.PhotosJson, PhotosJson) ?? [];
        return new LeaseMaintenanceRequestView(
            request.Id,
            request.LeaseContractId,
            request.Title,
            request.Description,
            request.Urgency,
            request.Status,
            request.RejectionReason,
            request.Status == LeaseMaintenanceStatus.Pagata,
            request.AmountCents,
            request.Currency,
            photos.Select(p => new LeaseMaintenancePhotoView(p.Id, p.FileName, p.ContentType)).ToList(),
            request.CreatedAt,
            request.DecidedAt);
    }

    private static LeaseMaintenancePaymentView ToPayment(
        LeaseMaintenanceRequest request, BankLinkPayment row, string? clientSecret, string stripeStatus) =>
        new(
            request.Id,
            request.Status,
            request.Status == LeaseMaintenanceStatus.Pagata,
            row.Id,
            row.StripePaymentIntentId,
            clientSecret,
            row.AmountCents,
            row.Currency,
            stripeStatus);

    private static string StripeStatus(BankLinkPayment row) => row.Status switch
    {
        BankLinkPaymentStatus.Succeeded => "succeeded",
        BankLinkPaymentStatus.Failed => "failed",
        BankLinkPaymentStatus.Canceled => "canceled",
        _ => "processing",
    };

    private static string SafeFileName(string? fileName, ImageHeaderInfo header)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 120)
            name = $"foto{header.Extension}";
        return name;
    }

    private sealed record StoredPhoto(Guid Id, string FileName, string ContentType, string StorageKey);
}
