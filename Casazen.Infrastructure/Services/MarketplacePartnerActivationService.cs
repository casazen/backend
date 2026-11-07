using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplacePartnerActivationService"/> (E09/F02/US03/T01, NF-56, MKT-03, S18, D16, Q-006): the brief of a partner activated on its own
/// site, and the record that the host did it. It reads CasaZen's own tables and writes one kind of row, the <see cref="MarketplaceActivation"/> of kind
/// <c>Partner</c>; it calls no one.
/// </summary>
/// <remarks>
/// <para><b>One row for each org, property and partner.</b> The two partial unique indexes of <c>MarketplaceActivations</c> keep it single (one for the
/// org-level rows, one for the property-level rows): two declarations that race get one row, the loser reads the winner's and returns it.</para>
/// <para><b>Declared, then verified only by a connection.</b> A declaration is the host's word (<c>IsDeclared</c>); it becomes verified only when the
/// partner has a portal in Octorate's catalog and the same property has a proven, Active connection of that portal. The connection is read from the
/// activation rows of CasaZen, and only while the connector is on and the org is connected (with the flag off, nothing is read and the declaration stays a
/// declaration).</para>
/// <para><b>Logs.</b> Ids and the partner code: never a name, an address or a CIN of a property.</para>
/// </remarks>
public sealed class MarketplacePartnerActivationService(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<MarketplacePartnerActivationService> logger) : IMarketplacePartnerActivationService
{
    public async Task<MarketplacePartnerBrief> GetBriefAsync(
        Guid orgId,
        HostScope scope,
        string partnerCode,
        Guid? propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);

        var propertyData = propertyId is { } id ? await PropertyDataAsync(orgId, scope, id, ct) : null;
        var partner = await VisiblePartnerAsync(orgId, partnerCode, ct);

