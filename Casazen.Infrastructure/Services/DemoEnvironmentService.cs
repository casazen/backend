using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Demo environment of a non-production installation (docs/runbooks/demo-environment.md). Invented CIN, PDF documents
/// and checklists are valid for the activation rules and marked <see cref="Property.IsTestData"/>. The reset deletes
/// only rows marked <see cref="TestDataOrigins.Showcase"/>.
/// </summary>
/// <remarks>
/// <paramref name="scopeFactory"/>, <paramref name="contactIndex"/> and <paramref name="featureFlags"/> serve the CRM profiles of the showcase
/// (E07/F01/US02/T03): the profiles, contacts and proposed merges are made by the CRM sync itself, in a scope under the demo org, from the
/// bookings the reset has just created, and only when the sync is open (<see cref="CrmProfileSyncGate"/>: <c>Features:CrmMarketing</c> on and the
/// key of the index set).
/// </remarks>
public class DemoEnvironmentService(
    AppDbContext db,
    IFileStorage storage,
    IPropertyComplianceStatusService compliance,
    IOptions<ComplianceOptions> complianceOptions,
    IDemoEnvironmentGate gate,
    ILogger<DemoEnvironmentService> logger,
    TimeProvider clock,
    IServiceScopeFactory scopeFactory,
    ICrmContactIndex contactIndex,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment) : IDemoEnvironmentService
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    public async Task<DemoEnvironmentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!gate.IsAvailable)
        {
            return new DemoEnvironmentStatus(
                false, false, DemoShowcaseCatalog.Version, null, 0, DemoShowcaseCatalog.Scenarios);
        }

        var settings = await SettingsAsync(cancellationToken);
        var showcase = await Markers()
            .CountAsync(
                m => m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.Property,
                cancellationToken);
        return Status(settings, showcase);
    }

    public async Task<DemoEnvironmentStatus> SetEnabledAsync(
        bool enabled, string actorUserId, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var settings = await SettingsAsync(cancellationToken);
        settings.Enabled = enabled;
        settings.UpdatedAt = UtcNow();
        settings.LastResetBy = actorUserId;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Demo environment {State} by {Actor}", enabled ? "enabled" : "disabled", actorUserId);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DemoOrgListItem>> ListOrgsAsync(CancellationToken cancellationToken = default)
    {
        if (!gate.IsAvailable)
            return [];

        return await db.Orgs.AsNoTracking()
            .OrderBy(o => o.Name)
            .Select(o => new DemoOrgListItem(o.Id, o.Name, o.Slug))
            .ToListAsync(cancellationToken);
    }

    public async Task<DemoComplianceResult> ApplyTestComplianceAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);
        await RequireOrgAsync(orgId, cancellationToken);

        var properties = await Properties(orgId).Where(p => p.RentalMode == RentalMode.Short).ToListAsync(cancellationToken);
        var drafts = await DraftIdsAsync(orgId, cancellationToken);
        var used = await UsedCinsAsync(orgId, cancellationToken);
        int published = 0, already = 0, filled = 0;

        foreach (var property in properties)
        {
            if (drafts.Contains(property.Id))
                continue;

            var changed = await FillComplianceAsync(property, used, TestDataOrigins.Compliance, property.Id.ToString("N")[..8], cancellationToken);
            if (changed)
                filled++;
            else
                already++;

            await db.SaveChangesAsync(cancellationToken);
            if (property.ComplianceStatus == PropertyComplianceStatus.Active)
                continue;

            var check = await compliance.ActivateAsync(property.Id, cancellationToken);
            if (check.Status == PropertyComplianceStatus.Active)
                published++;
        }

        return new DemoComplianceResult(properties.Count, published, already, filled);
    }

    public async Task<DemoResetResult> ResetAsync(
        Guid orgId,
        string? confirmation,
        bool removeUnpublished,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        var settings = await EnsureEnabledAsync(cancellationToken);
        await RequireOrgAsync(orgId, cancellationToken);
        if (!string.Equals(confirmation, DemoShowcaseCatalog.ResetConfirmation, StringComparison.Ordinal))
            throw new DomainRuleException("demo_reset_confirmation", "DemoResetConfirmation");

        // The drafts, the popup alerts, the tourist tax payments, the marketplace rows and the CRM profiles first: removing the showcase
        // properties drops every showcase marker of the org, and these are found by theirs.
        await RemoveShowcaseWizardDraftsAsync(orgId, cancellationToken);
        await RemoveShowcasePopupAlertsAsync(orgId, cancellationToken);
        await RemoveShowcaseTouristTaxRemittancesAsync(orgId, cancellationToken);
        await RemoveShowcaseMarketplaceAsync(orgId, cancellationToken);
        await RemoveShowcaseTouristTaxRulesAsync(orgId, cancellationToken);
        await RemoveShowcaseMessageThreadsAsync(orgId, cancellationToken);
        await RemoveShowcaseMessageTemplatesAsync(orgId, cancellationToken);
        await RemoveShowcaseCrmAsync(orgId, cancellationToken);
        await RemoveShowcaseFiscalIssuerProfilesAsync(orgId, cancellationToken);
        await RemoveShowcaseOnboardingAsync(orgId, cancellationToken);
        var removed = await RemoveShowcaseAsync(orgId, cancellationToken);
        await RemoveShowcaseHostInvitesAsync(orgId, cancellationToken);
        var unpublished = removeUnpublished ? await RemoveUnpublishedAsync(orgId, cancellationToken) : 0;
        var ownerId = await OwnerIdAsync(orgId, cancellationToken);
        var created = 0;
        foreach (var template in DemoShowcaseCatalog.Properties)
        {
            await CreateShowcasePropertyAsync(orgId, ownerId, template, cancellationToken);
            created++;
        }

        // NF-88, NF-89: the CRM profiles, contacts and proposed merges of the showcase guests come from the bookings just created.
        await SeedCrmProfilesAsync(orgId, cancellationToken);

        // NF-61, NF-64: the conversations of the hub of messages are about the houses and stays just created, so they come after them.
        foreach (var thread in DemoShowcaseCatalog.MessageThreads)
            await CreateMessageThreadAsync(orgId, ownerId, thread, cancellationToken);

        // E05/F02/US02/T02: the message templates of the demo host point at no house or stay, so they can come anywhere after the removal above.
        foreach (var messageTemplate in DemoShowcaseCatalog.HostMessageTemplates)
            await CreateHostMessageTemplateAsync(orgId, messageTemplate, cancellationToken);

        // NF-90: the welcome link (the QR code) of the demo house, which the houses just created make possible.
        foreach (var welcomeLink in DemoShowcaseCatalog.WelcomeLinks)
            await CreateWelcomeLinkAsync(orgId, ownerId, welcomeLink, cancellationToken);

        // E10/F01/US01/T04: the activation with Octorate of the demo org names houses just created, so it comes after them.
        foreach (var activation in DemoShowcaseCatalog.OnboardingActivations)
            await CreateOnboardingActivationAsync(orgId, ownerId, activation, cancellationToken);

        foreach (var invite in DemoShowcaseCatalog.HostInvites)
            await CreateHostInviteAsync(orgId, ownerId, invite, cancellationToken);

        foreach (var draft in DemoShowcaseCatalog.WizardDrafts)
            await CreateWizardDraftAsync(orgId, ownerId, draft, cancellationToken);

        // NF-100: the tourist tax payments of the demo host name the comune of a house just created, so they come after the houses.
        foreach (var remittance in DemoShowcaseCatalog.TouristTaxRemittances)
            await CreateTouristTaxRemittanceAsync(orgId, ownerId, remittance, cancellationToken);

        // FIS-02, NF-82: the profile of the issuer of the demo org (one for each org, skipped when the org has its own).
        foreach (var profile in DemoShowcaseCatalog.FiscalIssuerProfiles)
            await CreateFiscalIssuerProfileAsync(orgId, ownerId, profile, cancellationToken);

        // NF-08: the popup alerts of the demo host point at the stays and houses just created, so they come last.
        foreach (var alert in DemoShowcaseCatalog.PopupAlerts)
            await CreatePopupAlertAsync(orgId, ownerId, alert, cancellationToken);

        // E09/F01: the test partner of the marketplace catalog, then the activation of it that the demo host declared (it names the partner by code).
        foreach (var partner in DemoShowcaseCatalog.MarketplacePartners)
            await CreateMarketplacePartnerAsync(orgId, ownerId, partner, cancellationToken);

        foreach (var activation in DemoShowcaseCatalog.MarketplaceActivations)
            await CreateMarketplaceActivationAsync(orgId, activation, cancellationToken);

        // NF-46: the channel rule of the invented comune of the demo (platform data, created only when missing).
        foreach (var rule in DemoShowcaseCatalog.TouristTaxChannelRules)
            await CreateTouristTaxChannelRuleAsync(orgId, rule, cancellationToken);

        await ApplyShowcaseBankLinkAsync(orgId, cancellationToken);

        settings.LastResetAt = UtcNow();
        settings.LastResetBy = actorUserId;
        settings.TemplateVersion = DemoShowcaseCatalog.Version;
        settings.UpdatedAt = settings.LastResetAt.Value;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Demo showcase of org {OrgId} reset: {Removed} removed, {Created} created, {Unpublished} unpublished removed",
            orgId, removed, created, unpublished);
        return new DemoResetResult(removed, created, unpublished);
    }

    private async Task<int> RemoveShowcaseAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var propertyIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.Property)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (propertyIds.Count == 0)
            return 0;

        if (await db.LeaseContracts.IgnoreQueryFilters([Tenant]).AnyAsync(l => propertyIds.Contains(l.PropertyId), cancellationToken))
            throw new DomainRuleException("demo_reset_blocked", "DemoResetBlocked");

        var files = await db.PropertyDocuments.IgnoreQueryFilters([Tenant])
            .Where(d => propertyIds.Contains(d.PropertyId))
            .Select(d => d.StorageUrl)
            .ToListAsync(cancellationToken);
        // NF-52: the files of the receipts uploaded for these houses go too (their rows cascade with the houses).
        files.AddRange(await db.ManualComplianceProofs.IgnoreQueryFilters([Tenant])
            .Where(p => p.OrgId == orgId && propertyIds.Contains(p.PropertyId))
            .Select(p => p.FileKey)
            .ToListAsync(cancellationToken));
        var guestIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.Guest)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Bookings.IgnoreQueryFilters([Tenant]).Where(b => propertyIds.Contains(b.PropertyId)).ExecuteDeleteAsync(cancellationToken);
            if (guestIds.Count > 0)
                await db.Guests.IgnoreQueryFilters([Tenant]).Where(g => guestIds.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken);
            await db.Properties.IgnoreQueryFilters([Tenant, AppDbContext.SoftDeleteQueryFilter])
                .Where(p => propertyIds.Contains(p.Id))
                .ExecuteDeleteAsync(cancellationToken);
            await Markers().Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Demo showcase of org {OrgId} could not be removed", orgId);
            throw new DomainRuleException("demo_reset_blocked", "DemoResetBlocked");
        }

        foreach (var file in files)
        {
            if (!StorageKeys.IsValid(file))
                continue;
            try
            {
                await storage.DeleteAsync(StorageBucket.Private, file, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Demo document {Key} was left in storage after the reset", file);
            }
        }

        return propertyIds.Count;
    }

    /// <summary>Deletes the host invites (and their audit lines) the showcase wrote for the org, found by their markers.</summary>
    private async Task RemoveShowcaseHostInvitesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var inviteIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.HostInvite)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (inviteIds.Count == 0)
            return;

        await db.HostInviteAuditEntries.Where(e => inviteIds.Contains(e.InviteId)).ExecuteDeleteAsync(cancellationToken);
        await db.HostInviteRecords.Where(i => inviteIds.Contains(i.Id)).ExecuteDeleteAsync(cancellationToken);
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.HostInvite)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Deletes the wizard drafts the showcase wrote for the org, found by their markers (invented rows: nothing of a real person).</summary>
    private async Task RemoveShowcaseWizardDraftsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var draftIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.WizardDraft)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (draftIds.Count == 0)
            return;

        await db.WizardDrafts.IgnoreQueryFilters([Tenant]).Where(d => draftIds.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken);
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.WizardDraft)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// One saved draft of the demo host, split as the service splits a real one (the personal fields go to the encrypted half). Skipped when
    /// the host already has a draft of that wizard (a real one, which the showcase never touches): there is one per person and wizard.
    /// </summary>
    private async Task CreateWizardDraftAsync(
        Guid orgId, string ownerId, DemoWizardDraftTemplate template, CancellationToken cancellationToken)
    {
        var taken = await db.WizardDrafts.IgnoreQueryFilters([Tenant])
            .AnyAsync(d => d.OrgId == orgId && d.UserId == ownerId && d.WizardType == template.Type && d.EntityId == null, cancellationToken);
        if (taken)
        {
            logger.LogInformation("Demo wizard draft {Key} skipped: the host already has a draft of that wizard", template.Key);
            return;
        }

        using var data = System.Text.Json.JsonDocument.Parse(template.DataJson);
        var parts = WizardDraftPii.Split(data.RootElement);
        var savedAt = UtcNow().AddDays(template.SavedOffsetDays);
        var draft = new WizardDraft
        {
            OrgId = orgId,
            UserId = ownerId,
            WizardType = template.Type,
            Version = 1,
            DraftJson = parts.PlainJson,
            EncryptedFieldsJson = parts.PersonalJson,
            LastSavedAt = savedAt,
            ExpiresAt = savedAt.AddDays(WizardDraftLimits.RetentionDays),
        };
        db.WizardDrafts.Add(draft);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.WizardDraft, draft.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// One invite of the demo host, in the state of its template. The token is random and thrown away: the link of a demo invite
    /// does not exist, so it can be neither opened nor accepted.
    /// </summary>
    private async Task CreateHostInviteAsync(
        Guid orgId, string ownerId, DemoHostInviteTemplate template, CancellationToken cancellationToken)
    {
        var created = UtcNow().AddDays(template.CreatedOffsetDays);
        var invite = new HostInviteRecord
        {
            Email = template.Email,
            TokenHash = Casazen.Core.Suppliers.SupplierInviteTokens.Hash(Casazen.Core.Suppliers.SupplierInviteTokens.Generate()),
            Message = template.Message,
            InvitedByUserId = ownerId,
            InvitedByOrgId = orgId,
            CreatedAt = created,
            ExpiresAt = template.State == DemoHostInviteState.Pending
                ? UtcNow().Add(Casazen.Core.HostInvites.HostInviteRules.Validity)
                : created.Add(Casazen.Core.HostInvites.HostInviteRules.Validity),
        };
        if (template.State == DemoHostInviteState.Used)
        {
            invite.IsUsed = true;
            invite.UsedAt = created.AddHours(2);
            invite.UsedByUserId = DemoSafetyChecklist.Actor;
        }

        db.HostInviteRecords.Add(invite);
        db.HostInviteAuditEntries.Add(new HostInviteAuditEntry
        {
            InviteId = invite.Id,
            Action = HostInviteAuditAction.Created,
            ActorUserId = ownerId,
            ActorOrgId = orgId,
            OccurredAt = created,
        });
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.HostInvite, invite.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// Deletes the popup alerts the showcase wrote for the org, found by their markers. They point at the demo stays and houses by id and
    /// have no foreign key to them, so they are removed by hand and before the markers of the showcase are (NF-08, ADR-025).
    /// </summary>
    private async Task RemoveShowcasePopupAlertsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var alertIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.PopupAlert)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (alertIds.Count == 0)
            return;

        await db.PopupAlerts.IgnoreQueryFilters([Tenant]).Where(a => alertIds.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.PopupAlert)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// One popup alert of the demo host, in the state of its template (open, or snoozed until some hours from now), about a stay or a
    /// house the showcase has just created (found by the template key of its marker). Written for the owner of the org. Skipped, with a
    /// warning, when the stay or house was not created or the owner has no account row (the alert would point at no user).
    /// </summary>
    private async Task CreatePopupAlertAsync(
        Guid orgId, string ownerId, DemoPopupAlertTemplate template, CancellationToken cancellationToken)
    {
        var aboutBooking = template.BookingKey is not null;
        var markerType = aboutBooking ? TestDataEntities.Booking : TestDataEntities.Property;
        var markerKey = template.BookingKey ?? template.PropertyKey;
        var entityId = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == markerType && m.TemplateKey == markerKey)
            .Select(m => m.EntityId)
            .FirstOrDefaultAsync(cancellationToken);
        if (entityId == Guid.Empty || !await db.Users.AsNoTracking().AnyAsync(u => u.Id == ownerId, cancellationToken))
        {
            logger.LogWarning(
                "Demo popup alert {Key} of org {OrgId} was skipped: the stay or house it names, or the owner account, does not exist",
                template.Key,
                orgId);
            return;
        }

        var now = UtcNow();
        var alert = new PopupAlert
        {
            OrgId = orgId,
            UserId = ownerId,
            AlertType = template.Type,
            EntityType = aboutBooking ? PopupAlert.BookingEntityType : PopupAlert.PropertyEntityType,
            EntityId = entityId,
            Urgency = template.Urgency,
            CreatedAt = now,
            RemindAt = template.SnoozedForHours is { } hours ? now.AddHours(hours) : null,
        };
        db.PopupAlerts.Add(alert);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.PopupAlert, alert.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>Deletes the profile of the issuer the showcase wrote for the org, found by its marker (FIS-02). A real profile has no marker and is never touched.</summary>
    private async Task RemoveShowcaseFiscalIssuerProfilesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var profileIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.FiscalIssuerProfile)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (profileIds.Count == 0)
            return;

        await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters([Tenant]).Where(p => p.OrgId == orgId && profileIds.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.FiscalIssuerProfile)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The profile of the issuer of the demo org, complete and invented (FIS-02, NF-82). There is one for each org: it is skipped, with a line in the
    /// log, when the org already has one (a real one, which the showcase never touches or replaces).
    /// </summary>
    private async Task CreateFiscalIssuerProfileAsync(
        Guid orgId, string ownerId, DemoFiscalIssuerProfileTemplate template, CancellationToken cancellationToken)
    {
        if (await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters([Tenant]).AnyAsync(p => p.OrgId == orgId, cancellationToken))
        {
            logger.LogInformation("Demo issuer profile {Key} skipped: org {OrgId} already has a profile of its own", template.Key, orgId);
            return;
        }

        var now = UtcNow();
        var profile = new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            IssuerMode = template.Mode,
            DocumentRegime = template.Regime,
            LegalName = template.LegalName,
            AddressStreet = template.AddressStreet,
            AddressCivic = template.AddressCivic,
            PostalCode = template.PostalCode,
            City = template.City,
            Province = template.Province,
            CountryCode = OrgFiscalIssuerProfile.DefaultCountryCode,
            FatturaPaRegimeCode = template.FatturaPaRegimeCode,
            VatRegistered = FiscalDocumentRules.IsVatRegisteredRegime(template.Regime),
            ConfirmedAt = now.AddDays(template.ConfirmedOffsetDays),
            UpdatedAt = now,
            UpdatedByUserId = ownerId,
        };
        db.OrgFiscalIssuerProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.FiscalIssuerProfile, profile.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// Deletes the tourist tax payments the showcase wrote for the org and their invented receipts, found by their markers (NF-100). A real
    /// payment has no marker and is never touched; the files are removed after the rows, and one that cannot be is only logged.
    /// </summary>
    private async Task RemoveShowcaseTouristTaxRemittancesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var remittanceIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.TouristTaxRemittance)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (remittanceIds.Count == 0)
            return;

        var files = await db.TouristTaxRemittances.IgnoreQueryFilters([Tenant])
            .Where(r => r.OrgId == orgId && remittanceIds.Contains(r.Id) && r.ReceiptStorageKey != null)
            .Select(r => r.ReceiptStorageKey!)
            .ToListAsync(cancellationToken);
        await db.TouristTaxRemittances.IgnoreQueryFilters([Tenant])
            .Where(r => r.OrgId == orgId && remittanceIds.Contains(r.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.TouristTaxRemittance)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var file in files)
        {
            if (!StorageKeys.IsValid(file))
                continue;
            try
            {
                await storage.DeleteAsync(StorageBucket.Private, file, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Demo tourist tax receipt {Key} was left in storage after the reset", file);
            }
        }
    }

    /// <summary>
    /// One tourist tax payment of the demo host, with its invented receipt (a PDF that says it is test data) in the private bucket, for the
    /// comune of a house the showcase has just created (found by the template key of its marker). Skipped, with a warning, when the house was
    /// not created. The same rules as a payment recorded through the service: an amount above zero, a period that is not backwards, a day that is
    /// not after today in Europe/Rome (the template says so, and <c>DemoShowcaseTests</c> checks it).
    /// </summary>
    /// <remarks>Internal so that a test can seed the payment of one house of the catalog (<c>DemoDocumentArchiveSeedTests</c>).</remarks>
    internal async Task CreateTouristTaxRemittanceAsync(
        Guid orgId, string ownerId, DemoTouristTaxRemittanceTemplate template, CancellationToken cancellationToken)
    {
        var propertyId = await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && m.EntityType == TestDataEntities.Property
                && m.TemplateKey == template.PropertyKey)
            .Select(m => m.EntityId)
            .FirstOrDefaultAsync(cancellationToken);
        var city = propertyId == Guid.Empty
            ? null
            : await Properties(orgId).Where(p => p.Id == propertyId).Select(p => p.City).FirstOrDefaultAsync(cancellationToken);
        var comuneKey = TouristTaxSummaryRules.ComuneKeyOf(city);
        if (city is null || comuneKey.Length == 0)
        {
            logger.LogWarning(
                "Demo tourist tax payment {Key} of org {OrgId} was skipped: the house it names was not created",
                template.Key,
                orgId);
            return;
        }

        var today = clock.TodayInRomeAsDateOnly();
        var remittanceId = Guid.NewGuid();
        var pdf = DemoDocumentPdf.Create(
            "RICEVUTA DI VERSAMENTO DI TEST",
            DemoDocumentPdf.Marker + " CasaZen. Nessun valore fuori dall'ambiente di test.");
        var fileName = "ricevuta-di-test.pdf";
        var key = StorageKeys.TouristTaxReceipt(orgId, remittanceId, fileName);
        await using (var stream = new MemoryStream(pdf))
            await storage.PutAsync(StorageBucket.Private, key, stream, "application/pdf", cancellationToken);

        var now = UtcNow();
        var remittance = new TouristTaxRemittance
        {
            Id = remittanceId,
            OrgId = orgId,
            Comune = city.Trim(),
            ComuneKey = comuneKey,
            PeriodFrom = today.AddDays(template.PeriodFromOffsetDays),
            PeriodTo = today.AddDays(template.PeriodToOffsetDays),
            PaidAmount = template.PaidAmount,
            PaidOn = today.AddDays(template.PaidOnOffsetDays),
            PaymentReference = template.Reference,
            Notes = template.Notes,
            ReceiptStorageKey = key,
            ReceiptFileName = fileName,
            ReceiptContentType = "application/pdf",
            ReceiptSizeBytes = pdf.Length,
            RecordedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TouristTaxRemittances.Add(remittance);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.TouristTaxRemittance, remittance.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// Deletes what the showcase wrote on the marketplace for the org, found by the markers (E09/F01): the activations it declared, then the test
    /// partners it created (their texts go with them). An activation or a partner a person made has no marker and is never touched.
    /// </summary>
    private async Task RemoveShowcaseMarketplaceAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var activationIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.MarketplaceActivation)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (activationIds.Count > 0)
        {
            await db.MarketplaceActivations.IgnoreQueryFilters([Tenant])
                .Where(a => a.OrgId == orgId && activationIds.Contains(a.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var partnerIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.MarketplacePartner)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (partnerIds.Count > 0)
            await db.MarketplacePartners.Where(p => partnerIds.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);

        await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && (m.EntityType == TestDataEntities.MarketplaceActivation
                    || m.EntityType == TestDataEntities.MarketplacePartner
                    || m.EntityType == TestDataEntities.MarketplacePartnerText))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// One test partner of the marketplace catalog with its Italian and English texts, marked as showcase data. Skipped, with a log, when the catalog
    /// already has a partner with that code (another org's showcase created it, or a person did): the code is unique, and a partner that is not
    /// the showcase's is never replaced.
    /// </summary>
    private async Task CreateMarketplacePartnerAsync(
        Guid orgId, string ownerId, DemoMarketplacePartnerTemplate template, CancellationToken cancellationToken)
    {
        if (await db.MarketplacePartners.AnyAsync(p => p.Code == template.Code, cancellationToken))
        {
            logger.LogInformation("Demo marketplace partner {Key} skipped: the catalog already has a partner with that code", template.Key);
            return;
        }

        var now = UtcNow();
        var partner = new MarketplacePartner
        {
            Code = template.Code,
            Name = template.Name,
            Job = template.Job,
            SortOrder = template.SortOrder,
            IsVisible = true,
            WebsiteUrl = template.WebsiteUrl,
            InvoicedBy = template.InvoicedBy,
            ServiceCategories = [.. template.ServiceCategories],
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedByUserId = ownerId,
        };
        partner.Texts.Add(MarketplaceText(partner, MarketplacePartnerText.Italian, template.Italian));
        partner.Texts.Add(MarketplaceText(partner, MarketplacePartnerText.English, template.English));
        db.MarketplacePartners.Add(partner);
        await db.SaveChangesAsync(cancellationToken);

        await MarkAsync(orgId, TestDataEntities.MarketplacePartner, partner.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
        foreach (var text in partner.Texts)
        {
            await MarkAsync(
                orgId, TestDataEntities.MarketplacePartnerText, text.Id, TestDataOrigins.Showcase, $"{template.Key}:{text.Language}", cancellationToken);
        }
    }

    private static MarketplacePartnerText MarketplaceText(MarketplacePartner partner, string language, DemoMarketplacePartnerText text) =>
        new()
        {
            PartnerId = partner.Id,
            Language = language,
            Benefit = text.Benefit,
            Audience = text.Audience,
            Description = text.Description,
            Requirements = text.Requirements,
            CostText = text.CostText,
            DataShared = text.DataShared,
            Deactivation = text.Deactivation,
            ActivationGuide = text.ActivationGuide,
            FaqJson = System.Text.Json.JsonSerializer.Serialize(text.Faq.Select(faq => new { q = faq.Question, a = faq.Answer })),
        };

    /// <summary>
    /// The activation of a test partner that the demo host declared: org-level, active, declared (Octorate proves nothing for a partner on its own
    /// site) a few days ago. Skipped, with a warning, when the partner is not in the catalog, and when the org already has an activation of it (a
    /// real one is never touched).
    /// </summary>
    private async Task CreateMarketplaceActivationAsync(
        Guid orgId, DemoMarketplaceActivationTemplate template, CancellationToken cancellationToken)
    {
        var code = DemoShowcaseCatalog.MarketplacePartners.FirstOrDefault(p => p.Key == template.PartnerKey)?.Code;
        if (code is null || !await db.MarketplacePartners.AnyAsync(p => p.Code == code, cancellationToken))
        {
            logger.LogWarning(
                "Demo marketplace activation {Key} of org {OrgId} was skipped: the partner it names is not in the catalog", template.Key, orgId);
            return;
        }

        var taken = await db.MarketplaceActivations.IgnoreQueryFilters([Tenant])
            .AnyAsync(
                a => a.OrgId == orgId && a.PropertyId == null && a.Kind == MarketplaceActivationKind.Partner && a.TargetCode == code,
                cancellationToken);
        if (taken)
        {
            logger.LogInformation("Demo marketplace activation {Key} skipped: the org already has an activation of that partner", template.Key);
            return;
        }

        var now = UtcNow();
        var declaredAt = now.AddDays(template.DeclaredOffsetDays);
        var activation = new MarketplaceActivation
        {
            OrgId = orgId,
            Kind = MarketplaceActivationKind.Partner,
            TargetCode = code,
            Status = MarketplaceActivationStatus.Active,
            IsDeclared = true,
            DeclaredAt = declaredAt,
            RequestedAt = declaredAt,
            CreatedAt = declaredAt,
            UpdatedAt = declaredAt,
        };
        db.MarketplaceActivations.Add(activation);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.MarketplaceActivation, activation.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// NF-46: the rates the demo host declared (tenant data) and the channel rules the showcase created (platform data, only the invented comune
    /// of the demo), found by their markers; a real rate or rule has no marker and is never touched.
    /// </summary>
    private async Task RemoveShowcaseTouristTaxRulesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var rateIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.OrgTouristTaxRate)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (rateIds.Count > 0)
        {
            await db.OrgTouristTaxRates.IgnoreQueryFilters([Tenant])
                .Where(r => r.OrgId == orgId && rateIds.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var ruleIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.TouristTaxChannelRule)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (ruleIds.Count > 0)
            await db.TouristTaxChannelRules.Where(r => ruleIds.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);

        await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && (m.EntityType == TestDataEntities.OrgTouristTaxRate || m.EntityType == TestDataEntities.TouristTaxChannelRule))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The active welcome link of a demo house (E07/F04/US01/T03, NF-90): the QR code of the house, with the fixed test token of the template. The
    /// house is found by the template key of its marker; the link follows it (cascade), so the reset needs no step of its own to remove it. Skipped,
    /// with a warning, when the house was not created, when the house already has an active link (a person made it) and when another org holds the
    /// token: the token is unique among all the links, and a link of somebody else is never taken over.
    /// </summary>
    private async Task CreateWelcomeLinkAsync(
        Guid orgId, string ownerId, DemoWelcomeLinkTemplate template, CancellationToken cancellationToken)
    {
        var propertyId = await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && m.EntityType == TestDataEntities.Property
                && m.TemplateKey == template.PropertyKey)
            .Select(m => m.EntityId)
            .FirstOrDefaultAsync(cancellationToken);
        if (propertyId == Guid.Empty)
        {
            logger.LogWarning(
                "Demo welcome link {Key} of org {OrgId} was skipped: the house it names was not created", template.Key, orgId);
            return;
        }

        var taken = await db.PropertyWelcomeLinks.IgnoreQueryFilters([Tenant])
            .AnyAsync(l => l.Token == template.Token || (l.PropertyId == propertyId && l.RevokedAt == null), cancellationToken);
        if (taken)
        {
            logger.LogWarning(
                "Demo welcome link {Key} of org {OrgId} was skipped: the house already has an active link, or another org holds the token",
                template.Key,
                orgId);
            return;
        }

        var link = new PropertyWelcomeLink
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Token = template.Token,
            CreatedAt = UtcNow(),
            CreatedByUserId = ownerId,
        };
        db.PropertyWelcomeLinks.Add(link);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.WelcomeLink, link.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// Deletes the conversations of the channels the showcase wrote for the org, found by their markers (E05/F02/US01/T01): a conversation points at
    /// its house and its stay with a foreign key that lets go (SET NULL), so left to the removal of the houses it would stay behind as an unassigned
    /// conversation; its messages go with it (cascade). The conversations with the guests of the direct stays go with their bookings (cascade), and
    /// their markers with the other showcase markers. Runs before the showcase houses go: it finds its rows by the markers they drop.
    /// </summary>
    internal async Task RemoveShowcaseMessageThreadsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var threadIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.OtaMessageThread)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (threadIds.Count == 0)
            return;

        await db.OtaMessageThreads.IgnoreQueryFilters([Tenant])
            .Where(t => t.OrgId == orgId && threadIds.Contains(t.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.OtaMessageThread)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// One conversation of the hub of messages of the showcase (E05/F02/US01/T01): the conversation with the guest of a direct stay, or a conversation
    /// of a channel, written as the real flows leave them (the counters of the thread agree with its messages: the last message, the unread ones, the
    /// time the host read up to). Invented words that say TEST, no channel manager behind them. Skipped, with a log, when the house or the stay it names
    /// was not created.
    /// </summary>
    internal async Task CreateMessageThreadAsync(
        Guid orgId, string ownerId, DemoMessageThreadTemplate template, CancellationToken cancellationToken)
    {
        var propertyId = await ShowcaseIdAsync(orgId, TestDataEntities.Property, template.PropertyKey, cancellationToken);
        var bookingId = template.BookingKey is null
            ? null
            : await ShowcaseIdAsync(orgId, TestDataEntities.Booking, template.BookingKey, cancellationToken);
        if (propertyId is null || (template.BookingKey is not null && bookingId is null) || template.Messages.Count == 0)
        {
            logger.LogWarning(
                "Demo conversation {Key} of org {OrgId} was skipped: the house or the stay it names was not created", template.Key, orgId);
            return;
        }

        var now = UtcNow();
        var messages = template.Messages
            .OrderByDescending(m => m.MinutesAgo)
            .Select(m => (Message: m, At: now.AddMinutes(-m.MinutesAgo)))
            .ToList();
        var last = messages[^1];

        if (template.Kind == DemoMessageThreadKind.Direct)
        {
            var thread = new GuestThread
            {
                OrgId = orgId,
                PropertyId = propertyId.Value,
                BookingId = bookingId!.Value,
                CreatedAt = messages[0].At,
                LastMessageAt = last.At,
                LastGuestMessageAt = messages.Where(m => m.Message.FromGuest).Select(m => (DateTime?)m.At).Max(),
                LastHostMessageAt = messages.Where(m => !m.Message.FromGuest).Select(m => (DateTime?)m.At).Max(),
                HostReadAt = messages.Where(m => m.Message.FromGuest && m.Message.Read).Select(m => (DateTime?)m.At).Max(),
            };
            db.GuestThreads.Add(thread);
            foreach (var (message, at) in messages)
            {
                var sender = message.FromGuest ? GuestMessageSender.Guest : GuestMessageSender.Host;
                db.GuestMessages.Add(new GuestMessage
                {
                    OrgId = orgId,
                    ThreadId = thread.Id,
                    Sender = sender,
                    Origin = GuestMessageRules.OriginOf(sender),
                    Text = message.Text,
                    CreatedAt = at,
                    AuthorUserId = message.FromGuest ? null : ownerId,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            await MarkAsync(orgId, TestDataEntities.GuestThread, thread.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
            return;
        }

        var otaThread = new OtaMessageThread
        {
            OrgId = orgId,
            PropertyId = propertyId,
            BookingId = bookingId,
            OctorateThreadId = $"TEST-{template.Key}",
            OctorateAccommodationId = "TEST-accommodation",
            ChannelSource = template.Channel ?? OtaChannel.Airbnb,
            Kind = bookingId is null ? OtaThreadKind.Request : OtaThreadKind.Reservation,
            GuestDisplayName = template.GuestName,
            LastMessageAt = last.At,
            LastMessageDirection = last.Message.FromGuest ? OtaMessageDirection.Inbound : OtaMessageDirection.Outbound,
            UnreadCount = messages.Count(m => m.Message.FromGuest && !m.Message.Read),
            DefaultProcessor = "CHANNEL",
            AiAssistantOn = false,
            CreatedAt = messages[0].At,
            UpdatedAt = now,
        };
        db.OtaMessageThreads.Add(otaThread);
        for (var index = 0; index < messages.Count; index++)
        {
            var (message, at) = messages[index];
            db.OtaMessages.Add(new OtaMessage
            {
                OrgId = orgId,
                ThreadId = otaThread.Id,
                OctorateMessageId = $"TEST-{template.Key}-{index + 1}",
                Direction = message.FromGuest ? OtaMessageDirection.Inbound : OtaMessageDirection.Outbound,
                SenderName = message.FromGuest ? template.GuestName ?? string.Empty : "Host (TEST)",
                Text = message.Text,
                SentAt = at,
                ReadAt = message.FromGuest && message.Read ? at.AddMinutes(1) : null,
                DeliveryState = OtaMessageDeliveryState.Sent,
                Origin = message.FromGuest ? MessageOrigin.Guest : MessageOrigin.Person,
                CreatedAt = at,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.OtaMessageThread, otaThread.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// Deletes the message templates the showcase wrote for the org, found by their markers (E05/F02/US02/T02), and the markers: a template points at
    /// no house and no stay, so the removal of the houses would leave it behind. A template the host wrote has no marker and is never touched.
    /// Runs before the showcase houses go: that removal drops every showcase marker of the org.
    /// </summary>
    internal async Task RemoveShowcaseMessageTemplatesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var markers = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.MessageTemplate)
            .ToListAsync(cancellationToken);
        if (markers.Count == 0)
            return;

        var templateIds = markers.Select(m => m.EntityId).ToList();
        var templates = await db.MessageTemplates.IgnoreQueryFilters([Tenant])
            .Where(t => t.OrgId == orgId && templateIds.Contains(t.Id))
            .ToListAsync(cancellationToken);
        db.MessageTemplates.RemoveRange(templates);
        db.TestDataMarkers.RemoveRange(markers);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One message template of the demo host (E05/F02/US02/T02): invented words that say TEST, with the variables a real template may use. A host
    /// may already have a template with the same name and language (they are unique for each org): then the showcase one is skipped, with a line in
    /// the log, and the host's is never touched or marked.
    /// </summary>
    internal async Task CreateHostMessageTemplateAsync(Guid orgId, DemoHostMessageTemplate template, CancellationToken cancellationToken)
    {
        if (await db.MessageTemplates.IgnoreQueryFilters([Tenant])
                .AnyAsync(t => t.OrgId == orgId && t.Name == template.Name && t.Language == template.Language, cancellationToken))
        {
            logger.LogInformation("Demo message template {Key} skipped: org {OrgId} already has one with that name and language", template.Key, orgId);
            return;
        }

        var now = UtcNow();
        var row = new MessageTemplate
        {
            OrgId = orgId,
            Name = template.Name,
            Language = template.Language,
            Body = template.Body,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MessageTemplates.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.MessageTemplate, row.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>The id of the showcase row of <paramref name="entityType"/> that came from the template <paramref name="templateKey"/>, or <c>null</c> when there is none.</summary>
    private async Task<Guid?> ShowcaseIdAsync(Guid orgId, string entityType, string templateKey, CancellationToken cancellationToken)
    {
        var id = await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && m.EntityType == entityType
                && m.TemplateKey == templateKey)
            .Select(m => m.EntityId)
            .FirstOrDefaultAsync(cancellationToken);
        return id == Guid.Empty ? null : id;
    }

    /// <summary>
    /// Deletes the CRM profiles the showcase wrote for the org, found by their markers (E07/F01/US02/T03): the contacts and the proposed merges
    /// follow their profile (cascade). The guests of the showcase are deleted afterwards by <see cref="RemoveShowcaseAsync"/>; a guest that is not
    /// one of them (a real one the sync linked to a showcase profile) is let go of the profile first. A profile of a real guest has no marker and
    /// is never touched. Runs before the showcase guests and properties go: it finds its rows by the markers they drop.
    /// </summary>
    private async Task RemoveShowcaseCrmAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var profileIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.CrmProfile)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        if (profileIds.Count > 0)
        {
            await db.Guests.IgnoreQueryFilters([Tenant])
                .Where(g => g.OrgId == orgId && g.GuestCrmProfileId != null && profileIds.Contains(g.GuestCrmProfileId.Value))
                .ExecuteUpdateAsync(set => set.SetProperty(g => g.GuestCrmProfileId, (Guid?)null), cancellationToken);
            await db.GuestCrmProfiles.IgnoreQueryFilters([Tenant])
                .Where(p => p.OrgId == orgId && profileIds.Contains(p.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && (m.EntityType == TestDataEntities.CrmProfile
                    || m.EntityType == TestDataEntities.CrmContact
                    || m.EntityType == TestDataEntities.CrmMergeCandidate
                    || m.EntityType == TestDataEntities.GuestConsent
                    || m.EntityType == TestDataEntities.MarketingConsent))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the rows of the activation with Octorate the showcase wrote for the org (E10/F01/US01/T04), found by their markers, the dependent
    /// ones first: the "To do" of the deferred part, the rows of the onboarding tables, the test mappings, then the test connection they hang on.
    /// A real row has no marker and is never touched. Runs before the showcase properties go: it finds its rows by the markers they drop.
    /// </summary>
    /// <remarks>
    /// A marker says what the showcase wrote, not what the row has become. A real consent reuses the one connection of the org and the
    /// environment (its tokens are written into the marked row), and a deferral or an exit of the host writes into the one row of the part. So the
    /// test connection, the mappings on it and the deferred part go only while they are still the invented ones (<see cref="InventedConnectionsAsync"/>,
    /// <see cref="InventedPartStatesAsync"/>); a row the host made its own only loses its marker and stays, with its mappings and its "To do". A
    /// mapping that is not marked is deleted only with an invented connection (the foreign key is restrictive and no real mapping can be on it).
    /// </remarks>
    internal async Task RemoveShowcaseOnboardingAsync(Guid orgId, CancellationToken cancellationToken)
    {
        string[] types =
        [
            TestDataEntities.Todo,
            TestDataEntities.OnboardingPartState,
            TestDataEntities.PropertyFlowActivation,
            TestDataEntities.OnboardingPanelStep,
            TestDataEntities.OnboardingAssistRequest,
            TestDataEntities.OnboardingPilotMember,
            TestDataEntities.PropertyOnboardingData,
            TestDataEntities.OctorateListingMapping,
            TestDataEntities.OctorateConnection,
        ];
        var marked = (await Markers()
                .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && types.Contains(m.EntityType))
                .Select(m => new { m.EntityType, m.EntityId })
                .ToListAsync(cancellationToken))
            .ToLookup(m => m.EntityType, m => m.EntityId);
        if (marked.Count == 0)
            return;

        List<Guid> Ids(string type) => [.. marked[type]];
        var todos = Ids(TestDataEntities.Todo);
        var partStates = Ids(TestDataEntities.OnboardingPartState);
        var activations = Ids(TestDataEntities.PropertyFlowActivation);
        var panelSteps = Ids(TestDataEntities.OnboardingPanelStep);
        var assists = Ids(TestDataEntities.OnboardingAssistRequest);
        var pilots = Ids(TestDataEntities.OnboardingPilotMember);
        var propertyData = Ids(TestDataEntities.PropertyOnboardingData);
        var mappings = Ids(TestDataEntities.OctorateListingMapping);
        var connections = Ids(TestDataEntities.OctorateConnection);

        // What is still the invented one (the others only lose their marker below).
        var inventedConnections = await InventedConnectionsAsync(orgId, connections, cancellationToken);
        var inventedPartStates = await InventedPartStatesAsync(orgId, partStates, todos, cancellationToken);
        var inventedAccommodations = DemoShowcaseCatalog.OnboardingActivations
            .SelectMany(activation => activation.Properties)
            .Select(house => house.AccommodationId)
            .OfType<string>()
            .ToList();

        await db.InternalActivities.IgnoreQueryFilters([Tenant])
            .Where(a => a.OrgId == orgId && todos.Contains(a.Id))
            .ExecuteDeleteAsync(cancellationToken);

        // The "To do" the sync raised about the test connection (a job of the connector marked it lost): about a row that goes now.
        await db.InternalActivities.IgnoreQueryFilters([Tenant])
            .Where(a => a.OrgId == orgId
                && a.EntityType == OnboardingActivationCodes.ConnectionTodoEntityType
                && a.EntityId != null
                && inventedConnections.Contains(a.EntityId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        await db.OnboardingPartStates.IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == orgId && inventedPartStates.Contains(s.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PropertyFlowActivations.IgnoreQueryFilters([Tenant])
            .Where(a => a.OrgId == orgId && activations.Contains(a.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await db.OnboardingPanelSteps.IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == orgId && panelSteps.Contains(s.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await db.OnboardingAssistRequests.IgnoreQueryFilters([Tenant])
            .Where(r => r.OrgId == orgId && assists.Contains(r.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await db.OnboardingPilotMembers.IgnoreQueryFilters([Tenant])
            .Where(p => p.OrgId == orgId && pilots.Contains(p.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PropertyOnboardingData.IgnoreQueryFilters([Tenant])
            .Where(d => d.OrgId == orgId && propertyData.Contains(d.Id))
            .ExecuteDeleteAsync(cancellationToken);
        // Whatever hangs on an invented connection goes with it (the foreign keys are restrictive): its mappings, marked or not (no real one can
        // be on it), and the cursor a job of the OTA conversations may have written before it found no token. The marked mappings of a connection
        // the host took over go only while they still name an invented accommodation: the real ones stay.
        await db.OctorateListingMappings.IgnoreQueryFilters([Tenant])
            .Where(m => m.OrgId == orgId
                && (inventedConnections.Contains(m.ConnectionId)
                    || (mappings.Contains(m.Id) && inventedAccommodations.Contains(m.OctorateAccommodationId))))
            .ExecuteDeleteAsync(cancellationToken);
        await db.OtaMessageSyncStates.IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == orgId && inventedConnections.Contains(s.ConnectionId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.OctorateConnections.IgnoreQueryFilters([Tenant])
            .Where(c => c.OrgId == orgId && inventedConnections.Contains(c.Id))
            .ExecuteDeleteAsync(cancellationToken);

        // Every marker goes, those of the rows the host made its own too: from now on they are real rows.
        await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && types.Contains(m.EntityType))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The marked connections that are still the invented ones: the account of the template and no token at all. A consent overwrites the account
    /// of the connection it completes and stores its tokens, so a connection with a token (even one left by a consent that failed halfway) or
    /// with another account is the host's.
    /// </summary>
    private async Task<List<Guid>> InventedConnectionsAsync(Guid orgId, List<Guid> connections, CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
            return [];

        var accounts = DemoShowcaseCatalog.OnboardingActivations.Select(activation => activation.AccountId).ToList();
        return await db.OctorateConnections.IgnoreQueryFilters([Tenant])
            .Where(c => c.OrgId == orgId
                && connections.Contains(c.Id)
                && c.OctorateAccountId != null
                && accounts.Contains(c.OctorateAccountId)
                && c.EncryptedRefreshToken == null
                && c.EncryptedAccessToken == null)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The marked part states that are still the invented ones: the part is tied to the "To do" the showcase raised for it, or it holds no intent at
    /// all (resumed: the same as no row). A deferral or an exit of the host replaces the "To do" of the row, so the row is then the host's and its
    /// new "To do" (which has no marker) must not be left pointing at a row that is gone.
    /// </summary>
    private async Task<List<Guid>> InventedPartStatesAsync(Guid orgId, List<Guid> partStates, List<Guid> todos, CancellationToken cancellationToken)
    {
        if (partStates.Count == 0)
            return [];

        var rows = await db.OnboardingPartStates.IgnoreQueryFilters([Tenant])
            .Where(s => s.OrgId == orgId && partStates.Contains(s.Id))
            .Select(s => new { s.Id, s.ActivityId, s.Status, s.DeferredAt, s.RemindAt })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows
                .Where(s => s.ActivityId is { } activityId
                    ? todos.Contains(activityId)
                    : s.Status == OnboardingPartStatus.Open && s.DeferredAt is null && s.RemindAt is null)
                .Select(s => s.Id),
        ];
    }

    /// <summary>
    /// The activation with Octorate of the demo org (E10/F01/US01/T04, NF-104, NF-106): a connected test connection of this environment with no
    /// token (nothing can be called with it; a job of the connector that tries finds no token and marks it lost, which the next reset undoes unless
    /// a real consent took the row over: <see cref="RemoveShowcaseOnboardingAsync"/> then keeps it, with its tokens and mappings), the
    /// confirmed listings of the houses that have one on it (no product: the booking and calendar jobs never read them), the flows activated on
    /// them, and the deferred part of the plan with its "To do" (codes and ids only, as the plan raises it). The connection and the mappings are
    /// skipped when the org has a connection of its own in the environment (a real one is never touched nor mixed with invented listings); the
    /// deferred part is skipped when the org has its own state for that part. Every row is marked as showcase data.
    /// </summary>
    internal async Task CreateOnboardingActivationAsync(
        Guid orgId, string ownerId, DemoOnboardingActivationTemplate template, CancellationToken cancellationToken)
    {
        var keys = template.Properties.Select(p => p.PropertyKey).ToList();
        var houses = await Markers()
            .Where(m => m.OrgId == orgId
                && m.Origin == TestDataOrigins.Showcase
                && m.EntityType == TestDataEntities.Property
                && m.TemplateKey != null
                && keys.Contains(m.TemplateKey))
            .ToDictionaryAsync(m => m.TemplateKey!, m => m.EntityId, cancellationToken);
        var now = UtcNow();
        var rows = new List<(string EntityType, Guid Id, string Key)>();

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var hasOwnConnection = await db.OctorateConnections.IgnoreQueryFilters([Tenant])
            .AnyAsync(c => c.OrgId == orgId && c.Environment == environment, cancellationToken);
        if (hasOwnConnection)
        {
            logger.LogInformation(
                "Demo Octorate connection of org {OrgId} was skipped: the org has a connection of its own in this environment", orgId);
        }
        else
        {
            var connection = new OctorateConnection
            {
                OrgId = orgId,
                Environment = environment,
                OctorateAccountId = template.AccountId,
                Status = OctorateConnectionStatus.Connected,
                LastSyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OctorateConnections.Add(connection);
            rows.Add((TestDataEntities.OctorateConnection, connection.Id, template.Key));

            foreach (var house in template.Properties.Where(p => p.AccommodationId is not null))
            {
                if (!houses.TryGetValue(house.PropertyKey, out var propertyId))
                    continue;

                var mapping = new OctorateListingMapping
                {
                    OrgId = orgId,
                    ConnectionId = connection.Id,
                    OctorateAccommodationId = house.AccommodationId!,
                    SnapshotName = DemoShowcaseCatalog.Properties.FirstOrDefault(p => p.Key == house.PropertyKey)?.Name,
                    PropertyId = propertyId,
                    Status = OctorateMappingStatus.Confirmed,
                    MatchScore = 100,
                    ConfirmedByUserId = ownerId,
                    ConfirmedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.OctorateListingMappings.Add(mapping);
                rows.Add((TestDataEntities.OctorateListingMapping, mapping.Id, $"{template.Key}/{house.PropertyKey}"));
            }
        }

        foreach (var house in template.Properties)
        {
            if (!houses.TryGetValue(house.PropertyKey, out var propertyId))
                continue;

            foreach (var flow in house.ActivatedFlows.Distinct())
            {
                var activation = new PropertyFlowActivation
                {
                    OrgId = orgId,
                    PropertyId = propertyId,
                    Flow = flow,
                    ActivatedAt = now,
                    ActivatedByUserId = ownerId,
                };
                db.PropertyFlowActivations.Add(activation);
                rows.Add((TestDataEntities.PropertyFlowActivation, activation.Id, $"{template.Key}/{house.PropertyKey}/{flow}"));
            }
        }

        var partTaken = await db.OnboardingPartStates.IgnoreQueryFilters([Tenant])
            .AnyAsync(s => s.OrgId == orgId && s.PartKey == template.DeferredPart, cancellationToken);
        if (partTaken)
        {
            logger.LogInformation("Demo deferred part {Part} of org {OrgId} was skipped: the org has its own state for it", template.DeferredPart, orgId);
        }
        else
        {
            var remindAt = now.AddDays(template.RemindInDays);
            var part = new OnboardingPartState
            {
                OrgId = orgId,
                PartKey = template.DeferredPart,
                Status = OnboardingPartStatus.Deferred,
                DeferredAt = now,
                RemindAt = remindAt,
                UpdatedAt = now,
                UpdatedByUserId = ownerId,
            };
            var todo = new InternalActivity
            {
                OrgId = orgId,
                ActivityType = nameof(ActivityType.WizardResume),
                Action = ActivityAction.ResumeWizard,
                EntityType = OnboardingPlanCodes.TodoEntityType,
                EntityId = part.Id,
                Urgency = ActivityUrgency.Medium,
                Deadline = remindAt,
                Reason = OnboardingPlanCodes.ReasonDeferred,
                Status = InternalActivity.StatusPending,
                ScheduledAt = remindAt,
                CreatedAt = now,
            };
            part.ActivityId = todo.Id;
            db.OnboardingPartStates.Add(part);
            db.InternalActivities.Add(todo);
            rows.Add((TestDataEntities.OnboardingPartState, part.Id, $"{template.Key}/{template.DeferredPart}"));
            rows.Add((TestDataEntities.Todo, todo.Id, $"{template.Key}/{template.DeferredPart}"));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (entityType, id, key) in rows)
            await MarkAsync(orgId, entityType, id, TestDataOrigins.Showcase, key, cancellationToken);
    }

    /// <summary>
    /// NF-46, ADR-037-R3: the channel rule of the invented comune of a demo house, platform data with an example source; written only when no rule
    /// exists for that channel and comune (another org's demo may have written it), so a real rule is never replaced.
    /// </summary>
    private async Task CreateTouristTaxChannelRuleAsync(
        Guid orgId, DemoTouristTaxChannelRuleTemplate template, CancellationToken cancellationToken)
    {
        var istatCode = DemoShowcaseCatalog.Properties.FirstOrDefault(p => p.Key == template.PropertyKey)?.Place.IstatCode;
        if (istatCode is null
            || await db.TouristTaxChannelRules.AnyAsync(r => r.Channel == template.Channel && r.IstatCode == istatCode, cancellationToken))
        {
            return;
        }

        var now = UtcNow();
        var rule = new TouristTaxChannelRule
        {
            Channel = template.Channel,
            IstatCode = istatCode,
            Collector = template.Collector,
            SourceUrl = template.SourceUrl,
            VerifiedAt = now,
            IsActive = true,
            Notes = template.Notes,
            UpdatedAt = now,
        };
        db.TouristTaxChannelRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.TouristTaxChannelRule, rule.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    /// <summary>
    /// Builds the CRM of the showcase guests (NF-88, NF-89): runs the CRM sync, as the job and the event handler do, on every showcase booking
    /// just created, each in a scope under the demo org; then marks the profiles, the contacts and the proposed merges that came out of it. The
    /// profile of a returning guest, the OTA forward address, the soft opt-in customer, the confirmed consent and the two guests who share a phone
    /// (a merge to decide) are all made by the real rules, not written by hand. Skipped, with a log, while the CRM sync is closed (the flag
    /// <c>Features:CrmMarketing</c> is off or the CRM index has no key: <see cref="CrmProfileSyncGate"/>); a booking that fails is logged and
    /// the reset goes on (the job would make its profile later).
    /// </summary>
    private async Task SeedCrmProfilesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        if (!CrmProfileSyncGate.IsOpen(featureFlags, contactIndex))
        {
            logger.LogInformation(
                "Demo CRM profiles of org {OrgId} were skipped: Features:CrmMarketing is off or Crm:ContactIndexKey is not configured", orgId);
            return;
        }

        var before = await db.GuestCrmProfiles.IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var bookingIds = await Markers()
            .Where(m => m.OrgId == orgId && m.Origin == TestDataOrigins.Showcase && m.EntityType == TestDataEntities.Booking)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);

        foreach (var bookingId in bookingIds)
        {
            try
            {
                await CrmSyncScopes.SyncBookingAsync(scopeFactory, orgId, bookingId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Demo CRM sync of booking {BookingId} of org {OrgId} failed: {Error}", bookingId, orgId, CrmDatabaseErrors.Describe(ex));
            }
        }

        var made = (await db.GuestCrmProfiles.IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .Where(p => p.OrgId == orgId)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken))
            .Except(before)
            .ToList();
        if (made.Count == 0)
            return;

        var contactIds = await db.GuestContacts.IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && c.GuestCrmProfileId != null && made.Contains(c.GuestCrmProfileId.Value))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var proposalIds = await db.GuestMergeCandidates.IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && (made.Contains(c.ProfileAId) || made.Contains(c.ProfileBId)))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        const string key = DemoShowcaseCatalog.Scenario.CrmProfiles;
        await MarkManyAsync(orgId, TestDataEntities.CrmProfile, made, key, cancellationToken);
        await MarkManyAsync(orgId, TestDataEntities.CrmContact, contactIds, key, cancellationToken);
        await MarkManyAsync(orgId, TestDataEntities.CrmMergeCandidate, proposalIds, key, cancellationToken);
    }

    /// <summary>Marks the rows that have no marker yet as showcase data, in one save.</summary>
    private async Task MarkManyAsync(
        Guid orgId, string entityType, IReadOnlyCollection<Guid> entityIds, string templateKey, CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
            return;

        var marked = await Markers()
            .Where(m => m.EntityType == entityType && entityIds.Contains(m.EntityId))
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        var now = UtcNow();
        foreach (var entityId in entityIds.Except(marked))
        {
            db.TestDataMarkers.Add(new TestDataMarker
            {
                OrgId = orgId,
                EntityType = entityType,
                EntityId = entityId,
                Origin = TestDataOrigins.Showcase,
                TemplateKey = templateKey,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> RemoveUnpublishedAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var bookings = db.Bookings.IgnoreQueryFilters([Tenant]);
        var pending = await Properties(orgId)
            .Where(p => p.RentalMode == RentalMode.Short && p.ComplianceStatus == PropertyComplianceStatus.Pending)
            .Where(p => !bookings.Any(b => b.PropertyId == p.Id))
            .ToListAsync(cancellationToken);
        var now = UtcNow();
        foreach (var property in pending)
        {
            property.IsDeleted = true;
            property.DeletedAt = now;
            property.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }

    /// <summary>One house of the showcase with what its template says: its details, its listing rows, its documents and its stays.</summary>
    /// <remarks>Internal so that a test can seed one house of the catalog without the rest of the showcase (<c>DemoDocumentArchiveSeedTests</c>).</remarks>
    internal async Task CreateShowcasePropertyAsync(
        Guid orgId, string ownerId, DemoPropertyTemplate template, CancellationToken cancellationToken)
    {
        var place = await ResolvePlaceAsync(template.Place, cancellationToken);
        var now = UtcNow();
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = ownerId,
            Name = template.Name,
            Slug = await UniqueSlugAsync(orgId, "demo-" + template.Key, cancellationToken),
            Description = template.Description,
            Address = place.Address,
            City = place.City,
            PostalCode = place.PostalCode,
            ComuneIstatCode = place.IstatCode,
            RegionCode = place.RegionCode,
            RentalMode = template.Mode,
            Bedrooms = template.Bedrooms,
            Bathrooms = template.Bathrooms,
            MaxGuests = template.MaxGuests,
            NightlyRate = template.NightlyRate,
            CleaningFee = template.CleaningFee,
            HouseRules = "Dimostrazione: non fumare, silenzio dopo le 22.",
            IsActive = true,
            IsTestData = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (template.Cadastral is { } cadastral)
        {
            property.CadastralSheet = cadastral.Sheet;
            property.CadastralParcel = cadastral.Parcel;
            property.CadastralSubaltern = cadastral.Subaltern;
            property.CadastralCategory = cadastral.Category;
            property.CadastralIncome = cadastral.Income;
        }

        // SR-07a: the type, beds, area and house rules of the wizard «Aggiungi immobile», so the host's list is not empty.
        if (template.Profile is { } profile)
        {
            property.AccommodationType = profile.Type;
            property.Beds = profile.Beds;
            property.AreaSqm = profile.AreaSqm;
            property.ListingTitle = profile.ListingTitle;
            property.CheckInFrom = profile.CheckInFrom;
            property.CheckOutBy = profile.CheckOutBy;
            property.SmokingPolicy = profile.Smoking;
            property.PetsPolicy = profile.Pets;
            property.PartiesPolicy = profile.Parties;
            property.ChildrenPolicy = profile.Children;
            property.QuietHoursFrom = profile.QuietHoursFrom;
            property.QuietHoursTo = profile.QuietHoursTo;
            property.ExtraGuestFeePerNight = profile.ExtraGuestFeePerNight;
            PropertyHouseRules.SyncAmenities(property);
        }

        // DB-11a: what the host says about the house on its public page, through the rules of the host form (a demo can never hold
        // what a host could not).
        if (template.SiteProfile is { } siteProfile)
            SitePropertyProfileRules.Apply(property, SitePropertyProfileRules.Normalize(siteProfile, property.CheckInFrom, now.Year));

        db.Properties.Add(property);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.Property, property.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);

        // DB-11a: the points of interest around the house, with the distance rounded as for a host. They go with their property (cascade) when the
        // showcase is reset, so they need no marker of their own.
        for (var position = 0; position < (template.Pois?.Count ?? 0); position++)
        {
            var poi = template.Pois![position];
            db.PropertyPois.Add(new PropertyPoi
            {
                PropertyId = property.Id,
                OrgId = orgId,
                Position = position,
                Kind = poi.Kind,
                NameIt = poi.NameIt,
                NameEn = poi.NameEn,
                Mode = poi.Mode,
                Minutes = poi.Minutes,
                Direction = poi.Direction,
                DistanceMeters = PoiDistance.Round(poi.DistanceMeters),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // NF-27, NF-38, NF-34, NF-35: where the content of the house stands on the channels of the channel manager (invented: nothing is sent
        // anywhere), its rooms with their beds and its length-of-stay discounts. Like the points of interest they go with their property (cascade)
        // at the next reset, and each carries a marker of its own as the demo rule asks.
        var listingRows = new List<(string EntityType, Guid Id, string Key)>();
        foreach (var channel in template.ChannelStates ?? [])
        {
            var state = new ListingChannelState
            {
                OrgId = orgId,
                PropertyId = property.Id,
                ChannelCode = channel.ChannelCode,
                Status = channel.Status,
                ErrorCode = channel.ErrorCode,
                ErrorMessage = channel.ErrorMessage,
                LastSyncedAt = channel.LastSyncedHoursAgo is { } hoursAgo ? now.AddHours(-hoursAgo) : null,
                ContentVersion = channel.Status == ChannelPublishStatus.Published ? 1 : 0,
            };
            db.ListingChannelStates.Add(state);
            listingRows.Add((TestDataEntities.ListingChannelState, state.Id, template.Key + "/" + channel.ChannelCode));
        }

        for (var position = 0; position < (template.Rooms?.Count ?? 0); position++)
        {
            var demoRoom = template.Rooms![position];
            var room = new PropertyRoom
            {
                OrgId = orgId,
                PropertyId = property.Id,
                Position = position,
                Type = demoRoom.Type,
                Beds = demoRoom.Beds,
            };
            db.PropertyRooms.Add(room);
            listingRows.Add((TestDataEntities.PropertyRoom, room.Id, template.Key + "/room-" + (position + 1)));
        }

        foreach (var demoDiscount in template.StayDiscounts ?? [])
        {
            var tier = new PropertyStayDiscount
            {
                OrgId = orgId,
                PropertyId = property.Id,
                MinNights = demoDiscount.MinNights,
                PercentOff = demoDiscount.PercentOff,
            };
            db.PropertyStayDiscounts.Add(tier);
            listingRows.Add((TestDataEntities.PropertyStayDiscount, tier.Id, template.Key + "/discount-" + demoDiscount.MinNights));
        }

        // NF-36: the price and the rules of single nights of the house (invented). Like the rows above they go with their property (cascade) at the
        // next reset and carry a marker of their own; the nights are counted from the day of the reset, on the calendar of the property.
        var firstNight = DayRuleRules.TodayOf(property.Timezone, clock.GetUtcNow());
        foreach (var demoRule in template.DayRules ?? [])
        {
            var rule = new PropertyDayRule
            {
                OrgId = orgId,
                PropertyId = property.Id,
                StayDate = firstNight.AddDays(demoRule.OffsetDays),
                Price = demoRule.Price,
                MinNights = demoRule.MinNights,
                MaxNights = demoRule.MaxNights,
                ClosedToArrival = demoRule.ClosedToArrival,
                ClosedToDeparture = demoRule.ClosedToDeparture,
                MinAdvanceDays = demoRule.MinAdvanceDays,
                MaxAdvanceDays = demoRule.MaxAdvanceDays,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedByUserId = ownerId,
            };
            db.PropertyDayRules.Add(rule);
            listingRows.Add((TestDataEntities.PropertyDayRule, rule.Id, template.Key + "/day-rule-" + demoRule.OffsetDays));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (entityType, id, key) in listingRows)
            await MarkAsync(orgId, entityType, id, TestDataOrigins.Showcase, key, cancellationToken);

        // NF-46: the rate the demo host declared for a comune with no official one (an invented amount and source note).
        if (template.DeclaredTouristTaxRate is { } declared)
        {
            var rate = new OrgTouristTaxRate
            {
                OrgId = orgId,
                IstatCode = property.ComuneIstatCode,
                City = property.City,
                RatePerPersonPerNight = declared.RatePerPersonPerNight,
                MaxNights = declared.MaxNights,
                MinimumAge = declared.MinimumAge,
                EffectiveFrom = clock.TodayInRomeAsDateOnly().AddYears(-1),
                SourceNote = declared.SourceNote,
                EnteredByUserId = ownerId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OrgTouristTaxRates.Add(rate);
            await db.SaveChangesAsync(cancellationToken);
            await MarkAsync(orgId, TestDataEntities.OrgTouristTaxRate, rate.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
        }

        // NF-52: who sends the police and ISTAT communications of the house (the stays below are read by the traffic light with it).
        if (template.ComplianceSender is { } complianceSender)
            await CreateComplianceSenderAsync(orgId, ownerId, property, complianceSender, template.Key, cancellationToken);

        if (template.Publish || template.Mode == RentalMode.Long)
        {
            var used = await UsedCinsAsync(orgId, cancellationToken);
            await FillComplianceAsync(property, used, TestDataOrigins.Showcase, template.Key, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (template.Publish)
            await compliance.ActivateAsync(property.Id, cancellationToken);

        var today = DateOnly.FromDateTime(UtcNow());
        foreach (var booking in template.Bookings)
            await CreateBookingAsync(orgId, property, booking, today, cancellationToken);
    }

    /// <summary>One stay of a showcase house: its booker, the booking, the registered guests and what the compliance traffic light finds on it.</summary>
    /// <remarks>Internal so that a test can seed one stay of the catalog without the rest of the house (<c>DemoComplianceTrafficLightSeedTests</c>).</remarks>
    internal async Task CreateBookingAsync(
        Guid orgId, Property property, DemoBookingTemplate template, DateOnly today, CancellationToken cancellationToken)
    {
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = template.FirstName,
            LastName = template.LastName,
            Email = template.Email,
            // The placeholder every demo guest has unless the template gives a number: the CRM does not take it for a phone, so no two demo guests
            // look as if they shared one (only the guests of the CRM scenario that say so do).
            PhoneNumber = template.Phone ?? "+390000000000",
            Country = "Italia",
            City = "Roma",
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.Guest, guest.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);

        var checkIn = today.AddDays(template.CheckInOffsetDays).ToDateTime(TimeOnly.MinValue);
        var nights = property.NightlyRate * template.Nights;
        // SR-06a: a booking entered by hand at a price the host agreed. The demo stays carry no tourist tax unless the template says so
        // (NF-100: the amount the engine would have recorded, invented like the rest), so the price is the lodging and the tax comes on top.
        var quoted = nights + property.CleaningFee;
        var total = template.AgreedTotal ?? quoted;
        var priceAgreed = total != quoted;
        var touristTax = template.TouristTax ?? 0m;
        // SR-05a: the history of a demo stay reads in order, as the timeline of the booking shows it: made a month before the
        // arrival and confirmed at once, so a stay already over was not "made today"; checked out at the end of its last night.
        var madeAt = DateTime.SpecifyKind(checkIn.AddDays(-30), DateTimeKind.Utc);
        if (madeAt > UtcNow())
            madeAt = UtcNow();

        // NF-52: the guests of a checked-in stay entrusted to the channel manager arrived three hours before the reset (never before the start of the
        // check-in day): the legal term of the Alloggiati communication runs from the arrival, so the stay reads «awaiting the receipt» at whatever
        // time of the day the showcase is reset.
        DateTime? arrivedAt = null;
        if (template.Compliance is { GuestsEntrusted: true } && template.Status == BookingStatus.CheckedIn)
        {
            var threeHoursAgo = UtcNow().AddHours(-3);
            var startOfCheckInDay = DateTime.SpecifyKind(checkIn, DateTimeKind.Utc);
            arrivedAt = threeHoursAgo < startOfCheckInDay ? startOfCheckInDay : threeHoursAgo;
        }

        // A stay with registered guests declares as many (NF-40: the check-in wizard compares the two); the others keep two adults.
        var registered = template.Guests ?? [];
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(template.Nights),
            NumberOfGuests = registered.Count > 0 ? registered.Count : 2,
            NumberOfAdults = registered.Count > 0 ? registered.Count(g => g.AgeAtCheckIn >= AlloggiatiRecordRules.AdultAge) : 2,
            NumberOfChildren = registered.Count(g => g.AgeAtCheckIn < AlloggiatiRecordRules.AdultAge),
            Status = template.Status,
            Source = template.Source,
            PaymentOption = template.PaymentOption,
            BasePrice = total,
            CleaningFee = Math.Min(property.CleaningFee, total),
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            TotalPrice = total + touristTax,
            PriceOverridden = priceAgreed,
            PriceOverrideNote = priceAgreed ? template.PriceNote : null,
            SpecialRequests = "Prenotazione di dimostrazione con un ospite inventato.",
            CreatedAt = madeAt,
            UpdatedAt = madeAt,
            ConfirmedAt = template.Status == BookingStatus.Pending ? null : madeAt,
            CancelledAt = template.Status == BookingStatus.Cancelled ? madeAt : null,
            CancellationReason = template.Status == BookingStatus.Cancelled ? template.CancellationReason : null,
            // "Pay at the property" is a request the guest confirms by email and the host accepts (BK-06): a demo stay of that kind was both.
            GuestEmailVerifiedAt = template.PaymentOption == PaymentOption.OnSite && template.Status != BookingStatus.Pending ? madeAt : null,
            CheckedOutAt = template.Status == BookingStatus.CheckedOut
                ? DateTime.SpecifyKind(checkIn.AddDays(template.Nights).AddHours(9), DateTimeKind.Utc)
                : null,
            // NF-45: a no-show the host marked in the evening of the arrival day (the status stays Confirmed).
            NoShowAt = template.NoShow && template.Status == BookingStatus.Confirmed
                ? DateTime.SpecifyKind(checkIn.AddHours(20), DateTimeKind.Utc)
                : null,
            ArrivedAt = arrivedAt,
            // E08/F03/US03/T02 (NF-74, NF-77): the reservation of the stay on the channel manager, with the amounts and the flags the import would have
            // stored (invented, like the rest: nothing is sent to Octorate).
            OctorateReservationId = template.Ota?.ReservationId,
            OctorateUpdatedAt = template.Ota is null ? null : madeAt,
            OtaCommission = template.Ota?.Commission,
            OtaNetAmount = template.Ota?.Net,
            OtaPaidAmount = template.Ota?.Paid,
            OtaBalanceAmount = template.Ota?.Balance,
            OtaCompanyCollect = template.Ota?.CompanyCollect,
            OtaCommissionIncludedInPayments = template.Ota?.CommissionIncludedInPayments,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.Booking, booking.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
        await CreateCrmConsentsAsync(orgId, guest.Id, booking.Id, template, madeAt, cancellationToken);
        if (template.Ota?.Sync is { } mirror)
            await CreateOctorateSyncAsync(orgId, property, booking, mirror, madeAt, template.Key, cancellationToken);
        if (template.InvoiceData is { } invoiceData)
            await CreateInvoiceDataAsync(orgId, booking.Id, invoiceData, template.Key, madeAt, cancellationToken);

        // E07/F03/US02 (NF-97): the payments of the stay, so the reports of the money have something to show. They go with the booking at the next reset.
        if (template.Payments is { Count: > 0 } payments)
            await CreatePaymentsAsync(orgId, property, booking, template.Key, payments, today, cancellationToken);

        // NF-40, NF-46: the registered guests of the stay, with complete data of the right shape (they go with the booking at the next reset).
        List<StayGuest> rows = [];
        if (template.Guests is { Count: > 0 } guests)
        {
            var arrival = DateOnly.FromDateTime(checkIn);
            rows = guests.Select((demo, position) => DemoStayGuest(booking, property.OwnerId, demo, position, arrival)).ToList();
            db.StayGuests.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken);
            for (var position = 0; position < rows.Count; position++)
            {
                await MarkAsync(
                    orgId, TestDataEntities.StayGuest, rows[position].Id, TestDataOrigins.Showcase, $"{template.Key}/{position}", cancellationToken);
            }
        }

        // NF-52: what the compliance traffic light finds on the stay (guests entrusted to the channel manager, a receipt the host uploaded).
        if (template.Compliance is { } complianceTemplate)
            await CreateComplianceStateAsync(orgId, property.OwnerId, property, booking, rows, complianceTemplate, template.Key, cancellationToken);
    }

    /// <summary>
    /// Who sends the police and ISTAT communications of a showcase house (E03/F02/US01/T03, NF-52): the choice the demo host stored, the same for both
    /// communications (ADR-029-R5), with the host's word that it saw «Activated» in the Octorate panel when the template says so. Written the way the
    /// sender store writes it (both senders equal, who and when), and gone with its house at the next reset (the table cascades); marked like the rest.
    /// </summary>
    internal async Task CreateComplianceSenderAsync(
        Guid orgId,
        string ownerId,
        Property property,
        DemoComplianceSenderTemplate template,
        string templateKey,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var sender = new PropertyComplianceSender
        {
            OrgId = orgId,
            PropertyId = property.Id,
            AlloggiatiSender = template.Sender,
            IstatSender = template.Sender,
            LastChangedAt = now.AddDays(-3),
            LastChangedByUserId = ownerId,
            ActivationDeclaredAt = template.ActivationDeclared ? now.AddDays(-2) : null,
            ActivationDeclaredByUserId = template.ActivationDeclared ? ownerId : null,
            CreatedAt = now.AddDays(-3),
        };
        db.PropertyComplianceSenders.Add(sender);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.PropertyComplianceSender, sender.Id, TestDataOrigins.Showcase, templateKey, cancellationToken);
    }

    /// <summary>
    /// What the compliance traffic light finds on a showcase stay (E03/F02/US01/T03, NF-52). Guests entrusted: one <see cref="StayGuestOctorateLink"/>
    /// for each registered guest, accepted by Octorate for good (not as a draft) two hours before the reset, as the channel manager holds a guest it
    /// sends for; the stay then reads yellow while the receipt is awaited. Receipt archived: a <see cref="ManualComplianceProof"/> of the Alloggiati
    /// communication of the arrival day, with an invented one-page PDF (it says it is test data) in the private bucket; the stay then reads green.
    /// Nothing is sent to Octorate or to the police. The rows go with the stay and the house at the next reset (the tables cascade) and are marked like
    /// the rest; the reset removes the file.
    /// </summary>
    private async Task CreateComplianceStateAsync(
        Guid orgId,
        string ownerId,
        Property property,
        Booking booking,
        IReadOnlyList<StayGuest> guests,
        DemoComplianceTemplate template,
        string templateKey,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        if (template.GuestsEntrusted)
        {
            var links = guests
                .Select(guest => new StayGuestOctorateLink
                {
                    OrgId = orgId,
                    BookingId = booking.Id,
                    Position = guest.Position,
                    OctorateGuestId = $"TEST-OCT-G-{booking.Id:N}-{guest.Position}",
                    PayloadHash = DemoPayloadHash(booking.Id, guest.Position),
                    SentAsDraft = false,
                    Status = OctorateSyncStatus.Sent,
                    Attempts = 1,
                    SentAt = now.AddHours(-2),
                    UpdatedAt = now.AddHours(-2),
                })
                .ToList();
            db.StayGuestOctorateLinks.AddRange(links);
            await db.SaveChangesAsync(cancellationToken);
            for (var position = 0; position < links.Count; position++)
            {
                await MarkAsync(
                    orgId, TestDataEntities.StayGuestOctorateLink, links[position].Id, TestDataOrigins.Showcase, $"{templateKey}/{position}", cancellationToken);
            }

            // NF-47: and the line of the log of what was sent that the send of the guests to the channel manager writes (the page of the stay shows it).
            await AddSendLogLineAsync(orgId, property, booking, ComplianceSender.ChannelManager, now.AddHours(-2), null, templateKey, cancellationToken);
        }

        if (template.ReceiptArchived)
        {
            var proofId = Guid.NewGuid();
            var pdf = DemoDocumentPdf.Create(
                "RICEVUTA ALLOGGIATI WEB DI TEST",
                DemoDocumentPdf.Marker + " CasaZen. Nessun valore fuori dall'ambiente di test.");
            var key = StorageKeys.ManualComplianceProof(orgId, proofId, "ricevuta-di-test.pdf");
            await using (var stream = new MemoryStream(pdf))
                await storage.PutAsync(StorageBucket.Private, key, stream, "application/pdf", cancellationToken);

            var proof = new ManualComplianceProof
            {
                Id = proofId,
                OrgId = orgId,
                PropertyId = property.Id,
                Kind = ManualProofKind.AlloggiatiReceipt,
                Day = RomeCalendar.DateInRome(booking.CheckInDate),
                FileKey = key,
                ContentType = "application/pdf",
                UploadedByUserId = ownerId,
                UploadedAt = now.AddHours(-1),
            };
            db.ManualComplianceProofs.Add(proof);
            await db.SaveChangesAsync(cancellationToken);
            await MarkAsync(orgId, TestDataEntities.ManualComplianceProof, proof.Id, TestDataOrigins.Showcase, templateKey, cancellationToken);

            // NF-47: the host sent the communication on the portal on the arrival day and declared it: the line of the log of what was sent, by the owner.
            await AddSendLogLineAsync(
                orgId, property, booking, ComplianceSender.Manual, DateTime.SpecifyKind(proof.Day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc), ownerId, templateKey, cancellationToken);
        }
    }

    /// <summary>One line of the log of what was sent for a showcase stay (E03/F02/US03/T01, NF-47): no guest data, no reference, marked like the rest.</summary>
    private async Task AddSendLogLineAsync(
        Guid orgId,
        Property property,
        Booking booking,
        ComplianceSender sender,
        DateTime sentAt,
        string? byUserId,
        string templateKey,
        CancellationToken cancellationToken)
    {
        var line = new ComplianceSendLogEntry
        {
            OrgId = orgId,
            PropertyId = property.Id,
            BookingId = booking.Id,
            Kind = ComplianceSendKind.AlloggiatiGuests,
            Sender = sender,
            SentAt = sentAt,
            ByUserId = byUserId,
        };
        db.ComplianceSendLogEntries.Add(line);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.ComplianceSendLogEntry, line.Id, TestDataOrigins.Showcase, templateKey, cancellationToken);
    }

    /// <summary>
    /// The hash of what the showcase says it sent to Octorate for a guest: SHA-256 (hex, lower case, 64 characters) of an invented text, as the
    /// column wants. No personal data goes into it.
    /// </summary>
    private static string DemoPayloadHash(Guid bookingId, int position) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"TEST|demo-guest|{bookingId:N}|{position}")));

    /// <summary>
    /// The mirror of a showcase booking on Octorate (E08/F03/US03/T02, NF-74): the row the push would have left for the invented reservation, in the
    /// state the template says. A synced one was confirmed when the booking was made, and so was the create that precedes a refused change; a
    /// conflict keeps the stay Octorate holds (the booking's own with the arrival moved), so the booking page shows both versions. It goes with its
    /// booking at the next reset (cascade) and is marked like the rest.
    /// </summary>
    private async Task CreateOctorateSyncAsync(
        Guid orgId,
        Property property,
        Booking booking,
        DemoOctorateSyncTemplate template,
        DateTime madeAt,
        string templateKey,
        CancellationToken cancellationToken)
    {
        // The create is the first operation: a later one (a change that was refused) follows a reservation Octorate had already confirmed.
        var confirmed = template.State == OctorateSyncState.Synced || template.LastOperation != OctorateSyncOperation.Create;
        var sync = new OctorateBookingSync
        {
            OrgId = orgId,
            BookingId = booking.Id,
            PropertyId = property.Id,
            State = template.State,
            LastOperation = template.LastOperation,
            Attempts = 1,
            LastAttemptAt = madeAt,
            LastSyncedAt = confirmed ? madeAt : null,
            ErrorCode = template.ErrorCode,
            OctorateUpdateTime = confirmed ? madeAt : null,
            OctorateVersionJson = template.State == OctorateSyncState.Conflict ? ConflictVersionJson(booking, template) : null,
            CreatedAt = madeAt,
            UpdatedAt = madeAt,
        };
        db.OctorateBookingSyncs.Add(sync);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.OctorateBookingSync, sync.Id, TestDataOrigins.Showcase, templateKey, cancellationToken);
    }

    /// <summary>The stay Octorate holds in a showcase conflict, in the stored form: the booking's own, the arrival and the departure moved by the same days.</summary>
    private static string ConflictVersionJson(Booking booking, DemoOctorateSyncTemplate template)
    {
        var shift = template.VersionCheckInShiftDays ?? 0;
        return new OctorateVersionSnapshot(
            DateOnly.FromDateTime(booking.CheckInDate).AddDays(shift),
            DateOnly.FromDateTime(booking.CheckOutDate).AddDays(shift),
            template.VersionGuests ?? booking.NumberOfGuests).ToJson();
    }

    /// <summary>
    /// The payments of a showcase stay (E07/F03/US02, NF-97): each one at 10:00 in Rome, <c>DaysAgo</c> days before the reset (never in the future). A payment that
    /// is completed (or partly refunded) has its processing instant, like one the provider confirmed; a card payment the provider settled also has a TEST payment
    /// intent and the commission, the net and the credit day the capture stores (so the capture never reads it: it is marked as read). Nothing is sent to a
    /// provider. The rows are marked as showcase data and go with the booking at the next reset (the table cascades).
    /// </summary>
    private async Task CreatePaymentsAsync(
        Guid orgId,
        Property property,
        Booking booking,
        string templateKey,
        IReadOnlyList<DemoPaymentTemplate> templates,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var currency = StayCurrency.ForProperty(property);
        var now = UtcNow();
        var rows = new List<Payment>();
        for (var position = 0; position < templates.Count; position++)
        {
            var demo = templates[position];
            var day = today.AddDays(-demo.DaysAgo);
            var at = RomeCalendar.ToUtc(day, new TimeOnly(10, 0));
            if (at > now)
                at = now;

            var settled = demo.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded;
            var payment = new Payment
            {
                OrgId = orgId,
                BookingId = booking.Id,
                Amount = demo.Amount,
                RefundedAmount = demo.RefundedAmount,
                Status = demo.Status,
                Method = demo.Method,
                TransactionId = $"TEST-{templateKey}-{position}",
                Description = "Pagamento di dimostrazione (TEST)",
                ProcessedAt = settled ? at : null,
                CreatedAt = at,
                UpdatedAt = at,
            };
            if (demo.Card is { } card)
            {
                payment.StripePaymentIntentId = $"pi_TEST_{templateKey}_{position}";
                payment.ProviderFeeAmount = card.Fee;
                payment.ProviderNetAmount = card.Net;
                payment.ProviderBalanceCurrency = currency;
                payment.ProviderBalanceTransactionId = $"txn_TEST_{templateKey}_{position}";
                payment.ProviderAvailableOn = DateTime.SpecifyKind(day.AddDays(card.CreditAfterDays).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
                payment.ProviderFeeFetchedAt = at.AddHours(1) > now ? now : at.AddHours(1);
            }

            rows.Add(payment);
        }

        db.Payments.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        for (var position = 0; position < rows.Count; position++)
            await MarkAsync(orgId, TestDataEntities.Payment, rows[position].Id, TestDataOrigins.Showcase, $"{templateKey}/{position}", cancellationToken);
    }

    /// <summary>
    /// Who the invoice of a showcase stay is made out to (NF-81): the guest's «yes» of the online check-in and the data of the recipient, in the
    /// row the real flows write (encrypted by the context). It goes with its booking when the reset deletes it (the table cascades).
    /// </summary>
    private async Task CreateInvoiceDataAsync(
        Guid orgId, Guid bookingId, DemoInvoiceData template, string templateKey, DateTime madeAt, CancellationToken cancellationToken)
    {
        var row = new BookingInvoiceData
        {
            OrgId = orgId,
            BookingId = bookingId,
            InvoiceRequested = true,
            InvoiceRequestedAt = madeAt,
            RecipientKind = template.Kind,
            Name = template.Name,
            FiscalCode = template.FiscalCode,
            VatNumber = template.VatNumber,
            AddressStreet = template.AddressStreet,
            AddressCivic = template.AddressCivic,
            PostalCode = template.PostalCode,
            City = template.City,
            Province = template.Province,
            CountryCode = BookingInvoiceData.DefaultCountryCode,
            SdiRecipientCode = template.SdiRecipientCode,
            Pec = template.Pec,
            Source = template.Source,
            CreatedAt = madeAt,
            UpdatedAt = madeAt,
        };
        db.BookingInvoiceData.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.BookingInvoiceData, row.Id, TestDataOrigins.Showcase, templateKey, cancellationToken);
    }

    /// <summary>
    /// A registered guest of a demo stay: complete Alloggiati data of the right shape (born in Rome, Italian, an identity card with TEST in its
    /// number for a single guest or a head), born so that the guest has the template's age on the arrival day.
    /// </summary>
    private static StayGuest DemoStayGuest(Booking booking, string ownerId, DemoStayGuestTemplate demo, int position, DateOnly arrival)
    {
        var born = arrival.AddYears(-demo.AgeAtCheckIn).AddDays(-30);
        var row = new StayGuest
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            GuestId = position == 0 ? booking.GuestId : null,
            Position = position,
            Type = demo.Type,
            FirstName = demo.FirstName,
            LastName = demo.LastName,
            Gender = position % 2 == 0 ? Gender.Female : Gender.Male,
            DateOfBirth = born.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            BornInItaly = true,
            BirthComuneName = "Roma",
            BirthProvince = "RM",
            CitizenshipName = "Italia",
            ResidenceInItaly = true,
            ResidenceComuneName = "Roma",
            ResidenceProvince = "RM",
            TaxExemptionCode = demo.TaxExemptionCode,
            DataSource = StayGuestDataSource.Host,
            EnteredByUserId = ownerId,
            CreatedAt = booking.CreatedAt,
            UpdatedAt = booking.CreatedAt,
        };
        if (AlloggiatiRecordRules.RequiresDocument(demo.Type))
        {
            row.DocumentType = GuestDocumentType.IdentityCard;
            row.DocumentNumber = $"CATEST{position + 1:D3}";
            row.DocumentIssuePlaceName = "Roma";
            // NF-40: an expired document is a warning of the check-in wizard (the host records what the guest showed).
            row.DocumentIssuedOn = demo.DocumentExpired ? arrival.AddYears(-10).AddDays(-20) : arrival.AddYears(-3);
            row.DocumentExpiresOn = demo.DocumentExpired ? arrival.AddDays(-20) : arrival.AddYears(7);
        }

        // NF-40: data still to complete, which the check-in wizard asks for.
        if (demo.BirthPlaceMissing)
        {
            row.BirthComuneName = string.Empty;
            row.BirthProvince = null;
        }

        return row;
    }

    /// <summary>
    /// What the CRM guests of the showcase were shown and agreed to (NF-89, NF-90), invented and marked: the soft opt-in notice of a direct customer,
    /// and the marketing consent of the online check-in with the address confirmed (double opt-in), in the rows the real flows write. They go with
    /// their guest and their booking when the reset deletes them. The CRM sync reads them to build the contacts.
    /// </summary>
    private async Task CreateCrmConsentsAsync(
        Guid orgId, Guid guestId, Guid bookingId, DemoBookingTemplate template, DateTime madeAt, CancellationToken cancellationToken)
    {
        if (!template.SoftOptInNotice && !template.CheckInMarketingConsent)
            return;

        var history = new List<(string EntityType, GuestConsentRecord Row)>();
        if (template.SoftOptInNotice)
        {
            history.Add((TestDataEntities.GuestConsent, new GuestConsentRecord
            {
                OrgId = orgId,
                GuestId = guestId,
                Purpose = GuestConsentPurpose.SoftOptInNotice,
                Action = GuestConsentAction.NoticePresented,
                Version = DemoShowcaseCatalog.CrmTextVersion,
                Source = GuestConsentSource.BookingCheckout,
                RecordedAt = madeAt,
            }));
        }

        MarketingConsentRecord? consent = null;
        if (template.CheckInMarketingConsent)
        {
            history.Add((TestDataEntities.GuestConsent, new GuestConsentRecord
            {
                OrgId = orgId,
                GuestId = guestId,
                Purpose = GuestConsentPurpose.Marketing,
                Action = GuestConsentAction.Granted,
                Version = DemoShowcaseCatalog.CrmTextVersion,
                Source = GuestConsentSource.GuestPortal,
                RecordedAt = madeAt,
            }));
            consent = new MarketingConsentRecord
            {
                OrgId = orgId,
                BookingId = bookingId,
                GuestId = guestId,
                MarketingEmail = template.Email,
                ConsentEmail = true,
                ConsentWhatsApp = false,
                EmailConfirmed = true,
                ConsentTimestamp = madeAt,
                ConsentTextVersion = DemoShowcaseCatalog.CrmTextVersion,
            };
            db.MarketingConsentRecords.Add(consent);
        }

        foreach (var (_, row) in history)
            db.GuestConsentRecords.Add(row);

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (entityType, row) in history)
            await MarkAsync(orgId, entityType, row.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);

        if (consent is not null)
            await MarkAsync(orgId, TestDataEntities.MarketingConsent, consent.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
    }

    private async Task<bool> FillComplianceAsync(
        Property property,
        HashSet<string> usedCins,
        string origin,
        string templateKey,
        CancellationToken cancellationToken)
    {
        var changed = FillBase(property);
        changed |= await FillComuneAsync(property, cancellationToken);
        changed |= FillCin(property, usedCins);
        changed |= await FillDocumentsAsync(property, origin, templateKey, cancellationToken);
        changed |= await FillSafetyAsync(property, origin, templateKey, cancellationToken);
        if (!changed)
            return false;

        property.IsTestData = true;
        property.UpdatedAt = UtcNow();
        await MarkAsync(property.OrgId, TestDataEntities.Property, property.Id, origin, templateKey, cancellationToken);
        return true;
    }

    private static bool FillBase(Property property)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(property.Name))
        {
            property.Name = "Annuncio demo";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(property.Address))
        {
            property.Address = "Via Demo 1";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(property.City))
        {
            property.City = "Como";
            changed = true;
        }

        if (property.MaxGuests < 1 && property.RentalMode == RentalMode.Short)
        {
            property.MaxGuests = 2;
            changed = true;
        }

        if (property.NightlyRate <= 0 && property.RentalMode == RentalMode.Short)
        {
            property.NightlyRate = 100m;
            changed = true;
        }

        if (property.Bathrooms < 1)
        {
            property.Bathrooms = 1;
            changed = true;
        }

        return changed;
    }

    private async Task<bool> FillComuneAsync(Property property, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(property.ComuneIstatCode) && !string.IsNullOrWhiteSpace(property.RegionCode))
            return false;

        if (!string.IsNullOrWhiteSpace(property.ComuneIstatCode))
        {
            var regionIstat = await db.Comuni.AsNoTracking()
                .Where(c => c.IstatCode == property.ComuneIstatCode)
                .Select(c => c.RegionIstatCode)
                .FirstOrDefaultAsync(cancellationToken);
            var region = ItalianRegions.FindByIstatCode(regionIstat)?.Code;
            if (region is null)
                return false;

            property.RegionCode = region;
            return true;
        }

        var known = DemoShowcaseCatalog.Properties
            .Select(template => template.Place)
            .FirstOrDefault(place =>
                string.Equals(ComuneNames.Normalize(place.City), ComuneNames.Normalize(property.City), StringComparison.Ordinal));
        DemoPlace? resolved = null;
        if (known is not null)
        {
            resolved = await ResolvePlaceAsync(known, cancellationToken);
        }
        else
        {
            var normalized = ComuneNames.Normalize(property.City);
            var comune = string.IsNullOrEmpty(normalized)
                ? null
                : await db.Comuni.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.IsActive && c.NormalizedName == normalized, cancellationToken);
            if (comune?.RegionCode is { Length: > 0 } region)
                resolved = new DemoPlace(comune.Name, comune.IstatCode, region, property.PostalCode, property.Address);
        }

        if (resolved is null)
            return false;

        property.ComuneIstatCode = resolved.IstatCode;
        property.RegionCode = resolved.RegionCode;
        return true;
    }

    private static bool FillCin(Property property, HashSet<string> usedCins)
    {
        if (CinFormat.IsValid(property.CinCode))
            return false;

        var istat = property.ComuneIstatCode is { Length: 6 } code && code.All(char.IsAsciiDigit) ? code : "058091";
        for (var sequence = 1; sequence <= 99; sequence++)
        {
            var cin = DemoCin.ForIstat(istat, sequence);
            if (!usedCins.Add(cin))
                continue;

            property.CinCode = cin;
            return true;
        }

        throw new DomainRuleException("demo_cin_exhausted", "DemoCinExhausted");
    }

    private async Task<bool> FillDocumentsAsync(
        Property property, string origin, string templateKey, CancellationToken cancellationToken)
    {
        var required = DemoRequiredDocuments.ForRegion(complianceOptions.Value.RequiredDocuments, property.RegionCode);
        var present = (await db.PropertyDocuments.IgnoreQueryFilters([Tenant])
                .Where(d => d.PropertyId == property.Id)
                .Select(d => d.DocumentType)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var changed = false;
        var ape = 1;
        foreach (var type in required)
        {
            if (!present.Add(type))
                continue;

            var heading = type switch
            {
                DocumentType.Ape => "ATTESTATO APE DI TEST",
                DocumentType.PropertyLicense => "LICENZA COMUNALE DI TEST",
                _ => "CERTIFICATO CIN DI TEST",
            };
            var pdf = DemoDocumentPdf.Create(heading, DemoDocumentPdf.Marker + " CasaZen. Nessun valore fuori dall'ambiente di test.");
            var fileName = $"TEST-{type}.pdf";
            var key = StorageKeys.PropertyDocument(property.Id, fileName);
            await using var stream = new MemoryStream(pdf);
            await storage.PutAsync(StorageBucket.Private, key, stream, "application/pdf", cancellationToken);

            var document = new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                FileName = fileName,
                StorageUrl = key,
                DocumentType = type,
                UploadedBy = DemoSafetyChecklist.Actor,
                UploadedAt = UtcNow(),
                IsTestData = true,
            };
            if (type == DocumentType.Ape)
            {
                document.ApeCode = $"TEST-APE-{ape:0000}";
                document.ApeEnergyClass = "C";
                ape++;
            }

            db.PropertyDocuments.Add(document);
            await db.SaveChangesAsync(cancellationToken);
            await MarkAsync(property.OrgId, TestDataEntities.PropertyDocument, document.Id, origin, templateKey, cancellationToken);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> FillSafetyAsync(
        Property property, string origin, string templateKey, CancellationToken cancellationToken)
    {
        if (property.RentalMode != RentalMode.Short)
            return false;

        var checklist = await db.PropertySafetyChecklists.IgnoreQueryFilters([Tenant])
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.PropertyId == property.Id, cancellationToken);
        if (checklist is not null && SafetyChecklistRules.Evaluate(checklist).IsComplete)
            return false;

        var now = UtcNow();
        checklist ??= new PropertySafetyChecklist
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            CreatedAt = now,
        };
        DemoSafetyChecklist.Apply(checklist, now);
        if (db.Entry(checklist).State == EntityState.Detached)
            db.PropertySafetyChecklists.Add(checklist);

        if (checklist.Items.Count > 0)
        {
            db.PropertySafetyChecklistItems.RemoveRange(checklist.Items);
            checklist.Items.Clear();
        }

        foreach (var item in DemoSafetyChecklist.Items(property.OrgId, DateOnly.FromDateTime(now)))
        {
            item.ChecklistId = checklist.Id;
            checklist.Items.Add(item);
        }

        if (!SafetyChecklistRules.Evaluate(checklist).IsComplete)
            throw new InvalidOperationException("The demo safety checklist is still incomplete.");

        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(property.OrgId, TestDataEntities.SafetyChecklist, checklist.Id, origin, templateKey, cancellationToken);
        return true;
    }

    private async Task<DemoPlace> ResolvePlaceAsync(DemoPlace fallback, CancellationToken cancellationToken)
    {
        var normalized = ComuneNames.Normalize(fallback.City);
        if (string.IsNullOrEmpty(normalized))
            return fallback;

        var comune = await db.Comuni.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsActive && c.NormalizedName == normalized, cancellationToken);
        if (comune?.RegionCode is not { Length: > 0 } region)
            return fallback;

        return fallback with { IstatCode = comune.IstatCode, RegionCode = region, City = comune.Name };
    }

    private async Task MarkAsync(
        Guid orgId, string entityType, Guid entityId, string origin, string? templateKey, CancellationToken cancellationToken)
    {
        var existing = await Markers().FirstOrDefaultAsync(m => m.EntityType == entityType && m.EntityId == entityId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Origin != TestDataOrigins.Showcase)
                existing.Origin = origin;
            existing.TemplateKey ??= templateKey;
            return;
        }

        db.TestDataMarkers.Add(new TestDataMarker
        {
            OrgId = orgId,
            EntityType = entityType,
            EntityId = entityId,
            Origin = origin,
            TemplateKey = templateKey,
            CreatedAt = UtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<HashSet<string>> UsedCinsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var codes = await Properties(orgId)
            .Where(p => p.CinCode != null)
            .Select(p => p.CinCode!)
            .ToListAsync(cancellationToken);
        return codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HashSet<Guid>> DraftIdsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var ids = await Markers()
            .Where(m => m.OrgId == orgId && m.TemplateKey == DemoShowcaseCatalog.DraftKey)
            .Select(m => m.EntityId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    private async Task<string> OwnerIdAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var owner = await db.OrgMembers.IgnoreQueryFilters([Tenant])
            .Where(m => m.OrgId == orgId && m.Role == OrgRole.Owner)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(owner))
            return owner;

        return await Properties(orgId).Select(p => p.OwnerId).FirstOrDefaultAsync(cancellationToken) ?? DemoSafetyChecklist.Actor;
    }

    private async Task<string> UniqueSlugAsync(Guid orgId, string slug, CancellationToken cancellationToken)
    {
        var candidate = slug;
        var suffix = 2;
        while (await Properties(orgId).AnyAsync(p => p.Slug == candidate, cancellationToken))
            candidate = $"{slug}-{suffix++}";
        return candidate;
    }

    /// <summary>
    /// The demo landlord can show a linked bank without a raw account number. A link the org already stored is left as it is.
    /// </summary>
    private async Task ApplyShowcaseBankLinkAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var org = await db.Orgs.SingleOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null || !string.IsNullOrWhiteSpace(org.FinancialConnectionsAccountId))
            return;

        org.FinancialConnectionsAccountId = DemoShowcaseCatalog.BankLinkAccountId;
        org.UpdatedAt = UtcNow();
    }

    private async Task RequireOrgAsync(Guid orgId, CancellationToken cancellationToken)
    {
        if (!await db.Orgs.AsNoTracking().AnyAsync(o => o.Id == orgId, cancellationToken))
        {
            throw new NotFoundException("Demo target org was not found.")
            {
                Code = "demo_org_not_found",
                MessageKey = "DemoOrgNotFound",
            };
        }
    }

    private async Task<DemoEnvironmentSetting> EnsureEnabledAsync(CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var settings = await SettingsAsync(cancellationToken);
        if (!settings.Enabled)
            throw new DomainConflictException("demo_disabled", "DemoEnvironmentDisabled");
        return settings;
    }

    private void EnsureAvailable()
    {
        if (!gate.IsAvailable)
        {
            throw new NotFoundException("Demo environment is not available on this host.")
            {
                Code = "demo_unavailable",
                MessageKey = "DemoEnvironmentUnavailable",
            };
        }
    }

    private async Task<DemoEnvironmentSetting> SettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.DemoEnvironmentSettings
            .FirstOrDefaultAsync(s => s.Id == DemoEnvironmentSetting.SingletonId, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new DemoEnvironmentSetting
        {
            Id = DemoEnvironmentSetting.SingletonId,
            TemplateVersion = DemoShowcaseCatalog.Version,
            UpdatedAt = UtcNow(),
        };
        db.DemoEnvironmentSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private IQueryable<Property> Properties(Guid orgId) =>
        db.Properties.IgnoreQueryFilters([Tenant]).Where(p => p.OrgId == orgId);

    private IQueryable<TestDataMarker> Markers() =>
        db.TestDataMarkers.IgnoreQueryFilters([Tenant]);

    private DemoEnvironmentStatus Status(DemoEnvironmentSetting settings, int showcase) =>
        new(true, settings.Enabled, DemoShowcaseCatalog.Version, settings.LastResetAt, showcase, DemoShowcaseCatalog.Scenarios);

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
