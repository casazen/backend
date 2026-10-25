# ADR-021: Guest CRM Matching and Profile Structure

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-23

## Context

Agencies need to recognize returning guests across bookings and aggregate their history (stays, spend, marketing consent). Guest matching requires a composite key approach to handle name variations.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `Guest` entity exists per booking
- No CRM profile entity exists
- GDPR export/deletion pattern exists per ADR-009

**Repo scope:** Backend (CRM profile, matching, aggregation), Frontend (CRM search, profile display)

## Decision

### Composite Key Hash

Per Q-A50 answer, use SHA-256 hash of normalized composite key:

```csharp
var normalized = $"{email.Trim().ToLowerInvariant()}|" +
                 $"{name.Trim().ToLowerInvariant()}|" +
                 $"{surname.Trim().ToLowerInvariant()}|" +
                 $"{dob:yyyyMMdd}|" +
                 $"{fiscalCode?.Trim().ToUpperInvariant() ?? ""}";
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).ToHexString().ToLowerInvariant();
```

Normalization:
- Trim whitespace
- Lowercase (except fiscal code which is uppercase)
- Remove accents (optional, for edge cases)

### GuestCrmProfile Entity

```csharp
public class GuestCrmProfile : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string CompositeKeyHash { get; set; }  // SHA-256, varchar(64)
    public string ConfirmedMarketingEmail { get; set; }
    public bool MarketingConsentEmail { get; set; }
    public bool MarketingConsentWhatsApp { get; set; }
    public string Language { get; set; }
    public string Country { get; set; }
    public int TotalStays { get; set; }
    public decimal TotalSpend { get; set; }
    public string PropertyIdsJson { get; set; }  // JSON array
    public string TagsJson { get; set; }  // JSON array
    public DateTime FirstStayAt { get; set; }
    public DateTime LastStayAt { get; set; }
}
```

### One-to-Many Relationship

Per Q-A51 answer, Guest links to GuestCrmProfile:

```csharp
// Add to Guest entity
public Guid? GuestCrmProfileId { get; set; }
public GuestCrmProfile CrmProfile { get; set; }
```

One profile can have many Guest records (multiple bookings).

### Lazy Matching

Per Q-A52 answer:
- Existing guests have null GuestCrmProfileId
- Match on new booking creation only
- Admin can trigger rebuild batch job if desired

### Matching Logic

On booking creation:
1. Compute composite key hash
2. Query `WHERE CompositeKeyHash = @hash AND OrgId = @orgId`
3. If found: link Guest to existing profile, update aggregates
4. If not found: create new profile, link Guest

### Collision Handling

SHA-256 collision probability is negligible (2^-256), but if detected:
- If hash exists but normalized fields differ (true collision)
- Create new profile with hash + guid suffix
- Log warning for manual review

### Tag Limit

Per Q-A53 answer, maximum 50 tags per guest:
- Create beyond limit returns 422 `tag_limit_exceeded`

### Excluded Data

Per AC-NF07-5 and AC-NF05-10, CRM profile MUST exclude:
- Alloggiati submission data (document type, number, issue place)
- Raw document images
- Biometric data

### GDPR Integration

Per ADR-009-R11 pattern:
- **Export (art. 15):** Includes CRM profile (confirmed contacts, consent by channel, tags, stays, spend)
- **Deletion (art. 17):** Delete CRM profile, set Guest.GuestCrmProfileId NULL
- **Suppression list:** Hash kept in suppression list per NF-05

### HostScope Filtering

GET /api/crm/guests MUST filter by caller HostScope:
- Only profiles with stays at properties in perimeter

### Rejected Alternatives

- **Email-only matching:** Name variations, shared emails would mislink
- **Eager matching on migration:** Too slow, lazy matching sufficient
- **No collision handling:** Negligible probability but good practice

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-021-R1 | P0 | GuestCrmProfile table (OrgId, CompositeKeyHash varchar(64), ConfirmedMarketingEmail, MarketingConsentEmail, MarketingConsentWhatsApp, Language, Country, TotalStays, TotalSpend, PropertyIds JSON, Tags JSON, FirstStayAt, LastStayAt, ITenantOwned) |
| ADR-021-R2 | P0 | CompositeKeyHash MUST be SHA-256 of normalized `email.trim().toLowerCase() + "|" + name.trim().toLowerCase() + "|" + surname.trim().toLowerCase() + "|" + dob.yyyyMMdd + "|" + fiscalCode.trim().toUpperInvariant()` |
| ADR-021-R3 | P0 | Guest matching on booking MUST query `WHERE CompositeKeyHash = @hash AND OrgId = @orgId`, link or create profile |
| ADR-021-R4 | P0 | Collision handling: if hash exists but normalized fields differ (collision), create new profile with hash + guid suffix |
| ADR-021-R5 | P0 | GuestCrmProfile MUST exclude Alloggiati submission data (document type, number, issue place) per compliance source veto |
| ADR-021-R6 | P0 | GDPR export MUST include CRM profile per GdprDataExportService pattern, category "Marketing" |
| ADR-021-R7 | P0 | GDPR deletion MUST delete CRM profile and set Guest.GuestCrmProfileId NULL per retention rules |
| ADR-021-R8 | P0 | GET /api/crm/guests MUST filter by caller HostScope (only properties in perimeter) |
| ADR-021-R9 | P0 | Tag limit: maximum 50 tags per guest, create beyond returns 422 `tag_limit_exceeded` |
| ADR-021-R10 | P1 | Profile rebuild from Guest history: admin-triggered batch job "Rebuild all profiles from booking history" |

## Consequences

**Follow-on work:**
- Backend: GuestCrmProfile entity, guest matching service with SHA256 composite key, consent + spend + stays aggregation, tag system, search + filter with HostScope, GDPR integration
- Frontend: Guest CRM search + filter UI, profile detail display, tags UI, GDPR extension
- Tests: Guest matching proof test `GuestMatching_CompositeKey_CorrectHash`, GDPR proof test

**Risks:**
- Hash collision (extremely low probability, suffix strategy handles)
- Normalization edge cases (accented chars, Cyrillic)
- Lazy matching: existing guests not profiled until next booking

**CI cost:** +5s for matching and GDPR proof tests
