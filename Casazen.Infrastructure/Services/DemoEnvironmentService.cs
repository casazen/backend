using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Demo environment of a non-production installation (docs/runbooks/demo-environment.md). Invented CIN, PDF documents
/// and checklists are valid for the activation rules and marked <see cref="Property.IsTestData"/>. The reset deletes
/// only rows marked <see cref="TestDataOrigins.Showcase"/>.
/// </summary>
public class DemoEnvironmentService(
    AppDbContext db,
    IFileStorage storage,
    IPropertyComplianceStatusService compliance,
    IOptions<ComplianceOptions> complianceOptions,
    IDemoEnvironmentGate gate,
    ILogger<DemoEnvironmentService> logger,
    TimeProvider clock) : IDemoEnvironmentService
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

        foreach (var invite in DemoShowcaseCatalog.HostInvites)
            await CreateHostInviteAsync(orgId, ownerId, invite, cancellationToken);

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

    private async Task CreateShowcasePropertyAsync(
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

        db.Properties.Add(property);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.Property, property.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);

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

    private async Task CreateBookingAsync(
        Guid orgId, Property property, DemoBookingTemplate template, DateOnly today, CancellationToken cancellationToken)
    {
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = template.FirstName,
            LastName = template.LastName,
            Email = template.Email,
            PhoneNumber = "+390000000000",
            Country = "Italia",
            City = "Roma",
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.Guest, guest.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);

        var checkIn = today.AddDays(template.CheckInOffsetDays).ToDateTime(TimeOnly.MinValue);
        var nights = property.NightlyRate * template.Nights;
        // SR-06a: a booking entered by hand at a price the host agreed (the demo stays carry no tourist tax, so the total is the lodging).
        var quoted = nights + property.CleaningFee;
        var total = template.AgreedTotal ?? quoted;
        var priceAgreed = total != quoted;
        // SR-05a: the history of a demo stay reads in order, as the timeline of the booking shows it: made a month before the
        // arrival and confirmed at once, so a stay already over was not "made today"; checked out at the end of its last night.
        var madeAt = DateTime.SpecifyKind(checkIn.AddDays(-30), DateTimeKind.Utc);
        if (madeAt > UtcNow())
            madeAt = UtcNow();
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(template.Nights),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = template.Status,
            Source = template.Source,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = total,
            CleaningFee = Math.Min(property.CleaningFee, total),
            TotalPrice = total,
            PriceOverridden = priceAgreed,
            PriceOverrideNote = priceAgreed ? template.PriceNote : null,
            SpecialRequests = "Prenotazione di dimostrazione con un ospite inventato.",
            CreatedAt = madeAt,
            UpdatedAt = madeAt,
            ConfirmedAt = template.Status == BookingStatus.Pending ? null : madeAt,
            CancelledAt = template.Status == BookingStatus.Cancelled ? madeAt : null,
            CheckedOutAt = template.Status == BookingStatus.CheckedOut
                ? DateTime.SpecifyKind(checkIn.AddDays(template.Nights).AddHours(9), DateTimeKind.Utc)
                : null,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(cancellationToken);
        await MarkAsync(orgId, TestDataEntities.Booking, booking.Id, TestDataOrigins.Showcase, template.Key, cancellationToken);
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
