# ADR-024: Wizard Draft Save and Resume

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-03 (never lose data), NF-06 (save on step/blur), NF-10 (resume), NF-13 (guided layer)

## Context

UX-02 "never leave the process" requires guided wizards that save progress automatically, enable cross-device resume, and never lose user input. Draft save must handle PII (guest data, document numbers) with encryption, conflict detection across devices, and mobile offline support.

**Existing code:**
- `src/queries/use-lease-draft.ts` shows lease draft pattern with `gcTime: 0`, `refetchOnMount: 'always'`
- No generic wizard draft infrastructure exists
- Mobile SecureStore available (Expo SDK 57)

**Repo scope:** Backend (draft entity, encryption, conflict detection), Frontend (auto-save, conflict UI), Mobile (SecureStore offline queue)

## Decision

### Draft Entity

```csharp
public class WizardDraft : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public WizardType WizardType { get; set; }
    public Guid? EntityId { get; set; }           // null for new entity wizards
    public int Version { get; set; }              // conflict detection per Q-A05
    public string DraftJson { get; set; }         // non-PII fields (plaintext JSONB)
    public string? EncryptedFieldsJson { get; set; }  // PII fields (encrypted)
    public DateTime LastSavedAt { get; set; }
    public DateTime ExpiresAt { get; set; }       // LastSavedAt + 30 days
}

public enum WizardType
{
    ListingCreate, ListingEdit,
    BookingCreate, BookingEdit,
    CheckIn, CheckOut,
    ComplianceSender, TouristTax,
    // ... extensible
}
```

Unique index on `(OrgId, UserId, WizardType, EntityId)` prevents multiple drafts for same wizard.

### PII Field Encryption

```csharp
public static class WizardDraftPiiFields
{
    public static readonly HashSet<string> PII_FIELD_NAMES = new()
    {
        "guestName", "guestEmail", "guestPhone", "guestFiscalCode",
        "documentNumber", "documentType", "birthDate", "birthPlace",
        "citizenship", "address", "emergencyContact"
    };
}
```

Draft save MUST split PII fields into `EncryptedFieldsJson` (encrypted with purpose `Casazen.WizardDraft.PII`) and non-PII into `DraftJson` (plaintext). Architecture test verifies no PII in `DraftJson`.

### Conflict Detection (Version Number)

Per Q-A05: version number instead of timestamp (no clock skew issues).

```csharp
public async Task<WizardDraftResult> SaveDraftAsync(SaveDraftRequest request, CancellationToken ct)
{
    var existing = await _db.WizardDrafts
        .Where(d => d.UserId == _user.Id && d.WizardType == request.Type && d.EntityId == request.EntityId)
        .SingleOrDefaultAsync(ct);

    if (existing != null && existing.Version > request.ClientVersion)
    {
        return new WizardDraftResult
        {
            Status = DraftStatus.Conflict,
            ServerDraft = MapToDto(existing),
            ServerVersion = existing.Version
        };
    }

    // Upsert with version increment
    existing ??= new WizardDraft { ... };
    existing.Version++;
    existing.DraftJson = ExtractNonPii(request.Data);
    existing.EncryptedFieldsJson = EncryptPii(request.Data);
    existing.LastSavedAt = DateTime.UtcNow;
    existing.ExpiresAt = DateTime.UtcNow.AddDays(30);
    
    await _db.SaveChangesAsync(ct);
    return new WizardDraftResult { Status = DraftStatus.Saved, ServerVersion = existing.Version };
}
```

409 response includes server draft for "Keep yours / Take theirs" UI.

### Mobile Offline Queue

Per Q-A06: SecureStore for personal data encryption at rest.

```typescript
// Mobile: expo-secure-store for PII
import * as SecureStore from 'expo-secure-store';

async function saveDraftOffline(draft: WizardDraft) {
    const piiFields = extractPii(draft.data);
    const nonPiiFields = extractNonPii(draft.data);
    
    // PII encrypted in SecureStore
    await SecureStore.setItemAsync(`draft_pii_${draft.key}`, JSON.stringify(piiFields));
    
    // Non-PII can use AsyncStorage for larger data
    await AsyncStorage.setItem(`draft_${draft.key}`, JSON.stringify(nonPiiFields));
    
    // Queue for sync when online
    await queueForSync(draft.key);
}
```

Sync on app foreground with conflict resolution via version number.

### Retention and Cleanup

