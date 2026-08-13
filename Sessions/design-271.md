# Design Spec — Issue #271: Self-serve onboarding & activation PLG (US-006)

**Issue**: [#271](https://github.com/casazen/backend/issues/271)  
**Feature**: feat(MVP): self-serve onboarding PLG — Org provision, consents, activation checklist  
**Spec**: `Sessions/specs/spec-onboarding-plg.md`  
**ADR / related**: `spec-role-onboarding`, `spec-tenant-boundary`, `spec-branded-booking-site`, `spec-direct-checkout`  
**Status**: complete  
**Date**: 2026-08-13  
**Branch**: `feature/271-onboarding-plg`  
**Repos**: BE partial (ConsentRecord, LegalController, OnboardingController, Users onboarding consents) already in tree; FE partial (consents step, legal.api). Stage 03 closes remaining AC gaps + L2/L3 titled tests.

---

## Summary

Extend first-run onboarding into a PLG funnel: provision an `Org` (idempotent), capture versioned legal consents (ToS / Privacy / DPA / Subprocessors + optional marketing), expose anonymous legal documents, and drive activation via a checklist derived from real Org state (property, site published, first confirmed booking).

---

## Data model

### Entity — `ConsentRecord` (migration `20260611213956_AddConsentRecords`)

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` PK | |
| `UserId` | `string` MaxLength 255 | Auth0 `sub` |
| `OrgId` | `Guid` FK → Org | Tenant boundary |
| `Type` | `ConsentType` enum | `Tos` \| `Privacy` \| `Dpa` \| `Subprocessors` \| `Marketing` |
| `Version` | `string` MaxLength 100 | Document version at acceptance |
| `IpAddress` | `string?` MaxLength 100 | From `X-Forwarded-For` / remote IP |
| `RecordedAt` | `DateTime` UTC | Append-only; version bump → new row |

**Indexes:** `(UserId)`, `(OrgId)`, `(Type)` as configured in `AppDbContext`.

---

## API Contract

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| **Extend** | POST | `/api/users/onboarding` | `[Authorize]` | `OnboardingRequestDto` | 200 `OnboardingResponseDto` / 400 / 401 / 500 |
| **Extend** | PUT | `/api/users/onboarding` | `[Authorize]` | `OnboardingRequestDto` | 200 `OnboardingResponseDto` / 400 / 401 |
| **New** | GET | `/api/onboarding/status` | `[Authorize]` | — | 200 `OnboardingStatusDto` / 401 |
| **New** | GET | `/api/legal/subprocessors` | `[AllowAnonymous]` — public legal transparency | — | 200 `SubprocessorsDocumentDto` |
| **New** | GET | `/api/legal/dpa` | `[AllowAnonymous]` — public legal doc metadata | — | 200 `LegalDocumentDto` |
| **New** | GET | `/api/legal/tos` | `[AllowAnonymous]` — public legal doc metadata | — | 200 `LegalDocumentDto` |
| **New** | GET | `/api/legal/privacy` | `[AllowAnonymous]` — public legal doc metadata | — | 200 `LegalDocumentDto` |

### POST `/api/users/onboarding`

**Auth**: `[Authorize]` — valid Auth0 JWT; caller `sub` is the subject user.

**Request `OnboardingRequestDto`:**

| Field | Type | Required | Notes |
|---|---|---|---|
| `rentalType` | `string` | yes | `ShortTerm` \| `LongTerm` \| `Both` |
| `planTier` | `string?` | no | Defaults to `Starter` |
| `consents` | `OnboardingConsentsDto` | **yes on POST** | Required block; missing/incomplete → 400 |

**`OnboardingConsentsDto`:**

| Field | Type | Required |
|---|---|---|
| `tosAccepted` | `bool` | must be `true` |
| `tosVersion` | `string` | must match current ToS version |
| `privacyAccepted` | `bool` | must be `true` |
| `privacyVersion` | `string` | must match current Privacy version |
| `dpaAccepted` | `bool` | must be `true` |
| `dpaVersion` | `string` | must match current DPA version |
| `subprocessorsAcknowledged` | `bool` | must be `true` |
| `subprocessorsVersion` | `string` | must match current subprocessors version |
| `marketingOptIn` | `bool?` | optional |

**Responses:**
- `200` `OnboardingResponseDto`: `{ rolesAssigned[], rentalType, orgId, orgProvisioned, consentsRecorded }`
- `400`: incomplete consents, stale document version (`staleDocuments`), unknown `rentalType` / `planTier`
- `401`: missing/invalid JWT
- `500`: Org provisioning failed

**Semantics (AC1–AC3):** Idempotent Org provision — existing `OrgId` is kept; plan not overwritten on re-run. Consents append-only into `ConsentRecord`. Client IP recorded when available.

### PUT `/api/users/onboarding`

**Auth**: `[Authorize]` — same JWT subject. Admins bypass onboarding guard on FE (`spec-role-onboarding` AC9); API still authorizes the caller.

**Request**: same DTO; `consents` **not required** (`requireConsents: false`). Existing Org + consent history retained; only role/`rentalType` (and optional plan on first provision path) applied (AC7).

**Responses:** `200` / `400` / `401` as above.

### GET `/api/onboarding/status`

**Auth**: `[Authorize]` — status scoped to caller's Org via `sub` → User → OrgId (no cross-tenant IDOR).

**Response `200` `OnboardingStatusDto`:**

| Field | Type | Derivation (AC5–AC6) |
|---|---|---|
| `roleChosen` | `bool` | User has rental type / roles |
| `orgProvisioned` | `bool` | `User.OrgId` set |
| `consentsAccepted` | `bool` | Required consent types present at current versions |
| `propertyCreated` | `bool` | Org has ≥1 `Property` |
| `sitePublished` | `bool` | Org branded site active (`Org.IsActive` + ≥1 active property) |
| `firstBookingTaken` | `bool` | Org has ≥1 `Confirmed` direct `Booking` |
| `activated` | `bool` | All activation milestones true |
| `publicBookingUrl` | `string?` | Share URL when available |

### GET `/api/legal/*` (anonymous)

**Auth**: `[AllowAnonymous]` — justified as public legal documents required for GDPR transparency and pre-auth onboarding (AC4).

**`SubprocessorsDocumentDto`:** `{ version, effectiveAt, items: [{ name, purpose, region, website? }] }` — minimum items: Supabase (EU), Auth0, Stripe, SendGrid.

**`LegalDocumentDto`:** `{ version, effectiveAt, title, summary, documentUrl? }` for DPA / ToS / Privacy.

---

## Frontend Flow

> Web app (`casazen/frontend`). Authenticated app shell uses `<ProtectedRoute>`; `/onboarding` sits **outside** `OnboardingGuard` (inside auth shell) to avoid redirect loops (AC12). Public legal page is anonymous.

### New / Modified Routes

| Path | Component | Auth | Notes |
|---|---|---|---|
| `/onboarding` | `OnboardingPage` | `<ProtectedRoute>` (Auth0 session; demo mode bypass) | Wizard: role → consents → (existing plan step if present); **outside** `OnboardingGuard` |
| `/app/short-rent` (dashboard home) | dashboard + `ActivationChecklist` | `<ProtectedRoute>` → `OnboardingGuard` | Mount checklist widget (AC10) |
| `/legal/subprocessors` | `SubprocessorsPage` | **public** (no ProtectedRoute) | Linked from consents step + footer (AC11) |

### Component Plan

| Component | Status | Location | Responsibility |
|---|---|---|---|
| `OnboardingPage` | modified | `src/features/onboarding/onboarding-page.tsx` | Wizard orchestration; POST onboarding; token refresh (AC8–AC9) |
| `ConsentsStep` | exists / verify | `src/features/onboarding/components/consents-step.tsx` | Four required checkboxes + marketing opt-in; Continua disabled until all required (AC8) |
| `ActivationChecklist` | **new** | `src/features/onboarding/components/activation-checklist.tsx` | Renders status milestones + deep links; hide when `activated` (AC10) |
| `SubprocessorsPage` | **new** | `src/features/legal/subprocessors-page.tsx` | Public Italian “Responsabili del trattamento” view (AC11) |
| `OnboardingGuard` | preserve | `src/components/auth/onboarding-guard.tsx` | Ordering after ProtectedRoute; demo mode regression (AC12) |

### State & API

| Data | Hook | API module | Notes |
|---|---|---|---|
| Complete onboarding | `useCompleteOnboarding` / users mutation | `src/api/users.api.ts` | `POST/PUT` with `consents`; then `getAccessTokenSilently({ ignoreCache: true })` |
| Activation status | `useOnboardingStatus` | `src/api` onboarding or users | `GET /api/onboarding/status` |
| Legal docs | `useLegalDocuments` | `src/api/legal.api.ts` | Anonymous GET subprocessors/dpa/tos/privacy |
| Types | — | `src/types/onboarding.types.ts` | `OnboardingConsentsPayload`, `OnboardingStatus` |

**Deep links (AC10):** “Crea proprietà” → property create; “Pubblica il sito” → branded-site settings; “Prima prenotazione” → share `publicBookingUrl`.

**UX:** End-user strings Italian; Continua disabled until required consents; error states show human Italian messages (see spec `## UX / UI Quality`).

---

## Security Notes

**Auth gates:**
- `POST/PUT /api/users/onboarding`, `GET /api/onboarding/status` → `[Authorize]`
- `GET /api/legal/*` → `[AllowAnonymous]` with public-legal justification (transparency / pre-consent)

**IDOR risk:** Activation status and consent writes keyed by JWT `sub` → caller’s Org only; no OrgId in path for status. Consent rows always store caller UserId + provisioned OrgId.

**Secrets:** N/A — no OTA/Stripe secrets introduced. Legal document content served from config/service constants (`ILegalDocumentService`), not client-supplied HTML.

**Stripe:** N/A for this feature surface.

**PII exposure risk:** Consent rows store Auth0 user id + IP (account metadata). Do not log full consent payloads with unnecessary PII. No Guest PII in this flow. Error responses return generic validation messages + optional `staleDocuments` codes (no IP echo).

**Threat summary (STRIDE):**
- **Spoofing:** mitigated by Auth0 JWT on mutating/status endpoints.
- **Tampering:** consent versions server-validated against current legal versions; stale → 400.
- **Information disclosure:** anonymous legal endpoints expose only public document metadata; status endpoint requires auth.

---

## Migration Plan

| Migration | Entity / change |
|---|---|
| `20260611213956_AddConsentRecords` | `ConsentRecords` table + indexes (`UserId`, `OrgId`, `Type`) |

Register `DbSet<ConsentRecord>` in `AppDbContext` (already present). Stage 03 verifies migration applied on staging before L3; no additional schema expected unless gap analysis finds missing indexes.

---

## GDPR Scope

**In scope (operator / account data, not Guest):**
- Consent evidence: type, version, `RecordedAt`, IP — GDPR Art. 7 demonstrability.
- DPA acceptance establishes Org as controller / CasaZen as processor (`spec-tenant-boundary`).
- Subprocessor acknowledgement (Supabase EU, Auth0, Stripe, SendGrid) with versioned public list.
- Marketing opt-in optional; stored as `ConsentType.Marketing` when true.

**Out of scope:** Guest name/DOB/document — **N/A — no Guest personal data in this flow.**  
Data minimization: role, org, consent metadata only.

---

## AC Test Map

| AC | REQ-ID | L1 (unit/integration) | L2 (demo Playwright) | L3 (real API) | Seed / fixture |
|---|---|---|---|---|---|
| AC1 | SPEC:onboarding-plg:AC1 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | InMemory WebApplicationFactory auth client |
| AC2 | SPEC:onboarding-plg:AC2 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | Missing/stale consents → 400 |
| AC3 | SPEC:onboarding-plg:AC3 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | Assert ConsentRecords rows |
| AC4 | SPEC:onboarding-plg:AC4 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | Anonymous client |
| AC5 | SPEC:onboarding-plg:AC5 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | Authenticated status DTO |
| AC6 | SPEC:onboarding-plg:AC6 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | Seed property/booking state |
| AC7 | SPEC:onboarding-plg:AC7 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | N/A — non UI | N/A — non UI | PUT without consents |
| AC8 | SPEC:onboarding-plg:AC8 | N/A — non UI | `e2e/onboarding-plg.spec.ts` | `e2e/l3/onboarding-plg-l3.spec.ts` | Demo profile `onboarding` |
| AC9 | SPEC:onboarding-plg:AC9 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | `e2e/onboarding-plg.spec.ts` | `e2e/l3/onboarding-plg-l3.spec.ts` | POST + token refresh path |
| AC10 | SPEC:onboarding-plg:AC10 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | `e2e/onboarding-plg.spec.ts` | `e2e/l3/onboarding-plg-l3.spec.ts` | Dashboard mount + deep links |
| AC11 | SPEC:onboarding-plg:AC11 | `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs` | `e2e/onboarding-plg.spec.ts` | `e2e/l3/onboarding-plg-l3.spec.ts` | Public `/legal/subprocessors` |
| AC12 | SPEC:onboarding-plg:AC12 | N/A — non UI | `e2e/onboarding-plg.spec.ts` | N/A — non UI | `VITE_DEMO_MODE` / demo profile |

> L2/L3 path tokens are Stage 02 scaffolds under this repo’s `e2e/` (path-exists for G9). Canonical FE copies live in `casazen/frontend` (`e2e/onboarding-plg.spec.ts`, `e2e/l3/onboarding-plg-l3.spec.ts`) and must be mirrored in Stage 03 FE PR. Each UI AC uses a titled `test('ACn: …')`.

---

## Open Questions

(none — all resolved)
