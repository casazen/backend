# Design — Issue #1 Alloggiati Web Integration (MVP Phase 1)

> **Stage 02 — Design** · Issue #1 · Art. 109 TULPS compliance
> **Branch for Stage 03:** `feature/1-alloggiati-web`
> **MVP scope:** Guest self check-in + document upload + owner status dashboard + 24h alerts + manual resend. Real Alloggiati HTTP behind feature flag; regional portals deferred.

**Grounding (verified):** `Guest` already has Alloggiati fields; `AlloggiatiWebReport` + `AlloggiatiWebService` (stub) + `AlloggiatiWebReportJob` exist. `POST /api/bookings/{id}/check-in` enqueues report job. `GET /api/bookings/{id}/alloggiati-status` exists but **lacks `[Authorize]`** — must fix. No guest-facing check-in UI or compliance dashboard.

---

## API Contract

JSON camelCase; enums as strings.

### A. New endpoints

| # | Method | Path | Request | Response | Auth |
|---|---|---|---|---|---|
| 1 | `GET` | `/api/checkin/{token}` | Path: `token: Guid` | `200 CheckInContextDto` `{ bookingId, guestId, propertyName, checkInDate, checkOutDate, guest, dataComplete }` | **`[AllowAnonymous]`** — token in URL is the secret (sent via email link) |
| 2 | `POST` | `/api/checkin/{token}/guest-data` | Body: `SubmitGuestCheckInRequest` `{ dateOfBirth, placeOfBirth, nationality, gender, documentType, documentNumber, documentExpiryDate, documentIssuingCountry, address, city, postalCode, country, consentAccepted }` | `200 { dataComplete: bool }` | **`[AllowAnonymous]`** — token auth |
| 3 | `POST` | `/api/checkin/{token}/document` | `multipart/form-data` field `file` (image/pdf, max 5MB) | `200 { documentScanUrl }` | **`[AllowAnonymous]`** — token auth |
| 4 | `GET` | `/api/alloggiati/summary` | Query: `propertyId?: Guid` | `200 AlloggiatiSummaryDto[]` | **`[Authorize(Policy="RequireContext:short-rent:booking.read")]`** |
| 5 | `POST` | `/api/alloggiati/{bookingId}/send` | — | `200 AlloggiatiStatusDto` | **`[Authorize(Policy="RequireContext:short-rent:booking.write")]`** — manual/fallback resend |
| 6 | `GET` | `/api/alloggiati/{bookingId}/status` | — | `200 AlloggiatiStatusDto` | **`[Authorize(Policy="RequireContext:short-rent:booking.read")]`** |

### B. Changed endpoints

| Method | Path | Change | Auth |
|---|---|---|---|
| `GET` | `/api/bookings/{id}/alloggiati-status` | **Deprecate** — proxy to `/api/alloggiati/{bookingId}/status` or add `[Authorize]` | `[Authorize(Policy="RequireContext:short-rent:booking.read")]` |
| `POST` | `/api/bookings` (confirm flow) | Generate `CheckInToken` (Guid) on `Confirmed` bookings | unchanged |

### C. DTOs

**AlloggiatiStatusDto:**
```json
{ "bookingId": "uuid", "status": "Pending|Submitted|Confirmed|Failed", "confirmationNumber": "string|null", "errorMessage": "string|null", "reportedAt": "datetime|null", "hoursUntilDeadline": 0, "isOverdue": false, "dataComplete": true }
```

**AlloggiatiSummaryDto:**
```json
{ "bookingId": "uuid", "guestName": "string", "propertyName": "string", "checkInDate": "date", "status": "...", "dataComplete": false, "isOverdue": false, "hoursUntilDeadline": 12 }
```

### D. Background jobs

| Job | Schedule | Action |
|---|---|---|
| `AlloggiatiWebReportJob.ReportGuestAsync` | On check-in (existing) | Submit when data complete |
| `AlloggiatiDeadlineAlertJob` | Hourly | Email owner when check-in < 24h and data incomplete or report failed |

### E. Feature flag

`Alloggiati:Enabled` (bool, default `false` in prod until Questura creds). When `false`, service marks `Submitted` with note "simulated"; when `true`, HTTP to Alloggiati API.

---

## Frontend Flow

Repo `casazen/frontend`. New feature slice `src/features/alloggiati/` + public check-in at `src/features/checkin/`.

### Route changes

