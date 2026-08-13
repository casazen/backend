using System.Data;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
        User? user = null;
        if (userId is not null)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is not null)
            {
                var existing = await TryGetExistingSupplierRegistrationAsync(user, cancellationToken);
                if (existing is not null)
                {
                    logger.LogInformation(
                        "User {UserId} is already linked to supplier org {OrgId}; returning existing registration",
                        userId,
                        existing.Value.Org.Id);
                    return existing.Value;
                }
            }
        }

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
        // calls resolve the org via User.SupplierOrgId instead of falling back to
        // email lookup or auto-provisioning a duplicate.
        if (user is not null)
        {
            user.SupplierOrgId = org.Id;
            if (user.OrgId is null)
                user.OrgId = org.Id;
            user.UpdatedAt = DateTime.UtcNow;
            logger.LogInformation("Linked user {UserId} to supplier org {OrgId} during registration", userId, org.Id);
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
            if (!comuni.Any(c => ItalianComuneRegistry.Matches(comuneCode, c)))
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
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            return await GetOrProvisionSupplierOrgIdCoreAsync(userId, email, firstName, lastName, cancellationToken);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var orgId = await GetOrProvisionSupplierOrgIdCoreAsync(userId, email, firstName, lastName, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return orgId;
            }
            catch (Exception ex) when (attempt == 1 && IsProvisioningSerializationRace(ex))
            {
                logger.LogWarning(
                    ex,
                    "Retrying supplier org provisioning after serialization conflict for user {UserId}",
                    userId);
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }

        return null;
    }

    private async Task<Guid?> GetOrProvisionSupplierOrgIdCoreAsync(
        string userId,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return null;

        var resolvedEmail = string.IsNullOrWhiteSpace(email) ? user.Email : email;
        var normalizedEmail = string.IsNullOrWhiteSpace(resolvedEmail) ? string.Empty : resolvedEmail.Trim().ToLowerInvariant();

        // Step 1a: User.SupplierOrgId — set explicitly during registration or
        // auto-provisioning, survives even when User.OrgId points to a host org.
        if (user.SupplierOrgId is Guid supplierOrgId)
        {
            var supplierOrg = await db.Orgs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == supplierOrgId, cancellationToken);
            if (supplierOrg?.OrgType == OrgType.Supplier)
            {
                var supplierProfile = await db.SupplierProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(sp => sp.OrgId == supplierOrgId, cancellationToken);
                if (supplierProfile is not null)
                    return supplierOrgId;
            }
        }

        // Step 1b: User.OrgId — covers the case where the user is ONLY a supplier
        // (not dual-role) and their OrgId points to a supplier org.
        if (user.OrgId is Guid linkedOrgId)
        {
            var linkedOrg = await db.Orgs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == linkedOrgId, cancellationToken);
            if (linkedOrg?.OrgType == OrgType.Supplier)
            {
                var linkedProfile = await db.SupplierProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(sp => sp.OrgId == linkedOrgId, cancellationToken);
                if (linkedProfile is not null)
                {
                    // Backfill SupplierOrgId for consistency
                    if (user.SupplierOrgId != linkedOrgId)
                    {
                        user.SupplierOrgId = linkedOrgId;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    return linkedOrgId;
                }
            }
        }

        // Step 2: Email-based lookup
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var profileByEmail = await db.SupplierProfiles.AsNoTracking()
                .FirstOrDefaultAsync(sp => sp.Email.ToLower() == normalizedEmail, cancellationToken);
            if (profileByEmail is not null)
            {
                user.SupplierOrgId = profileByEmail.OrgId;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return profileByEmail.OrgId;
            }
        }

        // Step 3: Auto-provisioning — last resort
        logger.LogWarning(
            "Auto-provisioning supplier org for user {UserId} (email={Email}, name={FirstName} {LastName})",
            userId, resolvedEmail, firstName, lastName);

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

        // Consume any pending invite for this email during auto-provisioning
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
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
        }

        // Set SupplierOrgId — always, even when User.OrgId is already set (dual-role).
        user.SupplierOrgId = org.Id;
        user.UpdatedAt = DateTime.UtcNow;

        // If the user doesn't have an OrgId at all, set it too (single-role supplier).
        if (user.OrgId is null)
            user.OrgId = org.Id;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Auto-provisioned supplier org {OrgId} for user {UserId}", org.Id, userId);
        return org.Id;
    }

    private async Task<(Org Org, SupplierProfile Profile)?> TryGetExistingSupplierRegistrationAsync(
        User user,
        CancellationToken cancellationToken)
    {
        var candidateOrgIds = new[] { user.SupplierOrgId, user.OrgId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        foreach (var orgId in candidateOrgIds)
        {
            var org = await db.Orgs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orgId && o.OrgType == OrgType.Supplier, cancellationToken);
            if (org is null)
                continue;

            var profile = await db.SupplierProfiles.AsNoTracking()
                .FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);
            if (profile is null)
                continue;

            if (user.SupplierOrgId != orgId)
            {
                user.SupplierOrgId = orgId;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            return (org, profile);
        }

        return null;
    }

    private static bool IsProvisioningSerializationRace(Exception ex) =>
        ex is PostgresException { SqlState: "40001" or "40P01" } ||
        ex.InnerException is not null && IsProvisioningSerializationRace(ex.InnerException);

    public async Task<SupplierDashboard> GetDashboardStatsAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var profile = await db.SupplierProfiles.AsNoTracking()
            .FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);

        if (profile is null)
            return new SupplierDashboard(0, "Unknown", 0, 0, 0, 0, "None", null, null, null, DateTime.UtcNow);

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        // Profile completion: 5 dimensions — identity(=1) + categories + comuni + bio + tos
        var categories = JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? [];
        var comuni = JsonSerializer.Deserialize<string[]>(profile.ComuniJson, JsonOpts) ?? [];
        var hasBio = !string.IsNullOrWhiteSpace(profile.Bio);
        var hasTos = profile.TosAcceptedAt.HasValue;
        int completionSteps = 1 + (categories.Length > 0 ? 1 : 0) + (comuni.Length > 0 ? 1 : 0) + (hasBio ? 1 : 0) + (hasTos ? 1 : 0);
        int profileCompletionPercent = (int)Math.Round(completionSteps / 5.0 * 100);

        // Jobs aggregation
        var jobs = await db.SupplierJobs.AsNoTracking()
            .Where(j => j.SupplierOrgId == orgId)
            .ToListAsync(cancellationToken);

        int totalJobs = jobs.Count;
        int completedJobs = jobs.Count(j => j.Status == SupplierJobStatus.Completed);
        int upcomingJobs = jobs.Count(j =>
            j.Status is SupplierJobStatus.Accepted or SupplierJobStatus.Offered &&
            j.ScheduledStartUtc > now);

        // Availability rate over the next 30 days
        var thirtyDaysFromNow = today.AddDays(29);
        var availRows = await db.SupplierAvailability.AsNoTracking()
            .Where(sa => sa.OrgId == orgId && sa.Date >= today && sa.Date <= thirtyDaysFromNow)
            .ToListAsync(cancellationToken);

        double availabilityRate = 0;
        if (availRows.Count > 0)
        {
            int availableDays = availRows.Count(sa => sa.Available);
            availabilityRate = Math.Round((double)availableDays / availRows.Count, 2);
        }

        return new SupplierDashboard(
            profileCompletionPercent,
            profile.Status.ToString(),
            totalJobs,
            completedJobs,
            upcomingJobs,
            availabilityRate,
            profile.CalendarSyncType.ToString(),
            profile.IcalFeedUrl,
            profile.CalendarLastSyncAt,
            profile.CalendarSyncError,
            profile.UpdatedAt);
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

        // Group by normalized email to detect duplicates. Blank email is not an
        // identity key, so those profiles are never merged together.
        var profilesByEmail = allProfiles
            .GroupBy(sp => NormalizeSupplierEmail(sp.Email))
            .ToList();

        foreach (var group in profilesByEmail)
        {
            var email = group.Key;
            var profiles = group.ToList();

            // Case C: Duplicate profiles — keep the richest one
            if (!string.IsNullOrWhiteSpace(email) && profiles.Count > 1)
            {
                static int Score(SupplierProfile p)
                {
                    int s = 0;
                    if (!string.IsNullOrWhiteSpace(p.LegalName) && p.LegalName != "Fornitore") s += 3;
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
                    details.Add($"Duplicate merged: kept {keeper.OrgId} (score={Score(keeper)}), deleted {victim.OrgId} (score={Score(victim)}) — email='{email}'");
                    logger.LogWarning("FixOrphaned: deleted duplicate {VictimOrgId}, kept {KeeperOrgId}", victim.OrgId, keeper.OrgId);
                }

                profiles = [keeper];
            }

            // Link the surviving profile to its user
            foreach (var profile in profiles)
            {
                // Find the user: by email first, then by OrgId/SupplierOrgId linkage
                User? user = null;
                if (!string.IsNullOrWhiteSpace(email))
                {
                    user = await db.Users
                        .FirstOrDefaultAsync(u => u.Email.ToLower() == email, cancellationToken);
                }

                // Fallback: find user linked via OrgId or SupplierOrgId
                if (user is null)
                {
                    user = await db.Users
                        .FirstOrDefaultAsync(u =>
                            u.SupplierOrgId == profile.OrgId ||
                            (u.OrgId == profile.OrgId && u.SupplierOrgId == null),
                            cancellationToken);
                }

                if (user is null)
                {
                    orphansSkipped++;
                    details.Add($"Orphan: profile {profile.OrgId} (email='{email}') has no matching user");
                    logger.LogWarning("FixOrphaned: no user for profile {OrgId}", profile.OrgId);
                    continue;
                }

                // Set SupplierOrgId on the user (always) and OrgId only if empty
                if (user.SupplierOrgId != profile.OrgId)
                {
                    user.SupplierOrgId = profile.OrgId;
                    usersLinked++;
                    details.Add($"Linked: user {user.Id} SupplierOrgId → {profile.OrgId}");
                }

                // Only set OrgId if the user doesn't already have a host org
                if (user.OrgId is Guid existingOrgId && existingOrgId != profile.OrgId)
                {
                    var existingOrg = await db.Orgs.AsNoTracking()
                        .FirstOrDefaultAsync(o => o.Id == existingOrgId, cancellationToken);
                    if (existingOrg?.OrgType == OrgType.Host)
                    {
                        details.Add($"Dual-role: user {user.Id} keeps host OrgId={existingOrgId}, SupplierOrgId={profile.OrgId}");
                        continue;
                    }
                }

                if (user.OrgId is null)
                {
                    user.OrgId = profile.OrgId;
                    details.Add($"Set OrgId: user {user.Id} OrgId → {profile.OrgId}");
                }

                user.UpdatedAt = DateTime.UtcNow;
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

    private static string NormalizeSupplierEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

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
            var apiBaseUrl = configuration["App:ApiBaseUrl"];
            if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                signupUrl = SupplierInviteEmailBuilder.BuildSignupUrl(apiBaseUrl, invite);
            }
            else
            {
                var frontendBaseUrl = configuration["App:PublicSiteBaseUrl"];
                if (string.IsNullOrWhiteSpace(frontendBaseUrl))
                {
                    throw new InvalidOperationException(
                        "App:ApiBaseUrl o App:PublicSiteBaseUrl non configurato: impossibile inviare l'invito.");
                }
                signupUrl = SupplierInviteEmailBuilder.BuildSignupUrl(frontendBaseUrl, invite);
            }
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
