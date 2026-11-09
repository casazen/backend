using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Self-service area of a long-rent tenant. Access is bound to a verified Auth0 email which must equal an active,
/// non-anonymized tenant party of the lease. Queries deliberately bypass the host tenant filter and then narrow every
/// row by both lease and org; a tenant never joins the landlord's organization.
/// </summary>
[ApiController]
[Route("api/tenant-area")]
[Authorize(Policy = CasazenPolicies.TenantAccount)]
[PrivateAnswer]
public sealed class TenantAreaController(
    AppDbContext db,
    IAccountEmailResolver accountEmails,
    ILeaseMaintenanceService maintenance,
    IFileStorage storage,
    TimeProvider clock) : ControllerBase
{
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var identity = await TenantIdentityAsync(cancellationToken);
        if (identity.Failure is not null) return identity.Failure;

        var leases = await AccessibleLeasesAsync(identity.Email!, cancellationToken);
        return Ok(new
        {
            contracts = leases.Select(x => new
            {
                id = x.Lease.Id,
                label = Label(x.Lease),
                propertyName = x.Lease.Property.Name,
                role = RoleOf(x.Party),
                status = x.Lease.Status.ToString(),
            }),
        });
    }

    [HttpGet("contracts/{leaseId:guid}")]
    public async Task<IActionResult> GetContract(Guid leaseId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(leaseId, cancellationToken);
        if (access.Failure is not null) return access.Failure;
        var lease = access.Lease!;

        var dues = await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.LeaseContractId == lease.Id && x.OrgId == lease.OrgId)
            .Include(x => x.RentSchedule)
            .Include(x => x.Components)
            .OrderBy(x => x.DueDate)
            .ToListAsync(cancellationToken);
        var dueIds = dues.Select(x => x.Id).ToList();
        var declarations = await db.TenantPaymentDeclarations.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.OrgId == lease.OrgId && dueIds.Contains(x.RentLedgerEntryId))
            .OrderByDescending(x => x.SubmittedAt)
            .ToListAsync(cancellationToken);
        var latestDeclaration = declarations.GroupBy(x => x.RentLedgerEntryId).ToDictionary(x => x.Key, x => x.First());

        var documents = await db.LeaseDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.LeaseContractId == lease.Id && x.OrgId == lease.OrgId
                && (x.Kind == LeaseDocumentKind.Contract || x.Kind == LeaseDocumentKind.Attestation || x.Kind == LeaseDocumentKind.Registration))
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);
        var today = clock.TodayInRomeAsDateOnly();

        return Ok(new
        {
            contract = new
            {
                id = lease.Id,
                label = Label(lease),
                status = lease.Status.ToString(),
                startDate = DateOnly.FromDateTime(lease.StartDate),
                endDate = DateOnly.FromDateTime(lease.CurrentTermEndDate),
                role = RoleOf(access.Party!),
            },
            property = new { id = lease.Property.Id, name = lease.Property.Name, address = lease.Property.Address },
            dues = dues.Select(x => new
            {
                id = x.Id,
                periodStart = x.PeriodStart,
                periodEnd = x.PeriodEnd,
                dueDate = x.DueDate,
                currency = x.RentSchedule.Currency.ToUpperInvariant(),
                amount = x.AmountDue,
                amountPaid = x.Status == RentLedgerStatus.Paid ? x.AmountDue : (decimal?)null,
                residual = x.Status == RentLedgerStatus.Paid ? 0m : x.AmountDue,
                status = x.Status.ToString(),
                isOverdue = x.Status is not RentLedgerStatus.Paid and not RentLedgerStatus.Cancelled && x.DueDate < today,
                components = x.Components.Select(c => new { id = c.Id, kind = c.Kind.ToString(), amount = c.Amount, description = c.Description }),
                bank = (object?)null,
                declaration = latestDeclaration.TryGetValue(x.Id, out var declaration) ? Declaration(declaration) : null,
            }),
            documents = documents.Select(x => new
            {
                id = x.Id,
                name = x.FileName,
                kind = x.Kind.ToString(),
                sharedAt = x.UploadedAt,
                accepted = true,
            }),
            emergency = (object?)null,
            maintenanceCategories = new[] { "Plumbing", "Electrical", "Heating", "Appliances", "Building", "Other" },
        });
    }

    [HttpPost("contracts/{leaseId:guid}/dues/{dueId:guid}/declarations")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> DeclareTransfer(Guid leaseId, Guid dueId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(leaseId, cancellationToken);
        if (access.Failure is not null) return access.Failure;
        var lease = access.Lease!;
        var input = await ReadDeclarationAsync(cancellationToken);
        if (input is null || input.Amount <= 0 || input.TransferDate == default)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "TenantDeclarationInvalid");

        var due = await db.RentLedgerEntries.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == dueId && x.LeaseContractId == lease.Id && x.OrgId == lease.OrgId, cancellationToken);
        if (due is null)
            return NotFound();

        var previous = await db.TenantPaymentDeclarations.IgnoreQueryFilters()
            .Where(x => x.RentLedgerEntryId == due.Id && x.OrgId == lease.OrgId && x.Status == TenantPaymentDeclarationStatus.Submitted)
            .ToListAsync(cancellationToken);
        foreach (var row in previous) row.Status = TenantPaymentDeclarationStatus.Superseded;

        var declaration = new TenantPaymentDeclaration
        {
            OrgId = lease.OrgId,
            LeaseContractId = lease.Id,
            RentLedgerEntryId = due.Id,
            SubmittedByUserId = access.UserId!,
            Amount = input.Amount,
            TransferDate = input.TransferDate,
            BankReference = Clean(input.BankReference, 200),
            Note = Clean(input.Note, 500),
        };
        if (input.Receipt is not null)
        {
            var fileName = Path.GetFileName(input.Receipt.FileName);
            var extension = Path.GetExtension(fileName);
            declaration.ReceiptStorageKey = $"tenant-area/{lease.OrgId:N}/{lease.Id:N}/declarations/{declaration.Id:N}{extension}";
            await using var stream = input.Receipt.OpenReadStream();
            await storage.PutAsync(StorageBucket.Private, declaration.ReceiptStorageKey, stream,
                string.IsNullOrWhiteSpace(input.Receipt.ContentType) ? "application/octet-stream" : input.Receipt.ContentType,
                cancellationToken);
        }
        db.TenantPaymentDeclarations.Add(declaration);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(Declaration(declaration));
    }

    [HttpGet("contracts/{leaseId:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> DownloadDocument(Guid leaseId, Guid documentId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(leaseId, cancellationToken);
        if (access.Failure is not null) return access.Failure;
        var lease = access.Lease!;
        var document = await db.LeaseDocuments.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == documentId && x.LeaseContractId == lease.Id && x.OrgId == lease.OrgId
                && (x.Kind == LeaseDocumentKind.Contract || x.Kind == LeaseDocumentKind.Attestation || x.Kind == LeaseDocumentKind.Registration), cancellationToken);
        if (document is null) return NotFound();
        var stream = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, cancellationToken);
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return File(stream, "application/octet-stream", document.FileName);
    }

    [HttpGet("contracts/{leaseId:guid}/maintenance")]
    public async Task<IActionResult> ListMaintenance(Guid leaseId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(leaseId, cancellationToken);
        if (access.Failure is not null) return access.Failure;
        var caller = TenantCaller(access);
        var list = await maintenance.ListAsync(caller, leaseId, cancellationToken);
        return Ok(new { items = list.Items.Select(Maintenance) });
    }

    [HttpGet("contracts/{leaseId:guid}/maintenance/{requestId:guid}")]
    public async Task<IActionResult> GetMaintenance(Guid leaseId, Guid requestId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(leaseId, cancellationToken);
        if (access.Failure is not null) return access.Failure;
        var list = await maintenance.ListAsync(TenantCaller(access), leaseId, cancellationToken);
        var row = list.Items.SingleOrDefault(x => x.Id == requestId);
        return row is null ? NotFound() : Ok(Maintenance(row));
    }

    [HttpPost("contracts/{leaseId:guid}/maintenance")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> CreateMaintenance(Guid leaseId, CancellationToken cancellationToken)
    {
        var access = await RequireAccessAsync(leaseId, cancellationToken);
        if (access.Failure is not null) return access.Failure;
        if (!Request.HasFormContentType)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "MaintenanceInvalid");
        var form = await Request.ReadFormAsync(cancellationToken);
        var category = Clean(form["category"].FirstOrDefault(), LeaseMaintenanceRequest.TitleMaxLength) ?? "Other";
        var description = Clean(form["description"].FirstOrDefault(), LeaseMaintenanceRequest.DescriptionMaxLength);
        if (description is null || !Enum.TryParse<LeaseMaintenanceUrgency>(form["urgency"].FirstOrDefault(), true, out var urgency))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "MaintenanceInvalid");
        var photos = new List<LeaseMaintenancePhotoContent>();
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            photos.Add(new LeaseMaintenancePhotoContent(Path.GetFileName(file.FileName), buffer.ToArray()));
        }
        var created = await maintenance.CreateAsync(TenantCaller(access), leaseId,
            new CreateLeaseMaintenanceCommand(category, description, urgency, photos), cancellationToken);
        return Created($"/api/tenant-area/contracts/{leaseId}/maintenance/{created.Id}", Maintenance(created));
    }

    private async Task<TenantIdentity> TenantIdentityAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null) return new(null, null, Unauthorized());
        var (email, verified) = await accountEmails.ResolveAsync(User, userId, cancellationToken);
        if (!verified || string.IsNullOrWhiteSpace(email))
            return new(userId, null, this.ApiProblem(StatusCodes.Status403Forbidden, "tenant_email_not_verified", "TenantEmailNotVerified"));
        return new(userId, email.Trim(), null);
    }

    private async Task<TenantAccess> RequireAccessAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var identity = await TenantIdentityAsync(cancellationToken);
        if (identity.Failure is not null) return new(identity.UserId, identity.Email, null, null, identity.Failure);
        var rows = await AccessibleLeasesAsync(identity.Email!, cancellationToken, leaseId);
        var row = rows.SingleOrDefault();
        return row is null
            ? new(identity.UserId, identity.Email, null, null, NotFound())
            : new(identity.UserId, identity.Email, row.Lease, row.Party, null);
    }

    private async Task<List<AccessibleLease>> AccessibleLeasesAsync(string email, CancellationToken cancellationToken, Guid? leaseId = null)
    {
        var rows = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => leaseId == null || x.Id == leaseId)
            .Include(x => x.Property)
            .Include(x => x.Parties)
            .OrderByDescending(x => x.StartDate)
            .ToListAsync(cancellationToken);
        return rows.SelectMany(lease => lease.Parties
                .Where(p => p.Role == PartyRole.Tenant && p.AnonymizedAt == null
                    && string.Equals(p.ContactEmail.Trim(), email, StringComparison.OrdinalIgnoreCase))
                .Select(p => new AccessibleLease(lease, p)))
            .ToList();
    }

    private async Task<DeclarationInput?> ReadDeclarationAsync(CancellationToken cancellationToken)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            return decimal.TryParse(form["amount"].FirstOrDefault(), System.Globalization.NumberStyles.Number,
                       System.Globalization.CultureInfo.InvariantCulture, out var amount)
                && DateOnly.TryParse(form["transferDate"].FirstOrDefault(), out var day)
                ? new(amount, day, form["bankReference"].FirstOrDefault(), form["note"].FirstOrDefault(), form.Files.FirstOrDefault())
                : null;
        }
        var json = await Request.ReadFromJsonAsync<DeclarationJson>(cancellationToken);
        return json is null ? null : new(json.Amount, json.TransferDate, json.BankReference, json.Note, null);
    }

    private static LeaseMaintenanceCaller TenantCaller(TenantAccess access) =>
        new(access.Lease!.OrgId, access.UserId!, access.Email, true, false, false, false);

    private static object Declaration(TenantPaymentDeclaration x) => new
    {
        id = x.Id,
        status = x.Status.ToString(),
        amount = x.Amount,
        transferDate = x.TransferDate,
        submittedAt = x.SubmittedAt,
        bankReference = x.BankReference,
    };

    private static object Maintenance(LeaseMaintenanceRequestView x) => new
    {
        id = x.Id,
        category = x.Title,
        urgency = x.Urgency.ToString(),
        description = x.Description,
        submittedAt = x.CreatedAt,
        status = x.Status switch
        {
            LeaseMaintenanceStatus.InAttesa => "InReview",
            LeaseMaintenanceStatus.Approvata => "Accepted",
            LeaseMaintenanceStatus.Rifiutata => "Rejected",
            LeaseMaintenanceStatus.Pagata => "Closed",
            _ => "Sent",
        },
        updates = x.DecidedAt is null ? Array.Empty<object>() : new[] { new { id = $"decision-{x.Id:N}", at = x.DecidedAt, text = x.RejectionReason ?? x.Status.ToString(), author = "Manager" } },
        attachments = x.Photos.Select(p => new { id = p.Id, name = p.FileName }),
    };

    private static string Label(LeaseContract lease) => $"{lease.Property.Name} · {lease.StartDate:yyyy}";
    private static string RoleOf(Party party) => party.Position == 0 ? "TenantPrimary" : "Tenant";
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private sealed record TenantIdentity(string? UserId, string? Email, IActionResult? Failure);
    private sealed record TenantAccess(string? UserId, string? Email, LeaseContract? Lease, Party? Party, IActionResult? Failure);
    private sealed record AccessibleLease(LeaseContract Lease, Party Party);
    private sealed record DeclarationInput(decimal Amount, DateOnly TransferDate, string? BankReference, string? Note, IFormFile? Receipt);
    private sealed record DeclarationJson(decimal Amount, DateOnly TransferDate, string? BankReference, string? Note);
}
