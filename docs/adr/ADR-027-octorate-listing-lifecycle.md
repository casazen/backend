# ADR-027: Octorate Listing Lifecycle Management

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-27 (channel connection), NF-29 (listing write)

## Context

CasaZen writes listing content (descriptions, amenities, photos) to Octorate, which syncs to OTAs. Conflict detection is required when listings are modified outside CasaZen. Expedia-managed fields must be read-only in CasaZen per LST-01 section 8.2.

**Existing code:**
- No Octorate listing integration exists
- Authority table defined in requirements LST-01 section 8.2

**Repo scope:** Backend (listing sync, conflict detection), Frontend (conflict dialog, read-only fields), Mobile (none - listing management is desktop)

## Decision

### Authority Table (LST-01 Section 8.2)

| Field Category | CasaZen Authority | Octorate Authority |
|---|---|---|
| Descriptions, amenities, house rules | Write | Read |
| Photos | Write (except Expedia-managed) | Read |
| Rates, availability | Read | Write |
| Channel-specific (Expedia extranet) | Read-only | N/A |

### Conflict Detection

```csharp
public class OctorateListingSync
{
    public async Task<ListingSyncResult> SyncListingAsync(Guid listingId, ListingUpdateDto update, CancellationToken ct)
    {
        var listing = await _db.Listings.FindAsync(listingId, ct);
        var octorateData = await _octorate.GetPropertyAsync(listing.OctoratePropertyId, ct);
        
        // Check for external modification
        if (octorateData.ModifiedAt > listing.OctorateLastSyncedAt)
        {
            var conflicts = DetectConflicts(listing, octorateData, update);
            if (conflicts.Any())
            {
                _events.Record(DomainEventType.OctorateConflictDetected, listingId, conflicts);
                return new ListingSyncResult
                {
                    Status = SyncStatus.Conflict,
                    Conflicts = conflicts.Select(c => new ConflictDto
                    {
                        Field = c.Field,
                        CasazenValue = c.LocalValue,
                        OctorateValue = c.RemoteValue
                    }).ToArray()
                };
            }
        }
        
        // No conflict or user chose "Keep mine"
        await _octorate.UpdatePropertyAsync(listing.OctoratePropertyId, MapToOctorate(update), ct);
        listing.OctorateLastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        
        _events.Record(DomainEventType.ConflictResolved, listingId, new { Choice = "Keep" });
        return new ListingSyncResult { Status = SyncStatus.Success };
    }
}
```

### User Choice: Keep/Take

Per Q-A04 (LST-01 section 8.2): user chooses, CasaZen is the writer.

```typescript
// Frontend conflict dialog
function ConflictDialog({ conflicts, onKeepMine, onTakeTheirs }) {
    return (
        <Dialog>
            <DialogTitle>{t('listing.conflict.title')}</DialogTitle>
            <Table>
                <TableHead>
                    <TableRow>
                        <TableCell>{t('field')}</TableCell>
                        <TableCell>{t('yourVersion')}</TableCell>
                        <TableCell>{t('octorateVersion')}</TableCell>
                    </TableRow>
                </TableHead>
                <TableBody>
                    {conflicts.map(c => (
                        <TableRow key={c.field}>
                            <TableCell>{t(`listing.field.${c.field}`)}</TableCell>
                            <TableCell>{c.casazenValue}</TableCell>
                            <TableCell>{c.octorateValue}</TableCell>
                        </TableRow>
                    ))}
                </TableBody>
            </Table>
            <DialogActions>
                <Button onClick={onTakeTheirs}>{t('listing.conflict.takeOctorate')}</Button>
                <Button onClick={onKeepMine} variant="primary">{t('listing.conflict.keepMine')}</Button>
            </DialogActions>
        </Dialog>
    );
}
```

### Expedia-Managed Fields

Per Q-A13: read-only display in CasaZen for fields where Expedia is manager.

```csharp
public class ListingChannelConfig
{
    public Guid ListingId { get; set; }
    public OtaChannel Channel { get; set; }
    public string ManagedFieldsJson { get; set; }  // ["photos", "description"]
}
```

Frontend disables fields where `ManagedFieldsJson` includes the field name, with lock icon and tooltip.

### Listing Import (Reuse Existing)

Per Q-A14: CasaZen reuses existing Octorate properties from connection wizard, does not create new Octorate properties.

```csharp
public async Task ImportPropertiesAsync(Guid orgId, CancellationToken ct)
{
    var connection = await _db.OctorateConnections.FindAsync(orgId, ct);
    var properties = await _octorate.GetPropertiesAsync(ct);
    
    foreach (var prop in properties)
    {
        var listing = new Listing
        {
            OrgId = orgId,
            OctoratePropertyId = prop.Id,
            Name = prop.Name,
            OctorateLastSyncedAt = DateTime.UtcNow
        };
        _db.Listings.Add(listing);
    }
    
    await _db.SaveChangesAsync(ct);
}
```

### Rejected Alternatives

- **Three-way merge:** Too complex; two-way (Keep/Take) per Q-A04
- **Automatic overwrite:** Data loss risk; user must choose per LST-01
- **Create Octorate properties:** Pilot properties already exist in Octorate; reuse per Q-A14

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-027-R1 | P0 | Conflict detection: on listing read, if `OctorateModifiedAt > CasazenLastSyncedAt`, set `ListingConflictState = Detected`, log `OctorateConflictDetected` event, prompt user |
| ADR-027-R2 | P0 | Listing write with conflict MUST return 409 `listing_modified_outside` with conflict fields (CasaZen value, Octorate value) |
| ADR-027-R3 | P0 | "Take Octorate version" MUST show confirmation: "Your local changes will be lost: {changed field list}. Continue?" before overwriting |
| ADR-027-R4 | P0 | "Keep mine" with `overwrite=true` query param MUST force-write to Octorate, overwriting external changes |
| ADR-027-R5 | P0 | Conflict resolution MUST log: `ConflictResolved` event (ListingId, Choice: Keep|Take, UserId, ChangedFields) |
| ADR-027-R6 | P0 | Expedia-managed fields (`ListingChannelConfig.ManagedFieldsJson`) MUST render as read-only with lock icon and tooltip "This field is managed on Expedia" |
| ADR-027-R7 | P0 | Write to Expedia-managed field MUST return 422 `expedia_field_readonly` |
| ADR-027-R8 | P0 | Listing import MUST reuse existing Octorate properties from connection; MUST NOT create new Octorate properties |
| ADR-027-R9 | P1 | i18n keys MUST exist: `listing.conflict.title`, `listing.conflict.keepMine`, `listing.conflict.takeOctorate`, `listing.managedOnChannel.tooltip` (IT/EN) |
| ADR-027-R10 | P1 | Conflict detection MUST use 5-minute tolerance window for timestamp comparison (clock skew mitigation) |

## Consequences

**Follow-on work:**
- Backend: `ListingChannelConfig` entity, conflict detection service, import service, sync job
- Frontend: Conflict dialog, read-only field rendering, lock icon component
- Mobile: None (listing management is desktop per product rule)
- Tests: Conflict detection integration test, Expedia-managed field validation test

**Risks:**
- Conflict false positives: 5-minute tolerance window per R10
- "Keep/Take" UX confusion: include help text explaining consequence
- Large imports (1000 properties for agency): progress bar needed (P1)

**CI cost:** +10s for conflict detection tests
