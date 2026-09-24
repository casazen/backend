using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Service requests between a host org and a supplier org. Who may call each operation is decided by the web layer
/// (policies and the host resource handler, TN-3); this service only enforces the org boundaries it is given
/// (<see cref="HostScope"/>, host org id, supplier org id) and the state machine.
/// </summary>
public class ServiceRequestService(
    AppDbContext db,
    IServiceRequestRepository repository,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IPushNotificationService pushNotificationService,
    ILogger<ServiceRequestService> logger) : IServiceRequestService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ServiceRequest> CreateAsync(
        CreateServiceRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        // Only category codes are stored (SU-03); anything else is a 422 before any lookup.
        var category = ServiceCategories.Require(command.Category);

        // IgnoreQueryFilters: scoped by the explicit OrgId check below. command.OrgId comes from
        // OrgContextResolver, which may provision the org after the tenant filter cached a null org.
        var property = await db.Properties
            .IgnoreQueryFilters()
            .Include(p => p.Org)
            .FirstOrDefaultAsync(p => p.Id == command.PropertyId, cancellationToken);

        // Another org's property is answered exactly like a missing one.
        if (property is null || property.OrgId != command.OrgId)
        {
            throw new NotFoundException($"Property {command.PropertyId} not found for the service request")
            {
                Code = ServiceRequestErrorCodes.PropertyNotFound,
                MessageKey = ServiceRequestErrorCodes.PropertyNotFoundMessageKey,
            };
        }

        await EnsureBookingRuleAsync(command, cancellationToken);

        if (command.ChargeToGuest)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.ChargeToGuestNotAllowed, ServiceRequestErrorCodes.ChargeToGuestNotAllowedMessageKey);
        }

        var supplier = await db.SupplierProfiles
            .Include(sp => sp.Org)
            .FirstOrDefaultAsync(sp => sp.OrgId == command.SupplierOrgId, cancellationToken)
            ?? throw new NotFoundException($"Supplier {command.SupplierOrgId} not found")
            {
                Code = ServiceRequestErrorCodes.SupplierNotFound,
                MessageKey = ServiceRequestErrorCodes.SupplierNotFoundMessageKey,
            };

        if (supplier.Status != SupplierStatus.Active)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey);
        }

        var comuni = JsonSerializer.Deserialize<string[]>(supplier.ComuniJson, JsonOpts) ?? [];
        if (!comuni.Any(c => ItalianComuneRegistry.Matches(property.City, c)))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierOutsideComune, ServiceRequestErrorCodes.SupplierOutsideComuneMessageKey);
        }

        var request = new ServiceRequest
        {
            OrgId = command.OrgId,
            BookingId = command.BookingId,
            RentalContext = command.RentalContext,
            PropertyId = command.PropertyId,
            SupplierOrgId = command.SupplierOrgId,
            Category = category,
            Urgency = command.Urgency,
            Notes = command.Notes?.Trim() ?? string.Empty,
            ChargeToGuest = command.ChargeToGuest,
            Status = ServiceRequestStatus.Richiesto,
        };

        // Rendered before saving: a missing App:PublicSiteBaseUrl is a configuration error, not a wrong link.
        var supplierEmail = EmailTemplates.ServiceRequestCreated(
            EmailTemplates.DefaultCulture,
            supplier.LegalName,
            request.Category,
            property.Name,
            request.Notes,
            publicSiteLinks.SupplierInbox());

        await repository.AddAsync(request, cancellationToken);

        emailQueue.Enqueue(supplier.Email, supplierEmail, EmailTemplates.Names.ServiceRequestCreated);

        logger.LogInformation(
            "ServiceRequest {Id} ({RentalContext}) created by {UserId} for property {PropertyId} booking {BookingId} supplier {SupplierOrgId}",
            request.Id, request.RentalContext, command.UserId, request.PropertyId, request.BookingId, request.SupplierOrgId);

        return (await repository.GetByIdAsync(request.Id, cancellationToken))!;
    }

    /// <summary>
    /// D2 (SU-07): a short-rent request is for one stay, a booking of the request's property in the host's org; a
    /// long-rent request is for the property and takes no booking. 422 with the codes of
    /// <see cref="ServiceRequestErrorCodes"/>; a booking of another property or org answers like a missing one.
    /// </summary>
    private async Task EnsureBookingRuleAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken)
    {
        if (command.RentalContext == ServiceRequestRentalContext.LongRent)
        {
            if (command.BookingId is not null)
            {
                throw new DomainRuleException(
                    ServiceRequestErrorCodes.BookingNotAllowed, ServiceRequestErrorCodes.BookingNotAllowedMessageKey);
            }

            return;
        }

        if (command.BookingId is not { } bookingId)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.BookingRequired, ServiceRequestErrorCodes.BookingRequiredMessageKey);
        }

        // IgnoreQueryFilters: scoped by the explicit OrgId predicate (same reason as the property lookup above).
        var belongsToProperty = await db.Bookings
            .IgnoreQueryFilters()
            .AnyAsync(
                b => b.Id == bookingId && b.PropertyId == command.PropertyId && b.OrgId == command.OrgId,
                cancellationToken);

        if (!belongsToProperty)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.BookingMismatch, ServiceRequestErrorCodes.BookingMismatchMessageKey);
        }
    }

    public async Task<ServiceRequest> TakeAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetSupplierRequestOrThrow(id, supplierOrgId, cancellationToken);

        await TransitionAsync(
            request,
            ServiceRequestStatus.PresoInCarico,
            ServiceRequestErrorCodes.CannotTakeMessageKey,
            r =>
            {
                r.TakenAt = DateTime.UtcNow;
                r.TakenByUserId = userId;
            },
            cancellationToken);

        await NotifyHostAsync(request, "presa in carico", cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> CompleteAsync(
        Guid id,
        Guid supplierOrgId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var request = await GetSupplierRequestOrThrow(id, supplierOrgId, cancellationToken);

        await TransitionAsync(
            request,
            ServiceRequestStatus.Completato,
            ServiceRequestErrorCodes.CannotCompleteMessageKey,
            r =>
            {
                if (!string.IsNullOrWhiteSpace(notes))
                    r.Notes = notes.Trim();
                r.CompletedAt = DateTime.UtcNow;
            },
            cancellationToken);

        await NotifyHostAsync(request, "completata", cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> RejectAsync(
        Guid id,
        Guid supplierOrgId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // The API requires a reason of at most 500 characters (400 validation_error, A4-18): a blank one is a bug here.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = await GetSupplierRequestOrThrow(id, supplierOrgId, cancellationToken);

        await TransitionAsync(
            request,
            ServiceRequestStatus.Rifiutato,
            ServiceRequestErrorCodes.CannotRejectMessageKey,
            r => r.RejectionReason = reason.Trim(),
            cancellationToken);

        await QueueHostStatusEmailAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> MarkPaidAsync(
        Guid id,
        Guid hostOrgId,
        CancellationToken cancellationToken = default)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken);

        // Another org's request is answered exactly like a missing one.
        if (request is null || request.OrgId != hostOrgId)
            throw RequestNotFound(id);

        await TransitionAsync(
            request,
            ServiceRequestStatus.Pagato,
            ServiceRequestErrorCodes.CannotMarkPaidMessageKey,
            r => r.PaidAt = DateTime.UtcNow,
            cancellationToken);

        return request;
    }

    /// <summary>
    /// Moves <paramref name="request"/> to <paramref name="to"/> when <see cref="ServiceRequestStateMachine"/> allows it
    /// (422 <see cref="ServiceRequestErrorCodes.InvalidTransition"/> otherwise), and saves it only if nobody changed the
    /// request since it was read (<c>xmin</c>, A4-19). The loser of two concurrent transitions gets 409
    /// <see cref="ServiceRequestErrorCodes.StateChanged"/>: nothing is saved for it, and since the callers notify only
    /// after this returns, only the winner's email and push are sent.
    /// </summary>
    private async Task TransitionAsync(
        ServiceRequest request,
        ServiceRequestStatus to,
        string refusedMessageKey,
        Action<ServiceRequest> apply,
        CancellationToken cancellationToken)
    {
        var from = request.Status;
        if (!ServiceRequestStateMachine.CanTransition(from, to))
            throw new DomainRuleException(ServiceRequestErrorCodes.InvalidTransition, refusedMessageKey);

        apply(request);
        request.Status = to;
        request.UpdatedAt = DateTime.UtcNow;

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "ServiceRequest {Id}: transition {From} -> {To} refused, the request changed since it was read",
                request.Id, from, to);
            throw new DomainConflictException(ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.StateChangedMessageKey);
        }

        logger.LogInformation("ServiceRequest {Id}: {From} -> {To}", request.Id, from, to);
    }

    public Task<ServiceRequest?> GetByIdForHostAsync(
        Guid id,
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        CancellationToken cancellationToken = default)
    {
        // ServiceRequest is not tenant-filtered (two parties, see the TN-2 allow-list); host and supplier
        // reads are scoped by the explicit OrgId / SupplierOrgId predicate, never by the included Property.
        var query = ApplyHostScope(
            db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .Where(r => r.Id == id && r.RentalContext == rentalContext),
            scope);

        return query.FirstOrDefaultAsync(cancellationToken);
    }

    // IgnoreQueryFilters: the supplier reads the host's Property (another org) through the request;
    // scoped by the explicit SupplierOrgId predicate.
    public Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id && r.SupplierOrgId == supplierOrgId, cancellationToken);

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        ServiceRequestStatus? status,
        Guid? propertyId,
        Guid? bookingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: scoped by the explicit host OrgId predicate (see GetByIdForHostAsync).
        var query = ApplyHostScope(
            db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .Where(r => r.RentalContext == rentalContext),
            scope);

        if (status is not null)
            query = query.Where(r => r.Status == status.Value);

        if (propertyId is not null)
            query = query.Where(r => r.PropertyId == propertyId.Value);

        if (bookingId is not null)
            query = query.Where(r => r.BookingId == bookingId.Value);

        return MaterializeServiceRequestPageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForSupplierAsync(
        Guid supplierOrgId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        repository.ListForSupplierAsync(supplierOrgId, openOnly, page, pageSize, cancellationToken);

    private async Task<ServiceRequest> GetSupplierRequestOrThrow(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken) ?? throw RequestNotFound(id);

        // 403 (FD-05): the request exists but was sent to another supplier.
        if (request.SupplierOrgId != supplierOrgId)
            throw new UnauthorizedAccessException($"Service request {id} belongs to another supplier");

        return request;
    }

    private static NotFoundException RequestNotFound(Guid id) => new($"Service request {id} not found")
    {
        Code = ServiceRequestErrorCodes.NotFound,
        MessageKey = ServiceRequestErrorCodes.NotFoundMessageKey,
    };

    /// <summary>The host's org and, for a scope bound to an owner, only the requests on that owner's properties.</summary>
    private static IQueryable<ServiceRequest> ApplyHostScope(IQueryable<ServiceRequest> query, HostScope scope)
    {
        query = query.Where(r => r.OrgId == scope.OrgId);
        if (scope.OwnerId is { } ownerId)
            query = query.Where(r => r.Property != null && r.Property.OwnerId == ownerId);

        return query;
    }

    private static async Task<(IReadOnlyList<ServiceRequest> Items, int Total)> MaterializeServiceRequestPageAsync(
        IQueryable<ServiceRequest> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Host notifications after a supplier status change. The status is already saved, so a failure here is logged and
    /// never turned into an error for the supplier (A4-20); the email itself is sent by a Hangfire job.
    /// </summary>
    private async Task NotifyHostAsync(ServiceRequest request, string pushStatusLabel, CancellationToken cancellationToken)
    {
        await QueueHostStatusEmailAsync(request, cancellationToken);

        try
        {
            await pushNotificationService.SendServiceRequestUpdateAsync(request.Id, pushStatusLabel, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push notification for service request {Id} ({Status}) failed", request.Id, request.Status);
        }
    }

    private async Task QueueHostStatusEmailAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var hostEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == request.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);

            var email = EmailTemplates.ServiceRequestStatusChanged(
                EmailTemplates.DefaultCulture,
                request.Status,
                request.Category,
                request.Property.Name,
                request.RejectionReason);

            emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestStatusChanged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Host email for service request {Id} ({Status}) could not be queued", request.Id, request.Status);
        }
    }
}
