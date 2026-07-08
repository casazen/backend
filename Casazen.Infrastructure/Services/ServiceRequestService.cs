using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ServiceRequestService(
    AppDbContext db,
    IServiceRequestRepository repository,
    IPropertyAuthorizationService propertyAuthorization,
    IEmailService emailService,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
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
                command.UserId, command.PropertyId, ["PropertyOwner", "Admin", "PropertyManager"]))
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

        await repository.AddAsync(request, cancellationToken);

        await SendSupplierNewRequestEmailAsync(supplier, property, request, cancellationToken);

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
        await SendHostStatusEmailAsync(request, "presa in carico", cancellationToken);

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
        await SendHostStatusEmailAsync(request, "completata", cancellationToken);

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
        return request;
    }

    public async Task<ServiceRequest> MarkPaidAsync(
        Guid id,
        Guid hostOrgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var request = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Richiesta non trovata.");

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

    public Task<ServiceRequest?> GetByIdForHostAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == hostOrgId, cancellationToken);

    public Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .Include(r => r.Property)
            .Include(r => r.SupplierOrg)
            .FirstOrDefaultAsync(r => r.Id == id && r.SupplierOrgId == supplierOrgId, cancellationToken);

    public Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        Guid orgId,
        ServiceRequestStatus? status,
        Guid? propertyId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        repository.ListForHostAsync(orgId, status, propertyId, page, pageSize, cancellationToken);

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

    private async Task SendSupplierNewRequestEmailAsync(
        SupplierProfile supplier,
        Property property,
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!ShouldSendEmail())
            return;

        var consoleUrl = configuration["App:FrontendBaseUrl"]?.TrimEnd('/') ?? "https://app.casazen.it";
        var subject = $"Nuova richiesta di servizio — {property.Name}";
        var html = $"""
            <p>Ciao {supplier.LegalName},</p>
            <p>Hai ricevuto una nuova richiesta di <strong>{request.Category}</strong> per la proprietà <strong>{property.Name}</strong>.</p>
            <p>{(string.IsNullOrWhiteSpace(request.Notes) ? "" : $"Note: {request.Notes}<br/>")}</p>
            <p><a href="{consoleUrl}/app/supplier/inbox">Apri la console fornitore</a></p>
            """;

        var result = await emailService.SendEmailAsync(supplier.Email, subject, html);
        if (!result.Success)
            logger.LogWarning("Failed to send supplier notification for request {Id}: {Error}", request.Id, result.ErrorDetail);
    }

    private async Task SendHostStatusEmailAsync(
        ServiceRequest request,
        string statusLabel,
        CancellationToken cancellationToken)
    {
        if (!ShouldSendEmail())
            return;

        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == request.OrgId, cancellationToken);
        if (org?.ContactEmail is null)
            return;

        var property = request.Property;
        var subject = $"Richiesta fornitore {statusLabel} — {property.Name}";
        var html = $"""
            <p>La richiesta di <strong>{request.Category}</strong> per <strong>{property.Name}</strong> è stata <strong>{statusLabel}</strong>.</p>
            """;

        var result = await emailService.SendEmailAsync(org.ContactEmail, subject, html);
        if (!result.Success)
            logger.LogWarning("Failed to send host notification for request {Id}: {Error}", request.Id, result.ErrorDetail);
    }

    private bool ShouldSendEmail()
    {
        if (hostEnvironment.IsEnvironment("Testing"))
            return false;

        var apiKey = configuration["SendGrid:ApiKey"] ?? configuration["Email:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey) || !hostEnvironment.IsProduction();
    }
}
