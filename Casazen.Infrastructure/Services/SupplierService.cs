using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class SupplierService(AppDbContext db, ILogger<SupplierService> logger) : ISupplierService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<(Org Org, SupplierProfile Profile)> RegisterAsync(
        string email,
        string legalName,
        string phone,
        string comuneCode,
        string? inviteToken,
        CancellationToken cancellationToken = default)
    {
        if (inviteToken is not null)
        {
            var invite = await db.SupplierInviteRecords
                .FirstOrDefaultAsync(i =>
                    i.Id == Guid.Parse(inviteToken) &&
                    !i.IsUsed &&
                    i.ExpiresAt > DateTime.UtcNow,
                    cancellationToken);

            if (invite is null)
                throw new InvalidOperationException("Invalid or expired invite token.");

            invite.IsUsed = true;
        }

        var slug = $"supplier-{Guid.NewGuid():N}"[..30];
        var org = new Org
        {
            Name = legalName,
            Slug = slug,
            DisplayName = legalName,
            ContactEmail = email,
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);

        var profile = new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = legalName,
            Phone = phone,
            ComuniJson = JsonSerializer.Serialize(new[] { comuneCode }, JsonOpts),
        };
        db.SupplierProfiles.Add(profile);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Supplier org {OrgId} registered for {Email}", org.Id, email);
        return (org, profile);
    }

    public async Task<SupplierProfile?> GetProfileAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);

    public async Task<SupplierProfile?> UpdateProfileAsync(
        Guid orgId,
        string? legalName,
        string? vatNumber,
        string? phone,
        IEnumerable<string>? categories,
        IEnumerable<string>? comuni,
        string? bio,
        IEnumerable<string>? photoUrls,
        CancellationToken cancellationToken = default)
    {
        var profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);
        if (profile is null)
            return null;

        if (legalName is not null) profile.LegalName = legalName;
        if (vatNumber is not null) profile.VatNumber = vatNumber;
        if (phone is not null) profile.Phone = phone;
        if (categories is not null) profile.CategoriesJson = JsonSerializer.Serialize(categories, JsonOpts);
        if (comuni is not null) profile.ComuniJson = JsonSerializer.Serialize(comuni, JsonOpts);
        if (bio is not null) profile.Bio = bio;
        if (photoUrls is not null) profile.PhotoUrlsJson = JsonSerializer.Serialize(photoUrls, JsonOpts);
        profile.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    public async Task<IReadOnlyList<ActivationStep>> GetActivationStepsAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);
        if (profile is null)
            return Array.Empty<ActivationStep>();

        var categories = JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? [];
        var comuni = JsonSerializer.Deserialize<string[]>(profile.ComuniJson, JsonOpts) ?? [];

        return
        [
            new ActivationStep("identity", "Identità e contatti", "completed"),
            new ActivationStep("categories", "Categorie di servizio",
                categories.Length > 0 ? "completed" : "pending",
                categories.Length == 0 ? "Scegli almeno una categoria" : null),
            new ActivationStep("comuni", "Comuni di operatività",
                comuni.Length > 0 ? "completed" : "pending",
                comuni.Length == 0 ? "Seleziona almeno un comune" : null),
            new ActivationStep("profile", "Profilo professionale",
                !string.IsNullOrWhiteSpace(profile.Bio) ? "completed" : "pending",
                string.IsNullOrWhiteSpace(profile.Bio) ? "Aggiungi una descrizione professionale" : null),
            new ActivationStep("tos", "Termini di servizio",
                profile.TosAcceptedAt.HasValue ? "completed" : "pending",
                !profile.TosAcceptedAt.HasValue ? "Accetta i termini di servizio" : null),
        ];
    }

    public async Task<SupplierProfile> CompleteActivationAsync(Guid orgId, bool tosAccepted, CancellationToken cancellationToken = default)
    {
        var profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken)
            ?? throw new KeyNotFoundException($"Supplier profile not found for org {orgId}");

        var steps = await GetActivationStepsAsync(orgId, cancellationToken);
        var blockers = steps.Where(s => s.Status != "completed" && s.Id != "tos").ToList();

        if (!tosAccepted)
            blockers.Add(new ActivationStep("tos", "Termini di servizio", "pending", "Devi accettare i termini di servizio"));

        if (blockers.Count > 0)
        {
            var msgs = string.Join("; ", blockers.Select(b => b.Blocker));
            throw new InvalidOperationException($"Attivazione non completata: {msgs}");
        }

        profile.TosAcceptedAt = DateTime.UtcNow;
        profile.Status = SupplierStatus.Active;
        profile.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Supplier {OrgId} activated", orgId);
        return profile;
    }

    public async Task<int> UpdateAvailabilityAsync(
        Guid orgId,
        IEnumerable<(DateOnly Date, bool Available)> entries,
        CancellationToken cancellationToken = default)
    {
        var entriesList = entries.ToList();
        var dates = entriesList.Select(e => e.Date).ToList();

        var existing = await db.SupplierAvailability
            .Where(sa => sa.OrgId == orgId && dates.Contains(sa.Date))
            .ToListAsync(cancellationToken);

        int count = 0;
        foreach (var (date, available) in entriesList)
        {
            var record = existing.FirstOrDefault(e => e.Date == date);
            if (record is null)
            {
                db.SupplierAvailability.Add(new SupplierAvailability
                {
                    OrgId = orgId,
                    Date = date,
                    Available = available,
                });
            }
            else
            {
                record.Available = available;
            }
            count++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return count;
    }

    public async Task<IReadOnlyList<SupplierProfile>> GetActiveByComune(string comuneCode, string? category, CancellationToken cancellationToken = default)
    {
        var all = await db.SupplierProfiles
            .Where(sp => sp.Status == SupplierStatus.Active)
            .ToListAsync(cancellationToken);

        return all.Where(sp =>
        {
            var comuni = JsonSerializer.Deserialize<string[]>(sp.ComuniJson, JsonOpts) ?? [];
            if (!comuni.Contains(comuneCode, StringComparer.OrdinalIgnoreCase))
                return false;

            if (category is not null)
            {
                var cats = JsonSerializer.Deserialize<string[]>(sp.CategoriesJson, JsonOpts) ?? [];
                return cats.Contains(category, StringComparer.OrdinalIgnoreCase);
            }
            return true;
        }).ToList();
    }

    public async Task<SupplierInvite> CreateInviteAsync(
        string email,
        string comuneCode,
        IEnumerable<string>? categories,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.SupplierInviteRecords
            .FirstOrDefaultAsync(i => i.Email == email && !i.IsUsed && i.ExpiresAt > DateTime.UtcNow, cancellationToken);

        if (existing is not null)
            throw new InvalidOperationException($"Pending invite already exists for {email}");

        var invite = new SupplierInviteRecord
        {
            Email = email,
            ComuneCode = comuneCode,
            CategoriesJson = categories is not null
                ? JsonSerializer.Serialize(categories, JsonOpts)
                : null,
            Message = message,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        };
        db.SupplierInviteRecords.Add(invite);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Admin invite created for {Email}, expires {ExpiresAt}", email, invite.ExpiresAt);
        return new SupplierInvite(invite.Id, invite.ExpiresAt);
    }
}