| Route | Guard | Purpose |
|---|---|---|
| `/checkin/:token` | **Public** (no `<ProtectedRoute>`) | Guest self check-in form (AC8) |
| `/app/short-rent/alloggiati` | **`<ProtectedRoute>`** + context | Owner compliance dashboard (AC9) |
| `/app/short-rent/bookings/:id` | **`<ProtectedRoute>`** (modify) | Add Alloggiati status badge + manual resend (AC10) |

### Components

| File | Responsibility |
|---|---|
| `features/checkin/checkin-page.tsx` | Token-based form, Italian copy, GDPR consent checkbox |
| `features/checkin/components/guest-data-form.tsx` | Alloggiati fields validation |
| `features/checkin/components/document-upload.tsx` | File picker + upload progress |
| `features/alloggiati/alloggiati-dashboard-page.tsx` | Table of bookings with status/deadline |
| `features/alloggiati/components/alloggiati-status-badge.tsx` | Status chip (Pending/Submitted/Confirmed/Failed/Overdue) |
| `features/alloggiati/components/resend-button.tsx` | Manual send when Failed |
| `api/checkin.api.ts` | Public check-in API (no auth header) |
| `api/alloggiati.api.ts` | Authenticated dashboard API |

Nav: add "Alloggiati" item under short-rent manifest.

---

## Security Notes

| Threat | Mitigation |
|---|---|
| Token guessing on `/api/checkin/{token}` | 128-bit Guid token; rate-limit 10 req/min per IP |
| PII in document scans | Store under `uploads/guest-documents/{orgId}/{guestId}/` with random filename; serve only via authenticated owner endpoint or signed URL |
| Questura credentials exposure | `PropertyQuesturaCredentials.PasswordEncrypted` — AES via `IDataProtectionProvider`; never return in API |
| Missing auth on status endpoint | Fix `alloggiati-status` with `[Authorize]` |
| GDPR | Consent required on check-in form; `ConsentDate` + `ConsentVersion` set on guest |

OTA keys: not affected.

---

## Migration Plan

**Migration:** `AddAlloggiatiCheckInMvp`

| Table/Column | Change |
|---|---|
| `Bookings.CheckInToken` | `Guid?` unique index, populated on confirm |
| `Guests.DocumentScanUrl` | `string?` max 500 |
| `PropertyQuesturaCredentials` (new) | `Id`, `PropertyId` FK, `Username`, `PasswordEncrypted`, `WsKey`, `CreatedAt` |
| `AlloggiatiWebReports.ManuallyCompleted` | `bool` default false |

---

## GDPR Scope

**In scope:** Guest PII — name, DOB, birthplace, nationality, address, document type/number/expiry, document scan image.

| Requirement | Implementation |
|---|---|
| Lawful basis | Art. 6(1)(c) legal obligation (TULPS) + consent checkbox for processing |
| Data minimization | Collect only Alloggiati-required fields |
| Retention | Existing `Guest.DataRetentionUntil` (7 years default) |
| Right to erasure | Block erasure if active booking; anonymize after retention |
| Document scans | Encrypted at rest (file system ACL + optional AES); deleted with guest erasure job |

---

## Open Questions

| # | Question | Resolution |
|---|---|---|
| 1 | Alloggiati API sandbox? | No public sandbox — use `Alloggiati:Enabled=false` stub until Questura creds per property |
| 2 | Regional portals in MVP? | **Deferred** to follow-up issue |
| 3 | OCR document scan? | **Deferred** — manual upload only in MVP |

---

## Acceptance Criteria Test Map

| AC | Test |
|---|---|
| AC1 | Integration: `POST /api/checkin/{token}/guest-data` returns 400 when DOB missing |
| AC2 | Integration: document upload returns URL |
| AC3 | Integration: `GET /api/alloggiati/{id}/status` returns status DTO |
| AC4 | Integration: manual send creates/updates report |
| AC5 | Unit: check-in enqueues `AlloggiatiWebReportJob` |
| AC6 | Unit: `AlloggiatiDeadlineAlertJob` flags incomplete < 24h |
| AC7 | Integration: `GET /api/alloggiati/summary` lists overdue |
| AC8 | E2E: `/checkin/:token` form submission |
| AC9 | E2E: dashboard shows status badge |
| AC10 | E2E: resend button on failed status |
| AC11 | Unit: consent fields set on guest-data submit |
| AC12 | Grep: no plaintext Questura passwords in logs/code |
