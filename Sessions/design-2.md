# Design — Issue #2 CIN Management (MVP Phase 1)

> **Stage 02 — Design** · Issue #2 · D.L. 145/2023 CIN compliance
> **Branch for Stage 03:** `feature/2-cin-management`
> **MVP scope:** Owner CIN form + dedicated API + compliance dashboard + deadline banner + 7-day alert job. OTA sync and BDSR verification deferred.

**Grounding (verified):** `Property.CinCode` + `[CinCode]` validation exist. Derived `CinStatus` in `PropertyService.ResolveCinStatus`. Admin `GET /api/admin/cin-compliance` exists. `PropertyCinBadge` on detail/search. No owner form, no owner compliance endpoint, no deadline alerts.

---

## API Contract

JSON camelCase; `cinStatus` as string: `valid` | `missing` | `invalid`.

### A. New endpoints

| # | Method | Path | Request | Response | Auth |
|---|---|---|---|---|---|
| 1 | `GET` | `/api/properties/cin-compliance` | Query: `cinStatus?: string`, `page?: int`, `pageSize?: int` | `200 CinComplianceResponse` | **`[Authorize(Policy="RequireContext:short-rent:property.read")]`** — org-scoped to caller |
| 2 | `PUT` | `/api/properties/{id}/cin` | Body: `UpdatePropertyCinRequest` `{ cinCode: string \| null }` | `204 No Content` or `400` validation | **`[Authorize(Policy="RequireContext:short-rent:property.write")]`** — owner only |

### B. DTOs

**UpdatePropertyCinRequest:**
```json
{ "cinCode": "IT-12345-0123456789" }
```

**CinComplianceItemDto:**
```json
{ "propertyId": "uuid", "propertyName": "string", "cinCode": "string|null", "cinStatus": "valid|missing|invalid", "city": "string" }
```

**CinComplianceResponse:**
```json
{
  "items": [ /* CinComplianceItemDto */ ],
  "totalCount": 0,
  "summary": { "valid": 0, "missing": 0, "invalid": 0, "daysUntilDeadline": 264, "deadline": "2026-03-01" },
  "hasNonCompliant": true
}
```

### C. Validation rules

| Rule | Implementation |
|---|---|
| Format | `^IT-\d{5}-\d{10}$` via `CinCodeAttribute` |
| Uniqueness | Reject if another property (same platform) has same non-null `CinCode` |
| Optional | `null` or empty clears CIN (status → `missing`) |

### D. Background jobs

| Job | Schedule | Action |
|---|---|---|
| `CinDeadlineAlertJob` | Daily `0 8 * * *` UTC | If `daysUntilDeadline <= 7`, email owners with missing/invalid CIN properties |

Deadline constant: `2026-03-01` (Italian regulatory date for existing operators).

---

## Frontend Flow

Repo `casazen/frontend`. New feature slice `src/features/cin/`.

### Route changes

| Route | Guard | Purpose |
|---|---|---|
| `/app/short-rent/cin` | **`<ProtectedRoute>`** + `short-rent` context | Owner CIN compliance dashboard (AC7) |
| `/app/short-rent/properties` | **`<ProtectedRoute>`** (modify) | Add `CinDeadlineBanner` when non-compliant (AC5) |
| `/app/short-rent/properties/:id/edit` | **`<ProtectedRoute>`** (modify) | CIN field in property form (AC6) |

### Components

| File | Responsibility |
|---|---|
| `features/cin/cin-compliance-page.tsx` | Table of properties + summary cards + BDSR link |
| `features/cin/components/cin-deadline-banner.tsx` | Red/amber banner with countdown |
| `features/cin/components/cin-summary-cards.tsx` | valid/missing/invalid counts |
| `features/properties/components/property-form.tsx` | Add `cinCode` field with Italian hint |
| `api/cin.api.ts` | `getCinCompliance`, `updatePropertyCin` |
| `queries/use-cin.ts` | React Query hooks |

Nav: add "CIN" item under short-rent manifest (`navKey: nav.cin`).

---

## Security Notes

| Threat | Mitigation |
|---|---|
| Cross-org CIN data leak | Owner endpoint filters by caller's org via EF global filter (not `IgnoreQueryFilters`) |
| Unauthorized CIN update | `PropertyAuthorizationService.CanAccess` + `property.write` policy |
| CIN enumeration | No public endpoint; auth required |
| PII in CIN | CIN is property identifier, not guest PII — low sensitivity |

OTA keys: not affected in MVP.

---

## Migration Plan

**N/A — no schema changes.** MVP uses existing `Property.CinCode` (`varchar(25)`, nullable).

---

## GDPR Scope

**N/A** — CIN is a property regulatory identifier assigned by BDSR; no Guest personal data involved.

---

## Open Questions

| # | Question | Resolution |
|---|---|---|
| 1 | Store workflow enum (`Pending/Approved`)? | Deferred — derived `CinStatus` sufficient for MVP |
| 2 | Block listing publish without CIN? | Deferred — configurable gate in follow-up |
| 3 | OTA adapter field names? | Deferred — per-platform mapping in follow-up issue |