- Draft retention: 30 days from `LastSavedAt` per requirements
- `GdprDataRetentionJob` (Hangfire daily) anonymizes expired drafts: `DraftJson='{}'`, `EncryptedFieldsJson=NULL`, `UserId=Guid.Empty`, keep row for audit
- Never hard delete (audit trail)

### User Isolation

Draft read endpoint `GET /api/wizards/{type}/draft` verifies:
- `WizardDraft.OrgId = tenant.OrgId` (tenant isolation)
- `WizardDraft.UserId = currentUser.Id` (user isolation)
- Different user of same org receives 404 (not 403, no information leak)

### Rejected Alternatives

- **Timestamp-based conflict detection:** Clock skew between devices causes false positives
- **AsyncStorage for PII on mobile:** Not encrypted at rest; SecureStore required for GDPR
- **Shorter retention (7 days):** Requirements allow 30 days; shorter would lose drafts for users on vacation

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-024-R1 | P0 | `WizardDraft` table (OrgId, UserId, WizardType enum, EntityId nullable, Version int, DraftJson, EncryptedFieldsJson nullable, LastSavedAt, ExpiresAt, ITenantOwned) with unique index (OrgId, UserId, WizardType, EntityId) |
| ADR-024-R2 | P0 | PII fields (name, email, phone, fiscalCode, document numbers) MUST be encrypted in `EncryptedFieldsJson` with purpose `Casazen.WizardDraft.PII`; `DraftJson` MUST NOT contain PII |
| ADR-024-R3 | P0 | POST `/api/wizards/{type}/draft` with version conflict (client version < server version) MUST return 409 `draft_conflict` with server draft payload for "Keep yours / Take theirs" UI |
| ADR-024-R4 | P0 | Draft read `GET /api/wizards/{type}/draft` MUST verify `WizardDraft.OrgId = tenant.OrgId AND WizardDraft.UserId = currentUser.Id`; different user receives 404 |
| ADR-024-R5 | P0 | Draft retention: 30 days from LastSavedAt; `GdprDataRetentionJob` MUST anonymize expired drafts (DraftJson='{}', EncryptedFieldsJson=NULL, UserId=Guid.Empty); never delete row |
| ADR-024-R6 | P0 | Frontend SaveIndicator component MUST display "Saved at HH:MM" with timestamp from server response |
| ADR-024-R7 | P0 | Wizard conflict dialog MUST offer "Keep mine" (discard server) and "Take theirs" (reload); both with confirmation per UX-11 |
| ADR-024-R8 | P0 | Draft save mutation MUST invalidate affected queries via `queryClient.invalidateQueries()` |
| ADR-024-R9 | P0 | Mobile wizard draft MUST use `expo-secure-store` for PII fields; `AsyncStorage` MUST NOT store PII |
| ADR-024-R10 | P0 | Mobile offline queue MUST persist drafts when network unavailable, sync on app foreground with conflict resolution |
| ADR-024-R11 | P0 | Mobile wizard screens (checkout, check-in) MUST implement auto-save on field blur or 30-second timer |
| ADR-024-R12 | P1 | i18n keys MUST exist: `wizard.savedAt`, `wizard.conflict.title`, `wizard.conflict.keepMine`, `wizard.conflict.takeTheirs` (IT/EN) |
| ADR-024-R13 | P1 | Mobile "To do" screen MUST show "Resume" actions for incomplete wizard drafts with step indicator |
| ADR-024-R14 | P1 | Proof test `WizardDraft_PersonalData_EncryptedAtRest`: verify PII not in plaintext `DraftJson` column |

## Consequences

**Follow-on work:**
- Backend: `WizardDraft` entity and migration, encryption service integration, draft endpoints, cleanup job extension
- Frontend: SaveIndicator component, conflict dialog, auto-save hook for all wizards, query invalidation
- Mobile: SecureStore integration, offline queue, sync on foreground, "Resume" in "To do" screen
- Tests: Encryption proof test, conflict detection integration test

**Platform difference:** Mobile wizards use native full-screen modal navigation (not web's dimmed-background layer) per mobile platform conventions. This is acceptable per product rule "mobile may differ where platform conventions dictate."

**Risks:**
- 30-day retention may hold PII longer than necessary (GDPR art. 5.1.e); acceptable if documented in privacy policy
- Cross-device resume requires backend draft as source of truth (SecureStore is device-local)
- Encryption key rotation: old drafts unreadable; "re-save to continue" UX deferred to P1

**CI cost:** +10s for encryption proof test
