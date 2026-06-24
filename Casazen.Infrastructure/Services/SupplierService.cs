using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class SupplierService(
    AppDbContext db,
    IEmailService emailService,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    ILogger<SupplierService> logger) : ISupplierService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<(Org Org, SupplierProfile Profile)> RegisterAsync(
        string email,
        string legalName,
        string phone,
        string comuneCode,
        string? inviteToken,
        string? userId = null,
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

        // Link the authenticated user to the new org so subsequent supplier endpoint
        // calls resolve the org via User.OrgId instead of falling back to email lookup
        // or auto-provisioning a duplicate.
        if (userId is not null)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is not null)
            {
                user.OrgId = org.Id;
                user.UpdatedAt = DateTime.UtcNow;
                logger.LogInformation("Linked user {UserId} to supplier org {OrgId} during registration", userId, org.Id);
            }
        }

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
        if (vatNumber is not null) profile.VatNumber = vatNumber.Length == 0 ? null : vatNumber;
        if (phone is not null) profile.Phone = phone;
        if (categories is not null) profile.CategoriesJson = JsonSerializer.Serialize(categories, JsonOpts);
        if (comuni is not null) profile.ComuniJson = JsonSerializer.Serialize(comuni, JsonOpts);
        if (bio is not null) profile.Bio = bio.Length == 0 ? null : bio;
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

        // Only ToS gates activation. Categories, comuni, and bio can be completed later.
        if (!tosAccepted)
            throw new InvalidOperationException("Devi accettare i termini di servizio");

        profile.TosAcceptedAt = DateTime.UtcNow;
        profile.Status = SupplierStatus.Active;
        profile.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Supplier {OrgId} activated", orgId);
        return profile;
    }

    public async Task<IReadOnlyList<(DateOnly Date, bool Available)>> GetAvailabilityAsync(
        Guid orgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.SupplierAvailability
            .AsNoTracking()
            .Where(sa => sa.OrgId == orgId && sa.Date >= from && sa.Date <= to)
            .OrderBy(sa => sa.Date)
            .ToListAsync(cancellationToken);

        return rows.Select(sa => (sa.Date, sa.Available)).ToList();
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

        await SendInviteEmailAsync(invite, cancellationToken);

        logger.LogInformation("Admin invite created for {Email}, expires {ExpiresAt}", email, invite.ExpiresAt);
        return new SupplierInvite(invite.Id, invite.ExpiresAt);
    }

    public async Task<Guid?> GetOrProvisionSupplierOrgIdAsync(
        string userId,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return null;

        var resolvedEmail = string.IsNullOrWhiteSpace(email) ? user.Email : email;
        var normalizedEmail = resolvedEmail.Trim().ToLowerInvariant();

        if (user.OrgId is Guid linkedOrgId)
        {
            var linkedOrg = await db.Orgs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == linkedOrgId, cancellationToken);
            if (linkedOrg?.OrgType == OrgType.Supplier)
            {
                var linkedProfile = await db.SupplierProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(sp => sp.OrgId == linkedOrgId, cancellationToken);
                if (linkedProfile is not null)
                    return linkedOrgId;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var profileByEmail = await db.SupplierProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(sp => sp.Email.ToLower() == normalizedEmail, cancellationToken);
            if (profileByEmail is not null)
                return profileByEmail.OrgId;
        }

        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = string.IsNullOrWhiteSpace(resolvedEmail) ? "Fornitore" : resolvedEmail;

        var slug = $"supplier-{Guid.NewGuid():N}"[..30];
        var org = new Org
        {
            Name = displayName,
            Slug = slug,
            DisplayName = displayName,
            ContactEmail = resolvedEmail,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);

        var profile = new SupplierProfile
        {
            OrgId = org.Id,
            Email = resolvedEmail,
            LegalName = displayName,
            Phone = string.Empty,
        };
        db.SupplierProfiles.Add(profile);

        logger.LogWarning(
            "Auto-provisioning supplier org {OrgId} for user {UserId} (email={Email}, name={DisplayName}) — "
            + "this should only happen on first access; if it repeats, the user may have duplicate profiles",
            org.Id, userId, resolvedEmail, displayName);

        // Consume any pending invite for this email during auto-provisioning
        var pendingInvite = await db.SupplierInviteRecords
            .FirstOrDefaultAsync(i =>
                i.Email.ToLower() == normalizedEmail &&
                !i.IsUsed &&
                i.ExpiresAt > DateTime.UtcNow,
                cancellationToken);

        if (pendingInvite is not null)
        {
            pendingInvite.IsUsed = true;
            if (!string.IsNullOrWhiteSpace(pendingInvite.ComuneCode))
                profile.ComuniJson = JsonSerializer.Serialize(new[] { pendingInvite.ComuneCode });
            if (!string.IsNullOrWhiteSpace(pendingInvite.CategoriesJson))
                profile.CategoriesJson = pendingInvite.CategoriesJson;
        }

        if (user.OrgId is null)
        {
            user.OrgId = org.Id;
            user.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Auto-provisioned supplier org {OrgId} for user {UserId}", org.Id, userId);
        return org.Id;
    }

    public async Task<SupplierProfile?> UpdateCalendarSyncAsync(
        Guid orgId,
        CalendarSyncType syncType,
        string? icalFeedUrl,
        string? calendarSyncError,
        CancellationToken cancellationToken = default)
    {
        var profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);
        if (profile is null)
            return null;

        profile.CalendarSyncType = syncType;
        profile.IcalFeedUrl = icalFeedUrl;
        profile.CalendarSyncError = calendarSyncError;
        profile.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    public async Task<FixOrphanedSupplierOrgsReport> FixOrphanedSupplierOrgsAsync(CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        int usersLinked = 0, duplicatesMerged = 0, emptyOrgsDeleted = 0, orphansSkipped = 0;

        var allProfiles = await db.SupplierProfiles
            .Include(sp => sp.Org)
            .OrderBy(sp => sp.CreatedAt)
            .ToListAsync(cancellationToken);

        // Group by normalized email to detect duplicates
        var profilesByEmail = allProfiles
            .GroupBy(sp => sp.Email.Trim().ToLowerInvariant())
            .ToList();

        foreach (var group in profilesByEmail)
        {
            var email = group.Key;
            var profiles = group.ToList();

            // Case C: Duplicate profiles for the same email — keep the richest one
            if (profiles.Count > 1)
            {
                // Score: richer = has LegalName that differs from email prefix, has categories, has bio
                static int Score(SupplierProfile p)
                {
                    int s = 0;
                    if (!string.IsNullOrWhiteSpace(p.LegalName)) s += 2;
                    if (p.CategoriesJson is not null && p.CategoriesJson != "[]") s += 1;
                    if (p.ComuniJson is not null && p.ComuniJson != "[]") s += 1;
                    if (!string.IsNullOrWhiteSpace(p.Bio)) s += 1;
                    if (p.Status == SupplierStatus.Active) s += 1;
                    return s;
                }

                var ordered = profiles.OrderByDescending(Score).ThenBy(p => p.CreatedAt).ToList();
                var keeper = ordered[0];
                var victims = ordered.Skip(1).ToList();

                foreach (var victim in victims)
                {
                    var victimOrg = victim.Org;
                    db.SupplierProfiles.Remove(victim);
                    if (victimOrg is not null)
                        db.Orgs.Remove(victimOrg);
                    duplicatesMerged++;
                    details.Add($"Duplicate merged: kept profile {keeper.OrgId} (score={Score(keeper)}), deleted {victim.OrgId} (score={Score(victim)}) — email={email}");
                    logger.LogWarning("FixOrphaned: deleted duplicate profile {VictimOrgId} for {Email}, kept {KeeperOrgId}", victim.OrgId, email, keeper.OrgId);
                }

                // Ensure the remaining profiles list has only the keeper
                profiles = [keeper];
            }

            // Cases A, B, D for the remaining (non-duplicate) profile
            foreach (var profile in profiles)
            {
                var user = await db.Users
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == email, cancellationToken);

                if (user is null)
                {
                    // Case D: No user found
                    orphansSkipped++;
                    details.Add($"Orphan: profile {profile.OrgId} has no matching user for email={email}");
                    logger.LogWarning("FixOrphaned: no user found for supplier profile {OrgId} email={Email}", profile.OrgId, email);
                    continue;
                }

                if (user.OrgId is Guid existingOrgId && existingOrgId != profile.OrgId)
                {
                    var existingOrg = await db.Orgs.AsNoTracking()
                        .FirstOrDefaultAsync(o => o.Id == existingOrgId, cancellationToken);
                    if (existingOrg?.OrgType == OrgType.Host)
                    {
                        // Case B: User is a host — don't overwrite their host OrgId
                        details.Add($"Dual-role: user {user.Id} is host (org={existingOrgId}) and supplier (org={profile.OrgId}) — OrgId kept as host org");
                        continue;
                    }

                    // Existing OrgId points to a different supplier org (possibly deleted or wrong type)
                    details.Add($"Re-link: user {user.Id} OrgId changed from {existingOrgId} to supplier org {profile.OrgId}");
                }

                // Case A: Set user.OrgId to the supplier org
                if (user.OrgId != profile.OrgId)
                {
                    user.OrgId = profile.OrgId;
                    user.UpdatedAt = DateTime.UtcNow;
                    usersLinked++;
                    details.Add($"Linked: user {user.Id} ({email}) → supplier org {profile.OrgId}");
                    logger.LogInformation("FixOrphaned: linked user {UserId} to supplier org {OrgId}", user.Id, profile.OrgId);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var report = new FixOrphanedSupplierOrgsReport(
            ProfilesScanned: allProfiles.Count,
            UsersLinked: usersLinked,
            DuplicatesMerged: duplicatesMerged,
            EmptyOrgsDeleted: emptyOrgsDeleted,
            OrphansSkipped: orphansSkipped,
            Details: details);

        logger.LogInformation(
            "FixOrphaned completed: scanned={Scanned}, linked={Linked}, merged={Merged}, deletedOrgs={Deleted}, orphans={Orphans}",
            report.ProfilesScanned, report.UsersLinked, report.DuplicatesMerged, report.EmptyOrgsDeleted, report.OrphansSkipped);

        return report;
    }

    private async Task SendInviteEmailAsync(SupplierInviteRecord invite, CancellationToken cancellationToken)
    {
        if (!IsEmailConfigured())
        {
            if (ShouldSkipInviteEmail())
            {
                logger.LogWarning(
                    "Email not configured — supplier invite email skipped for {Email} (env={Environment})",
                    invite.Email,
                    hostEnvironment.EnvironmentName);
                return;
            }

            db.SupplierInviteRecords.Remove(invite);
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(
                "Email non configurata. Impostare Email__ResendApiKey su Railway (https://resend.com, gratis 100 email/giorno).");
        }

        var signupUrl = configuration["App:SupplierLoginUrl"];
        if (string.IsNullOrWhiteSpace(signupUrl))
        {
            var frontendBaseUrl = configuration["App:PublicSiteBaseUrl"];
            if (string.IsNullOrWhiteSpace(frontendBaseUrl))
            {
                throw new InvalidOperationException(
                    "App:PublicSiteBaseUrl non configurato: impossibile inviare l'invito.");
            }
            signupUrl = SupplierInviteEmailBuilder.BuildLoginUrl(frontendBaseUrl);
        }
        var (subject, html) = SupplierInviteEmailBuilder.Build(invite, signupUrl, invite.ExpiresAt);

        var result = await emailService.SendEmailAsync(invite.Email, subject, html);
        if (result.Success)
        {
            logger.LogInformation("Supplier invite email sent to {Email}", invite.Email);
            return;
        }

        db.SupplierInviteRecords.Remove(invite);
        await db.SaveChangesAsync(cancellationToken);
        var reason = string.IsNullOrWhiteSpace(result.ErrorDetail)
            ? "Impossibile inviare l'email di invito. Riprovare."
            : "Impossibile inviare l'email di invito. Controllare la configurazione email del server.";
        throw new InvalidOperationException(reason);
    }

    private bool IsEmailConfigured()
    {
        // Resend HTTP API (primary — works on all Railway plans)
        var resendKey = configuration["Email:ResendApiKey"];
        if (!string.IsNullOrWhiteSpace(resendKey) && resendKey.StartsWith("re_"))
            return true;

        // SMTP (local dev only — blocked on Railway Hobby)
        if (!string.IsNullOrWhiteSpace(configuration["Email:SmtpHost"]))
            return true;

        // SendGrid SMTP relay (legacy fallback)
        var sgKey = configuration["Email:SendGridApiKey"];
        return !string.IsNullOrWhiteSpace(sgKey)
            && !sgKey.StartsWith("SG.YOUR", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldSkipInviteEmail()
    {
        // Only called when !IsEmailConfigured() — skip in dev/test, throw in production.
        return hostEnvironment.IsEnvironment("Testing") || hostEnvironment.IsDevelopment();
    }
}
