using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ServiceRequestService(
    AppDbContext db,
    IServiceRequestRepository repository,
    IPropertyAuthorizationService propertyAuthorization,
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
        if (command.BookingId is { } bookingId)
        {
            var booking = await db.Bookings
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == bookingId && b.PropertyId == command.PropertyId, cancellationToken)
                ?? throw new InvalidOperationException("Prenotazione non valida per la proprietà indicata.");
        }

        if (command.ChargeToGuest)
            throw new InvalidOperationException("L'addebito all'ospite non è consentito per gli affitti brevi.");

        var property = await db.Properties
            .IgnoreQueryFilters()
            .Include(p => p.Org)
            .FirstOrDefaultAsync(p => p.Id == command.PropertyId, cancellationToken)
            ?? throw new InvalidOperationException("Proprietà non trovata.");

        if (property.OrgId != command.OrgId)
            throw new InvalidOperationException("Proprietà non appartiene all'organizzazione.");

        if (!await propertyAuthorization.CanAccessPropertyAsync(
                command.UserId, command.PropertyId, command.UserRoles ?? []))
            throw new UnauthorizedAccessException("Accesso negato alla proprietà.");

        var supplier = await db.SupplierProfiles
            .Include(sp => sp.Org)
            .FirstOrDefaultAsync(sp => sp.OrgId == command.SupplierOrgId, cancellationToken);

        if (supplier is null)
            throw new InvalidOperationException("Fornitore non trovato.");

        if (supplier.Status != SupplierStatus.Active)
            throw new ServiceRequestStateException("Il fornitore non è attivo.");

        var comuni = JsonSerializer.Deserialize<string[]>(supplier.ComuniJson, JsonOpts) ?? [];
        if (!comuni.Any(c => ItalianComuneRegistry.Matches(property.City, c)))
            throw new ServiceRequestStateException("Il fornitore non opera nel comune della proprietà.");

        var request = new ServiceRequest
        {
            OrgId = command.OrgId,
            BookingId = command.BookingId,
            PropertyId = command.PropertyId,
            SupplierOrgId = command.SupplierOrgId,
            Category = command.Category.Trim(),
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
            "ServiceRequest {Id} created for property {PropertyId} supplier {SupplierOrgId}",
            request.Id, request.PropertyId, request.SupplierOrgId);

        return (await repository.GetByIdAsync(request.Id, cancellationToken))!;
    }

    public async Task<ServiceRequest> TakeAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var request = await GetSupplierRequestOrThrow(id, supplierOrgId, cancellationToken);

        if (request.Status != ServiceRequestStatus.Richiesto)
            throw new ServiceRequestStateException("La richiesta non può essere presa in carico nello stato attuale.");

        request.Status = ServiceRequestStatus.PresoInCarico;
        request.TakenAt = DateTime.UtcNow;
        request.TakenByUserId = userId;
        request.UpdatedAt = DateTime.UtcNow;

        await repository.SaveChangesAsync(cancellationToken);
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

        if (request.Status is not (ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso))
            throw new ServiceRequestStateException("La richiesta non può essere completata nello stato attuale.");

        if (!string.IsNullOrWhiteSpace(notes))
            request.Notes = notes.Trim();

        request.Status = ServiceRequestStatus.Completato;
        request.CompletedAt = DateTime.UtcNow;
        request.UpdatedAt = DateTime.UtcNow;

        await repository.SaveChangesAsync(cancellationToken);
        await NotifyHostAsync(request, "completata", cancellationToken);

        return request;
    }

    public async Task<ServiceRequest> RejectAsync(
        Guid id,
        Guid supplierOrgId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await GetSupplierRequestOrThrow(id, supplierOrgId, cancellationToken);

        if (request.Status != ServiceRequestStatus.Richiesto)
            throw new ServiceRequestStateException("La richiesta non può essere rifiutata nello stato attuale.");

        request.Status = ServiceRequestStatus.Rifiutato;
        request.RejectionReason = reason.Trim();
        request.UpdatedAt = DateTime.UtcNow;

        await repository.SaveChangesAsync(cancellationToken);
        await QueueHostStatusEmailAsync(request, cancellationToken);
        return request;
    }

    public async Task<ServiceRequest> MarkPaidAsync(
        Guid id,
        Guid hostOrgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Service request {id} not found")
            {
                Code = "service_request_not_found",
                MessageKey = "ServiceRequestNotFound",
            };

        if (request.OrgId != hostOrgId)
            throw new UnauthorizedAccessException("Accesso negato.");

        if (!await propertyAuthorization.CanAccessPropertyAsync(userId, request.PropertyId, ["PropertyOwner", "Admin", "PropertyManager"]))
            throw new UnauthorizedAccessException("Accesso negato.");

        if (request.Status != ServiceRequestStatus.Completato)
            throw new ServiceRequestStateException("Solo le richieste completate possono essere segnate come pagate.");

        request.Status = ServiceRequestStatus.Pagato;
        request.PaidAt = DateTime.UtcNow;
        request.UpdatedAt = DateTime.UtcNow;

        await repository.SaveChangesAsync(cancellationToken);
        return request;
    }

    public Task<ServiceRequest?> GetByIdForHostAsync(
        Guid id,
        Guid hostOrgId,
        string userId,
        IEnumerable<string> userRoles,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyHostVisibility(
            db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .Where(r => r.Id == id && r.OrgId == hostOrgId),
            userId,
            userRoles);

        return query.FirstOrDefaultAsync(cancellationToken);
    }

    public Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id && r.SupplierOrgId == supplierOrgId, cancellationToken);

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        Guid orgId,
        string userId,
        IEnumerable<string> userRoles,
        ServiceRequestStatus? status,
        Guid? propertyId,
        Guid? bookingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyHostVisibility(
            db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .Include(r => r.SupplierOrg)
                .Where(r => r.OrgId == orgId),
            userId,
            userRoles);

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
        var request = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Richiesta non trovata.");

        if (request.SupplierOrgId != supplierOrgId)
            throw new UnauthorizedAccessException("Accesso negato.");

        return request;
    }

    private static IQueryable<ServiceRequest> ApplyHostVisibility(
        IQueryable<ServiceRequest> query,
        string userId,
        IEnumerable<string> userRoles)
    {
        if (userRoles.Any(r => r is "PropertyManager" or "Admin"))
            return query;

        return query.Where(r => r.Property != null && r.Property.OwnerId == userId);
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
