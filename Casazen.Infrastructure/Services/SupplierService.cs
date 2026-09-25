using System.Data;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

public partial class SupplierService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    ISafeExternalHttpClient externalHttpClient,
    IOptions<SupplierRegistrationOptions> registrationOptions,
    ILogger<SupplierService> logger) : ISupplierService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<SupplierRegistrationResult> RegisterAsync(
        SupplierRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var email = registration.Email.Trim();
        var comuneCode = registration.ComuneCode.Trim();
        var userId = registration.UserId;
        var accountEmail = registration.AccountEmail?.Trim();

        // A malformed or truncated token is "invalid invite" (422), never a parse error (A4-21).
        string? tokenHash = null;
        if (!string.IsNullOrEmpty(registration.InviteToken))
        {
            if (!SupplierInviteTokens.TryNormalize(registration.InviteToken, out var token))
                throw InviteInvalid();
            tokenHash = SupplierInviteTokens.Hash(token);

            // The invite is accepted by the Auth0 account of the invited email: an anonymous request cannot prove it.
            if (userId is null)
                throw new DomainRuleException("supplier_invite_login_required", "SupplierInviteLoginRequired");
        }

        if (userId is not null && string.IsNullOrEmpty(accountEmail))
            throw new DomainRuleException("supplier_account_email_missing", "SupplierAccountEmailMissing");

        // One registration per user and one acceptance per invite, across requests and instances (TN-4).
        var locks = new List<(PostgresAdvisoryLocks.Scope, string)>();
        if (userId is not null)
            locks.Add((PostgresAdvisoryLocks.Scope.OrgProvisioningUser, userId));
        if (tokenHash is not null)
            locks.Add((PostgresAdvisoryLocks.Scope.SupplierInvite, tokenHash));
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, locks.ToArray());

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
                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken);
                    return new SupplierRegistrationResult(existing.Value.Org, existing.Value.Profile);
                }
            }
        }

        SupplierInviteRecord? invite = null;
        if (tokenHash is not null)
        {
            invite = await db.SupplierInviteRecords.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);
            EnsureInviteUsable(invite);

            // The token is bound to the invited email (account and form) and comune (A4-04).
            if (!EmailsMatch(invite!.Email, accountEmail) || !EmailsMatch(invite.Email, email))
                throw new DomainRuleException("supplier_invite_email_mismatch", "SupplierInviteEmailMismatch");
            if (!string.IsNullOrWhiteSpace(invite.ComuneCode)
                && !string.Equals(invite.ComuneCode.Trim(), comuneCode, StringComparison.OrdinalIgnoreCase))
                throw new DomainRuleException("supplier_invite_comune_mismatch", "SupplierInviteComuneMismatch");

            email = invite.Email.Trim();
            if (!string.IsNullOrWhiteSpace(invite.ComuneCode))
                comuneCode = invite.ComuneCode.Trim();
            invite.IsUsed = true;
        }
        else
        {
            if (userId is not null && !EmailsMatch(accountEmail, email))
                throw new DomainRuleException("supplier_account_email_mismatch", "SupplierAccountEmailMismatch");

            var options = registrationOptions.Value;
            if (!options.SelfServeEnabled)
            {
                logger.LogWarning(
                    "Supplier self-serve registration refused: no pilot comune configured (Suppliers__PilotComuni)");
                throw new DomainRuleException("supplier_self_serve_unavailable", "SupplierSelfServeUnavailable");
            }

            var pilot = options.FindPilotComune(comuneCode)
                ?? throw new DomainRuleException("supplier_comune_not_pilot", "SupplierComuneNotPilot");
            comuneCode = pilot.Code.Trim();
        }

        // One profile per email (SU-14, A4-22): a second registration would create the duplicate that fix-orphaned had
        // to merge. The unique index is the guarantee under concurrency (23505 below); this check answers early.
        if (await IsSupplierEmailTakenAsync(email, cancellationToken))
            throw SupplierEmailTaken();

        var slug = $"supplier-{Guid.NewGuid():N}"[..30];
        var org = new Org
        {
            Name = registration.LegalName,
            Slug = slug,
            DisplayName = registration.LegalName,
            ContactEmail = email,
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);

        var profile = new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = registration.LegalName,
            Phone = registration.Phone,
            ComuniJson = JsonSerializer.Serialize(new[] { comuneCode }, JsonOpts),
        };
        // The invite's categories are codes already (validated when the invite was created, SU-03).
        if (!string.IsNullOrWhiteSpace(invite?.CategoriesJson))
            profile.CategoriesJson = invite.CategoriesJson;

        // Anonymous self-serve (an invite always needs a signed-in account): nobody can be linked now. The response
        // carries a claim token that the account created afterwards presents to ClaimAsync (SU-02, A4-02); the profile
        // is never joined by email alone (A4-23).
        SupplierClaimTicket? claimTicket = null;
        if (userId is null)
        {
            var claimToken = SupplierClaimTokens.Generate();
            claimTicket = new SupplierClaimTicket(claimToken, DateTime.UtcNow.Add(SupplierClaimTokens.Validity));
            profile.ClaimTokenHash = SupplierClaimTokens.Hash(claimToken);
            profile.ClaimTokenExpiresAt = claimTicket.ExpiresAt;
        }

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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SupplierProfileEmailIndex.IsViolation(ex))
        {
            // A parallel registration took the email between the check and the insert.
            throw SupplierEmailTaken();
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        if (invite is not null)
        {
            logger.LogInformation(
                "Supplier org {OrgId} registered for {MaskedEmail} by accepting invite {InviteId}",
                org.Id, LogRedaction.MaskEmail(email), invite.Id);
        }
        else
        {
            logger.LogInformation(
                "Supplier org {OrgId} self-registered for {MaskedEmail} ({Mode})",
                org.Id,
                LogRedaction.MaskEmail(email),
                claimTicket is null ? "signed in, linked" : "anonymous, claim token issued");
        }

        return new SupplierRegistrationResult(org, profile, claimTicket);
    }

    public async Task<SupplierClaimResult> ClaimAsync(SupplierClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var userId = claim.UserId;
        var accountEmail = claim.AccountEmail?.Trim();

        // A malformed or truncated token is "invalid claim" (422), never a parse error.
        string? tokenHash = null;
        if (!string.IsNullOrWhiteSpace(claim.ClaimToken))
        {
            if (!SupplierClaimTokens.TryNormalize(claim.ClaimToken, out var token))
                throw ClaimInvalid();
            tokenHash = SupplierClaimTokens.Hash(token);
        }

        // One supplier link per user, across requests and instances (TN-4); registrations take the same lock.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.OrgProvisioningUser, userId));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        // A repeated claim (double submit, retry after a failed Auth0 role sync) gets the org already linked.
        if (user is not null)
        {
            var existing = await TryGetExistingSupplierRegistrationAsync(user, cancellationToken);
            if (existing is not null)
            {
                var linkedOrgId = existing.Value.Org.Id;
                if (tokenHash is not null)
                {
                    var tokenOrgId = await db.SupplierProfiles.AsNoTracking()
                        .Where(sp => sp.ClaimTokenHash == tokenHash)
                        .Select(sp => (Guid?)sp.OrgId)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (tokenOrgId is Guid otherOrgId && otherOrgId != linkedOrgId)
                        throw new DomainConflictException("supplier_account_already_linked", "SupplierAccountAlreadyLinked");
                }

                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return new SupplierClaimResult(linkedOrgId, NewlyLinked: false);
            }
        }

        if (string.IsNullOrEmpty(accountEmail))
            throw new DomainRuleException("supplier_account_email_missing", "SupplierAccountEmailMissing");
        if (user is null)
            throw new InvalidOperationException($"User {userId} must exist before claiming a supplier profile.");

        SupplierProfile profile;
        string method;
        if (tokenHash is not null)
        {
            profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.ClaimTokenHash == tokenHash, cancellationToken)
                ?? throw ClaimInvalid();
            await LockSupplierClaimAsync(profile.OrgId, cancellationToken);

            // Merged into another profile by fix-orphaned while this claim waited for the lock (SU-14): the token no
            // longer names a profile, and linking it would point the account to a deleted org.
            if (!await db.SupplierProfiles.AsNoTracking().AnyAsync(sp => sp.OrgId == profile.OrgId, cancellationToken))
                throw ClaimInvalid();

            if (await IsSupplierProfileHeldAsync(profile.OrgId, cancellationToken))
                throw new DomainRuleException("supplier_claim_used", "SupplierClaimUsed");
            if (profile.ClaimTokenExpiresAt is not DateTime expiresAt || expiresAt <= DateTime.UtcNow)
                throw new DomainRuleException("supplier_claim_expired", "SupplierClaimExpired");
            // The token proves the registrant; the account must still be the one of the registered email.
            if (!EmailsMatch(profile.Email, accountEmail))
                throw new DomainRuleException("supplier_claim_email_mismatch", "SupplierClaimEmailMismatch");
            method = "claim token";
        }
        else
        {
            // Without the token only an email that Auth0 verified may pick the profile (A4-23, A1-13).
            if (!claim.AccountEmailVerified)
                throw new DomainRuleException("supplier_claim_email_unverified", "SupplierClaimEmailUnverified");

            var normalizedEmail = accountEmail.ToLowerInvariant();
            // Users has no tenant filter (allow-listed identity table): the check spans every org on purpose.
            var candidates = await db.SupplierProfiles.AsNoTracking()
                .Where(sp => sp.Email.ToLower() == normalizedEmail
                             && !db.Users.Any(u => u.SupplierOrgId == sp.OrgId || u.OrgId == sp.OrgId))
                .OrderBy(sp => sp.CreatedAt)
                .Select(sp => sp.OrgId)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
                throw new DomainRuleException("supplier_claim_not_found", "SupplierClaimNotFound");
            if (candidates.Count > 1)
                throw new DomainConflictException("supplier_claim_ambiguous", "SupplierClaimAmbiguous");

            await LockSupplierClaimAsync(candidates[0], cancellationToken);
            // Null when fix-orphaned merged it into another profile while this claim waited for the lock (SU-14).
            profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == candidates[0], cancellationToken)
                ?? throw new DomainRuleException("supplier_claim_not_found", "SupplierClaimNotFound");
            // Taken by a parallel claim while this one waited for the lock.
            if (await IsSupplierProfileHeldAsync(profile.OrgId, cancellationToken))
                throw new DomainRuleException("supplier_claim_used", "SupplierClaimUsed");
            method = "verified email";
        }

        user.SupplierOrgId = profile.OrgId;
        if (user.OrgId is null)
            user.OrgId = profile.OrgId;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} claimed supplier org {OrgId} with a {ClaimMethod}", userId, profile.OrgId, method);
        return new SupplierClaimResult(profile.OrgId, NewlyLinked: true);
    }

    private static DomainRuleException ClaimInvalid() =>
        new("supplier_claim_invalid", "SupplierClaimInvalid");

    /// <summary>Serializes the claims of one profile: at most one account is linked to it (joins the open transaction).</summary>
    private async Task LockSupplierClaimAsync(Guid supplierOrgId, CancellationToken cancellationToken)
    {
        await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SupplierClaim, supplierOrgId.ToString("N")));
    }

    /// <summary>True when an account is already linked to the supplier org (claimed, registered signed in, invited).</summary>
    private Task<bool> IsSupplierProfileHeldAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(
            u => u.SupplierOrgId == supplierOrgId || u.OrgId == supplierOrgId, cancellationToken);

    public async Task<SupplierInvitePreview> GetInviteAsync(string? inviteToken, CancellationToken cancellationToken = default)
    {
        if (!SupplierInviteTokens.TryNormalize(inviteToken, out var token))
            throw InviteInvalid();

        var tokenHash = SupplierInviteTokens.Hash(token);
        var invite = await db.SupplierInviteRecords.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);
        EnsureInviteUsable(invite);

        return new SupplierInvitePreview(
            invite!.Email.Trim(),
            invite.ComuneCode.Trim(),
            registrationOptions.Value.FindPilotComune(invite.ComuneCode)?.Name.Trim(),
            DeserializeStrings(invite.CategoriesJson),
            invite.ExpiresAt);
    }

    private static DomainRuleException InviteInvalid() =>
        new("supplier_invite_invalid", "SupplierInviteInvalid");

    private static void EnsureInviteUsable(SupplierInviteRecord? invite)
    {
        if (invite is null)
            throw InviteInvalid();
        if (invite.IsUsed)
            throw new DomainRuleException("supplier_invite_used", "SupplierInviteUsed");
        if (invite.ExpiresAt <= DateTime.UtcNow)
            throw new DomainRuleException("supplier_invite_expired", "SupplierInviteExpired");
    }

    private static bool EmailsMatch(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DomainConflictException SupplierEmailTaken() =>
        new("supplier_email_taken", "SupplierEmailTaken");

    /// <summary>
    /// True when a supplier profile already has <paramref name="email"/> (trimmed, case-insensitive), the key of the
    /// unique index <see cref="SupplierProfileEmailIndex"/>. A blank email is never taken.
    /// </summary>
    private async Task<bool> IsSupplierEmailTakenAsync(string? email, CancellationToken cancellationToken)
    {
        var normalized = SupplierProfileEmailIndex.Normalize(email);
        if (normalized.Length == 0)
            return false;

        return await db.SupplierProfiles.AsNoTracking()
            .AnyAsync(sp => sp.Email.Trim().ToLower() == normalized, cancellationToken);
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
        // Only category codes are stored (SU-03): an Italian label or unknown value is rejected (422), never saved.
        var categoryCodes = categories is null ? null : ServiceCategories.RequireAll(categories);

        var profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);
        if (profile is null)
            return null;

        if (legalName is not null) profile.LegalName = legalName;
        if (vatNumber is not null) profile.VatNumber = vatNumber.Length == 0 ? null : vatNumber;
        if (phone is not null) profile.Phone = phone;
        if (categoryCodes is not null) profile.CategoriesJson = JsonSerializer.Serialize(categoryCodes, JsonOpts);
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

        // Same lock as the iCal sync (SU-15): a sync running meanwhile never writes or frees the same days.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, CalendarSyncService.AvailabilityLock(orgId));

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
                    Source = SupplierAvailabilitySource.Manual,
                });
            }
            else if (record.Available != available)
            {
                // A change by the supplier makes the day manual: the iCal sync no longer frees it.
                record.Available = available;
                record.Source = SupplierAvailabilitySource.Manual;
            }

            // Same value: the day keeps its source. The page saves every visible day, and a busy day of the feed saved
            // unchanged must stay a feed day (freed when its event leaves the calendar).
            count++;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return count;
    }

    public async Task<IReadOnlyList<SupplierProfile>> GetActiveByComune(string comuneCode, string? category, CancellationToken cancellationToken = default)
    {
        // Filter by category code (SU-03). An unknown code is an error (422), not an empty list that hides the mistake;
        // a supplier matches only when it declared the code (no categories = no match).
        var categoryCode = string.IsNullOrWhiteSpace(category) ? null : ServiceCategories.Require(category);

        var all = await db.SupplierProfiles
            .Where(sp => sp.Status == SupplierStatus.Active)
            .ToListAsync(cancellationToken);

        return all.Where(sp =>
        {
            var comuni = JsonSerializer.Deserialize<string[]>(sp.ComuniJson, JsonOpts) ?? [];
            if (!comuni.Any(c => ItalianComuneRegistry.Matches(comuneCode, c)))
                return false;

            if (categoryCode is not null)
                return DeserializeStrings(sp.CategoriesJson).Contains(categoryCode, StringComparer.Ordinal);

            return true;
        }).ToList();
    }

    public async Task<IReadOnlyList<UnmappedServiceCategory>> GetUnmappedCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var unmapped = new List<UnmappedServiceCategory>();

        var profiles = await db.SupplierProfiles
            .AsNoTracking()
            .OrderBy(sp => sp.OrgId)
            .Select(sp => new { sp.OrgId, sp.CategoriesJson })
            .ToListAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            unmapped.AddRange(DeserializeStrings(profile.CategoriesJson)
                .Where(value => !ServiceCategories.IsKnown(value))
                .Select(value => new UnmappedServiceCategory("supplier_profile", profile.OrgId, value)));
        }

        var invites = await db.SupplierInviteRecords
            .AsNoTracking()
            .Where(i => i.CategoriesJson != null)
            .OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.CategoriesJson })
            .ToListAsync(cancellationToken);
        foreach (var invite in invites)
        {
            unmapped.AddRange(DeserializeStrings(invite.CategoriesJson)
                .Where(value => !ServiceCategories.IsKnown(value))
                .Select(value => new UnmappedServiceCategory("supplier_invite", invite.Id, value)));
        }

        var known = ServiceCategories.All.ToList();
        // Platform admin report across every host org (the endpoint is AdminOnly); ServiceRequest has no tenant filter.
        var requests = await db.ServiceRequests
            .AsNoTracking()
            .Where(sr => !known.Contains(sr.Category))
            .OrderBy(sr => sr.CreatedAt)
            .Select(sr => new { sr.Id, sr.Category })
            .ToListAsync(cancellationToken);
        unmapped.AddRange(requests.Select(sr => new UnmappedServiceCategory("service_request", sr.Id, sr.Category)));

        return unmapped;
    }

    /// <summary>String items of a JSON array column; anything else (not an array, non-string items) is ignored.</summary>
    private static IReadOnlyList<string> DeserializeStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<SupplierInvite> CreateInviteAsync(
        string email,
        string comuneCode,
        IEnumerable<string>? categories,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var categoryCodes = categories is null ? null : ServiceCategories.RequireAll(categories);

        email = email.Trim();
        comuneCode = comuneCode.Trim();

        // Invites created before SU-01 (no token hash) can no longer be accepted: they do not block a new one.
        var existing = await db.SupplierInviteRecords
            .FirstOrDefaultAsync(
                i => i.Email == email && i.TokenHash != null && !i.IsUsed && i.ExpiresAt > DateTime.UtcNow,
                cancellationToken);

        if (existing is not null)
            throw new InvalidOperationException($"Pending invite already exists for {email}");

        // Accepting the invite creates a profile with this email: with a profile already there it could never succeed
        // (SU-14). The owner of that profile links it with the claim instead.
        if (await IsSupplierEmailTakenAsync(email, cancellationToken))
            throw SupplierEmailTaken();

        // The token only travels in the email; the database keeps its hash (A4-04).
        var token = SupplierInviteTokens.Generate();
        var invite = new SupplierInviteRecord
        {
            Email = email,
            TokenHash = SupplierInviteTokens.Hash(token),
            ComuneCode = comuneCode,
            CategoriesJson = categoryCodes is not null
                ? JsonSerializer.Serialize(categoryCodes, JsonOpts)
                : null,
            Message = message,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        };

        // Rendered before saving: a missing App:PublicSiteBaseUrl is a configuration error, not an invite with a wrong link.
        var inviteEmail = BuildInviteEmail(invite, token);

        db.SupplierInviteRecords.Add(invite);
        await db.SaveChangesAsync(cancellationToken);

        if (!emailQueue.Enqueue(invite.Email, inviteEmail, EmailTemplates.Names.SupplierInvite))
            logger.LogWarning("Supplier invite {InviteId} created but its email was not queued", invite.Id);

        logger.LogInformation(
            "Admin invite {InviteId} created for {MaskedEmail}, expires {ExpiresAt}",
            invite.Id, LogRedaction.MaskEmail(email), invite.ExpiresAt);
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
            catch (DbUpdateException ex) when (SupplierProfileEmailIndex.IsViolation(ex))
            {
                // Another account provisioned or registered a profile with this email in parallel (SU-14).
                throw SupplierEmailTaken();
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

        // Only the contact of a provisioned profile: never used to find one (A4-23).
        var resolvedEmail = string.IsNullOrWhiteSpace(email) ? user.Email : email;

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

        // No email lookup (A4-23, A1-13): the email of the token is not proof of owning a profile registered with it,
        // and an unverified Auth0 account could take over that supplier. An existing profile is joined only through
        // its invite (RegisterAsync) or an explicit claim (ClaimAsync: claim token, or verified email).

        // Step 2: Auto-provisioning — last resort (a Supplier role given by hand, without any profile).
        // Never a second profile for an email that already has one (SU-14, A4-22: that is how the duplicates were
        // born): the account links the existing profile with the claim (token or verified email, SU-02) instead.
        if (await IsSupplierEmailTakenAsync(resolvedEmail, cancellationToken))
        {
            logger.LogWarning(
                "Supplier org not provisioned for user {UserId}: a supplier profile already has the email {MaskedEmail}",
                userId, LogRedaction.MaskEmail(resolvedEmail));
            throw SupplierEmailTaken();
        }

        logger.LogWarning(
            "Auto-provisioning supplier org for user {UserId} (email={MaskedEmail})",
            userId, LogRedaction.MaskEmail(resolvedEmail));

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
            Email = resolvedEmail.Trim(),
            LegalName = displayName,
            Phone = string.Empty,
        };
        db.SupplierProfiles.Add(profile);

        // A pending invite for the same email is left alone: it is accepted only with its token (SU-01), never
        // consumed by an account that merely shows the invited email (A4-23).

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
            return new SupplierDashboard(0, "Unknown", 0, 0, 0, 0, "None", null, null, null, nameof(SupplierCalendarSyncStatus.None), DateTime.UtcNow);

        var now = DateTime.UtcNow;
        var today = TimeProvider.System.TodayInRomeAsDateOnly();

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
            profile.CalendarSyncStatus.ToString(),
            profile.UpdatedAt);
    }

    public async Task<SupplierProfile?> UpdateCalendarSyncAsync(
        Guid orgId,
        CalendarSyncType syncType,
        string? icalFeedUrl,
        string? calendarSyncError,
        CancellationToken cancellationToken = default)
    {
        // The feed is downloaded by the server: only an external https URL is accepted (FD-16, A4-10 / A9-32).
        if (icalFeedUrl is not null && !externalHttpClient.TryValidateUrl(icalFeedUrl, out _))
        {
            throw new DomainRuleException(
                ICalErrorCodes.InvalidUrl,
                ICalErrorCodes.MessageKey(ICalErrorCodes.InvalidUrl));
        }

        var profile = await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken);
        if (profile is null)
            return null;

        profile.CalendarSyncType = syncType;
        profile.IcalFeedUrl = icalFeedUrl?.Trim();
        profile.CalendarSyncError = calendarSyncError;
        // An iCal URL is synced by a queued job (SU-15): the caller queues it, the state says so until it has run.
        profile.CalendarSyncStatus = syncType == CalendarSyncType.ICalFeed && !string.IsNullOrWhiteSpace(profile.IcalFeedUrl)
            ? SupplierCalendarSyncStatus.Syncing
            : SupplierCalendarSyncStatus.None;
        profile.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private EmailContent BuildInviteEmail(SupplierInviteRecord invite, string token) =>
        EmailTemplates.SupplierInvite(
            EmailTemplates.DefaultCulture,
            invite.Email,
            DescribeComune(invite.ComuneCode),
            invite.Message,
            publicSiteLinks.SupplierInviteSignup(token),
            invite.ExpiresAt);

    /// <summary>
    /// "Name (code)" when the comune is a configured pilot comune, otherwise the code. <c>ItalianComuneRegistry</c> is
    /// not used: it knows 12 comuni and maps F205 to Firenze while F205 is Milano (A4-12, SU-04).
    /// </summary>
    private string DescribeComune(string comuneCode)
    {
        var code = comuneCode.Trim();
        var name = registrationOptions.Value.FindPilotComune(code)?.Name.Trim();
        return string.IsNullOrEmpty(name) ? code : $"{name} ({code})";
    }
}