        var texts = await db.MarketplacePartnerTexts.AsNoTracking().Where(t => t.PartnerId == partner.Id).ToListAsync(ct);
        var text = MarketplaceReads.TextOf(texts, lang);
        return new MarketplacePartnerBrief(partner.Code, partner.Name, partner.WebsiteUrl, Lines(text?.ActivationGuide), propertyData);
    }

    public async Task<MarketplacePartnerActivation> DeclareAsync(
        Guid orgId,
        HostScope scope,
        string partnerCode,
        Guid? propertyId,
        DateOnly declaredOn,
        string userId,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The day is judged on the calendar of Rome, the host's: it does not depend on a tenant, so it is checked before anything is read.
        if (declaredOn > timeProvider.TodayInRomeAsDateOnly())
            throw DeclaredDateInvalid();

        await EnsureReachAsync(orgId, scope, propertyId, ct);
        var partner = await VisiblePartnerAsync(orgId, partnerCode, ct);
        var verified = await ConnectionShowsItAsync(orgId, propertyId, partner, ct);
        var declaredAt = declaredOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        for (var attempt = 1; ; attempt++)
        {
            var now = Now();
            var row = await TrackedRowAsync(orgId, propertyId, partner.Code, ct);

            // Declaring twice changes nothing: an activation that is Active (declared or verified) is the answer, and no day given later rewrites it.
            if (row is { Status: MarketplaceActivationStatus.Active })
                return Describe(row);

            if (row is null)
            {
                // The collection of the DbSet, not the navigation of another entity: a row with an id of its own is added, never attached as modified.
                row = new MarketplaceActivation
                {
                    OrgId = orgId,
                    PropertyId = propertyId,
                    Kind = MarketplaceActivationKind.Partner,
                    TargetCode = partner.Code,
                    CreatedAt = now,
                };
                db.MarketplaceActivations.Add(row);
            }

            // A new row, or one that was turned off or is to fix: Active, with the day the host gave. The status is the host's word unless a connection shows it.
            row.Status = MarketplaceActivationStatus.Active;
            row.IsDeclared = !verified;
            row.DeclaredAt = declaredAt;
            row.VerifiedAt = verified ? now : null;
            row.LastErrorCode = null;
            row.LastErrorAt = null;
            row.ChangeRequestedAt = null;
            row.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Two declarations at once: the other one wrote the row first. Read it again; an Active row is returned as it is.
                ForgetTrackedRows();
                continue;
            }

            logger.LogInformation(
                "Partner {PartnerCode} declared Active for org {OrgId} (property {PropertyId}) by {UserId}: activation {ActivationId}, verified by a connection {Verified}",
                partner.Code, orgId, propertyId, userId, row.Id, verified);
            return Describe(row);
        }
    }

    public async Task<MarketplacePartnerActivation> UndeclareAsync(
        Guid orgId,
        HostScope scope,
        string partnerCode,
        Guid? propertyId,
        string userId,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await EnsureReachAsync(orgId, scope, propertyId, ct);

        // No look at the catalog: a partner the admin hid since, or that the plan no longer allows, can still be turned off by the host that has it.
        var code = partnerCode?.Trim();
        if (string.IsNullOrEmpty(code))
            throw ActivationNotFound();

        for (var attempt = 1; ; attempt++)
        {
            var row = await TrackedRowAsync(orgId, propertyId, code, ct) ?? throw ActivationNotFound();
            if (row.Status == MarketplaceActivationStatus.Deactivated)
                return Describe(row);

            // The row stays as history: only its status moves, and the problem it carried is closed with it.
            row.Status = MarketplaceActivationStatus.Deactivated;
            row.LastErrorCode = null;
            row.LastErrorAt = null;
            row.ChangeRequestedAt = null;
            row.UpdatedAt = Now();

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Another request changed the same row meanwhile: read it again, a Deactivated row is returned as it is.
                ForgetTrackedRows();
                continue;
            }

            logger.LogInformation(
                "Partner {PartnerCode} declared turned off for org {OrgId} (property {PropertyId}) by {UserId}: activation {ActivationId} is Deactivated",
                code, orgId, propertyId, userId, row.Id);
            return Describe(row);
        }
    }

    // ─── The checks every method shares ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the caller reaches. A property must be one of the org inside the caller's scope (the 404 of a property that does not exist otherwise). With no
    /// property the declaration is the whole org's, an org-level decision: a caller limited to some properties does not reach it. «No property named»
    /// is never «inside the scope».
    /// </summary>
    private async Task EnsureReachAsync(Guid orgId, HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is { } id)
        {
            await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, id, ct);
            return;
        }

        if (!scope.IsOrgWide)
            throw new DomainForbiddenException(MarketplacePartnerErrorCodes.NeedsWholeOrg, "MarketplacePartnerNeedsWholeOrg");
    }

    /// <summary>
    /// The partner of the catalog the org may see (<see cref="MarketplaceVisibility"/>). A code nobody has, a hidden partner and one the plan or the
    /// country of the org does not allow are the same 404, so a code cannot be guessed.
    /// </summary>
    private async Task<MarketplacePartner> VisiblePartnerAsync(Guid orgId, string? partnerCode, CancellationToken ct)
    {
        var code = partnerCode?.Trim();
        if (string.IsNullOrEmpty(code))
            throw MarketplaceReads.EntryNotFound();

        var org = await MarketplaceReads.LoadOrgAsync(db, orgId, ct);
        var partner = await db.MarketplacePartners.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct);
        if (partner is null || !MarketplaceVisibility.IsVisible(partner, org.PlanTier, org.BillingCountry))
            throw MarketplaceReads.EntryNotFound();

        return partner;
    }

    /// <summary>
    /// The fixed list of fields of a property of the org inside the caller's scope (privacy rule): the query reads these columns and no other, and the CIN
    /// only when <see cref="CinFormat"/> says it is valid. The 404 of a property that does not exist otherwise.
    /// </summary>
    private async Task<MarketplacePartnerPropertyData> PropertyDataAsync(Guid orgId, HostScope scope, Guid propertyId, CancellationToken ct)
    {
        var found = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .InScope(scope)
            .Select(p => new { p.Name, p.Address, p.City, p.PostalCode, p.Bedrooms, p.Bathrooms, p.MaxGuests, p.CinCode })
            .FirstOrDefaultAsync(ct);
        if (found is null)
            throw MarketplaceReads.PropertyNotFound();

        var cin = CinFormat.GetStatus(found.CinCode) == CinStatus.Valid ? CinFormat.Normalize(found.CinCode) : null;
        return new MarketplacePartnerPropertyData(
            found.Name, found.Address, found.City, found.PostalCode, found.Bedrooms, found.Bathrooms, found.MaxGuests, cin);
    }

    /// <summary>
    /// Whether a connection shows the partner active for the property (Q-006): the partner has a portal in Octorate's catalog and the property has an
    /// Active channel or tool of that portal that Octorate proved (not one the host declared). Only CasaZen's own rows are read, and only while the
    /// connector is on and the org is connected: with the flag off the connector does not exist, and the declaration stays a declaration. A declaration
    /// without a property has nothing to compare, so it is never verified by a connection.
    /// </summary>
    private async Task<bool> ConnectionShowsItAsync(Guid orgId, Guid? propertyId, MarketplacePartner partner, CancellationToken ct)
    {
        if (propertyId is not { } id || string.IsNullOrWhiteSpace(partner.OctoratePortalCode))
            return false;

        var octorate = await MarketplaceReads.OctorateStateAsync(db, featureFlags, hostEnvironment, orgId, ct);
        if (octorate is not { Enabled: true, Connected: true })
            return false;

        var portalCode = partner.OctoratePortalCode;
        return await db.MarketplaceActivations.AsNoTracking().AnyAsync(
            a => a.OrgId == orgId
                 && a.PropertyId == id
                 && (a.Kind == MarketplaceActivationKind.Channel || a.Kind == MarketplaceActivationKind.Tool)
                 && a.TargetCode == portalCode
                 && a.Status == MarketplaceActivationStatus.Active
                 && !a.IsDeclared,
            ct);
    }

    // ─── The row ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The org's row of the partner for the property, or its org-level row when no property is named, tracked; <c>null</c> when there is none.</summary>
    private async Task<MarketplaceActivation?> TrackedRowAsync(Guid orgId, Guid? propertyId, string partnerCode, CancellationToken ct)
    {
        var rows = db.MarketplaceActivations.Where(a => a.OrgId == orgId && a.Kind == MarketplaceActivationKind.Partner && a.TargetCode == partnerCode);
        return propertyId is { } id
            ? await rows.FirstOrDefaultAsync(a => a.PropertyId == id, ct)
            : await rows.FirstOrDefaultAsync(a => a.PropertyId == null, ct);
    }

    /// <summary>The rows are read again after a failed save: nothing tracked from the failed attempt may be kept.</summary>
    private void ForgetTrackedRows()
    {
        foreach (var entry in db.ChangeTracker.Entries<MarketplaceActivation>().ToList())
            entry.State = EntityState.Detached;
    }

    private static MarketplacePartnerActivation Describe(MarketplaceActivation row) =>
        new(row.Id, row.TargetCode, row.PropertyId, row.Status, row.IsDeclared, row.DeclaredAt, row.VerifiedAt);

    /// <summary>The lines of a text, trimmed, without the empty ones: how a checklist is stored in one column.</summary>
    private static List<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DomainRuleException DeclaredDateInvalid() =>
        new(MarketplacePartnerErrorCodes.DeclaredDateInvalid, "MarketplaceDeclaredDateInvalid");

    private static NotFoundException ActivationNotFound() =>
        new("The activation is not one of the organization inside the scope of the caller.")
        {
            Code = MarketplaceActivationErrorCodes.ActivationNotFound,
            MessageKey = "MarketplaceActivationNotFound",
        };
}
