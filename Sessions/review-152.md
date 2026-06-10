# Stage 04 Review — Issue #152
# Property detail — aggregate endpoint, documents, RBAC hardening, CIN compliance

**Date**: 2026-06-05  
**Coordinator**: Stage 04 Review  
**Design spec**: `Sessions/design-152.md`  
**Backend PR**: https://github.com/casazen/backend/pull/193 (`feature/152-property-detail` → `develop`)  
**Frontend PR**: https://github.com/casazen/frontend/pull/96 (`feature/152-property-detail` → `develop`)

---

## Council Summary

| Reviewer | Verdict | Notes |
|---|---|---|
| **code-reviewer** | Approve with minor notes | Contract aligned with design; tests pass (386/386); section components well-factored |
| **security-auditor** | Approve with conditions | API IDOR mitigated; OTA secrets excluded; document static-file access and 403 UX gaps flagged 🟡 |

---

## Gate Status (G1–G10)

| Gate | Check | Status | Evidence |
|---|---|---|---|
| **G1** | PR(s) mergeable | ✅ PASS | Backend `mergeable: MERGEABLE`, `mergeStateStatus: CLEAN`; Frontend `mergeable: MERGEABLE`, `mergeStateStatus: CLEAN` |
| **G2** | No critical (🔴) findings | ✅ PASS | 0 open 🔴 findings (see Findings below) |
| **G3** | High (🟡) findings addressed or deferred | ✅ PASS | 3 🟡 findings documented with remediation / deferral rationale |
| **G4** | Cross-repo FE/BE contract consistency | ✅ PASS | `PropertyDetailDto`, nested DTOs, and API paths match design spec |
| **G5** | No IDOR on property endpoints | ✅ PASS | All `{id}` actions use `CanAccess`; non-owner tests return `Forbid`; doc delete verifies `document.PropertyId == id` |
| **G6** | No raw SQL | ✅ PASS | `grep FromSqlRaw\|ExecuteSqlRaw` in `Casazen.Infrastructure` → 0 matches |
| **G7** | PII not exposed in logs/errors | ✅ PASS | `BookingsSummary` aggregates only; no `DocumentNumber`/`DateOfBirth`/`Nationality` in property flow |
| **G8** | Stripe signature verified | ✅ PASS (N/A) | `StripeWebhookHandler.cs` not modified in either PR |
| **G9** | GDPR guest fields on creation | ✅ PASS (N/A) | No new guest creation flows in scope |
| **G10** | Frontend auth routes | ✅ PASS (N/A) | No new routes; detail page uses existing manifest + inherited `ProtectedRoute` / `ContextRouteGuard` |

**Harness exit**: ✅ All gates pass — eligible for Stage 05 handoff.

---

## Findings by Severity

### 🔴 Critical — 0

No blocking issues. API authorization, OTA secret exclusion, and RBAC hardening meet design requirements.

---

### 🟡 High — 3

#### H1 — Compliance documents served via unauthenticated static files

**Area**: Backend storage + Frontend download link  
**Risk**: Information disclosure (OWASP A01) — CIN certificates and insurance PDFs may be fetched without JWT if `downloadUrl` is known.

**Evidence**:
- `Program.cs` registers `UseStaticFiles()` before authentication middleware.
- `LocalImageStorageService.UploadDocumentAsync` returns URLs under `/uploads/properties/{propertyId}/documents/{guid}.ext`.
- Frontend `property-documents-section.tsx` uses `<a href={doc.downloadUrl}>` (no Bearer token).

**Design gap**: Spec states *"Download URLs are authenticated-context only (same JWT session)"* — current implementation does not enforce this.

**Remediation** (follow-up issue recommended):
- Serve documents through an authenticated controller action (`GET /api/properties/{id}/documents/{docId}/download`) with `CanAccess` check, or
- Protect `/uploads/properties/*/documents/*` with authorization middleware.

**Deferral**: Pre-existing static-file pattern for property images; UUID filenames limit enumeration. Accepted for #152 release with follow-up tracked separately.

---

#### H2 — Frontend 403 handling not implemented per design

**Area**: `property-detail-page.tsx`  
**Risk**: UX / least-privilege visibility — unauthorized users see generic "Proprietà non trovata" instead of explicit deny.

**Evidence**: Design spec error table requires toast *"Accesso negato"* + redirect to `/app/short-rent/properties`. Implementation treats all `isError` as 404 UI with no status-code discrimination.

**Remediation**: In `usePropertyDetail` error handler or page effect, detect HTTP 403 (axios `error.response?.status === 403`), show toast, `navigate('/app/short-rent/properties')`.

**Deferral**: API correctly returns 403; no data leak. UX gap only — non-blocking for merge.

---

#### H3 — Privileged audit logging tests incomplete

**Area**: `PropertiesControllerTests.cs`  
**Risk**: Repudiation — audit contract verified only for `GetDetail`, not write paths.

**Evidence**: Tests exist for `GetDetail_AsAdminCrossOwner_LogsPrivilegedAccess` and `GetDetail_AsOwner_DoesNotLogPrivilegedAccess`. No parallel tests for `Property.Update`, `PropertyDocument.Upload`, or `PropertyDocument.Delete` despite controller wiring.

**Remediation**: Add controller tests mirroring GetDetail pattern for Update/Upload/Delete cross-owner admin flows.

**Deferral**: Implementation code paths are present and symmetric; gap is test coverage only.

---

### 🟢 Medium — 3

#### M1 — AC7 regression test uses reflection, not JSON serialization

`GetPropertyDetailAsync_OtaIntegrations_DoNotExposeApiKey` asserts DTO properties via `GetType().GetProperty()` but does not serialize to JSON and grep for `apikey`/`apisecret` case-insensitively as design AC7 specifies. Low risk given DTO whitelist, but test could be strengthened.

#### M2 — Web layer references Infrastructure `PropertyService.MapDocument`

`PropertiesController.ToDocumentDto` delegates to `Casazen.Infrastructure.Services.PropertyService.MapDocument`, coupling Web → Infrastructure. Consider moving `MapDocument` to Core or a dedicated mapper in Web.

#### M3 — Document MIME validation trusts client `Content-Type`

`ValidateDocument` checks extension + `file.ContentType` without magic-byte verification. Acceptable for MVP; spoofed Content-Type could bypass validation in edge cases.

---

### ⚪ Low / Informational — 4

| ID | Note |
|---|---|
| L1 | `PropertyManagerOrAdmin` policy registered but not applied at action level — intentional per design; runtime uses `CanAccess`. |
| L2 | `ownerId` exposed in `PropertyDetailDto` — required for admin context; not guest PII. |
| L3 | E2E AC11 (legacy redirect) relies on existing route manifest — no new E2E assertion for redirect chain; covered by existing infra. |
| L4 | `Sessions/design-152.md` committed in backend PR — appropriate SDLC artifact. |

---

## Acceptance Criteria Traceability

| AC | Backend | Frontend | Tests |
|---|---|---|---|
| AC1 | ✅ Extended `PropertyDetailResponse` + `PricingAdapterConfig` include | ✅ `PropertyDetailDto` + sections | `PropertyServiceGetDetailTests` |
| AC2 | ✅ `GET /documents` DTO aligned (`downloadUrl`, `fileType`) | ✅ `getDocuments` API | `PropertiesControllerTests` |
| AC3 | ✅ `ValidateDocument` + `UploadDocumentAsync` | ✅ `DocumentUploadDialog` | `UploadDocument_InvalidFile_ReturnsBadRequest` |
| AC4 | ✅ DELETE with property-doc IDOR check | ✅ `useDeletePropertyDocument` | `DeleteDocument_*` tests |
| AC5 | ✅ `CanAccess` + audit on Update | — | `Update_AsNonOwner_ReturnsForbidden` |
| AC6 | ✅ `PropertyManagerOrAdmin` policy | — | Policy registration in `ServiceCollectionExtensions` |
| AC7 | ✅ OTA DTO excludes keys | ✅ `PropertyOtaSummary` types | `DoNotExposeApiKey` (reflection) |
| AC8 | — | ✅ 9 section components + `usePropertyDetail` | Unit + E2E |
| AC9 | ✅ `ResolveCinStatus` server-side | ✅ `PropertyCinBadge` + tooltip | Unit + E2E |
| AC10 | — | ✅ Drag-drop upload dialog | E2E |
| AC11 | — | ✅ No route regression | Inherited guards |
| AC12 | ✅ No apiKey in DTO | ✅ No apiKey in render/types | Unit + E2E |

---

## Security Review Matrix

| Threat | Surface | Mitigation in PR | Status |
|---|---|---|---|
| IDOR on `{id}` | All property endpoints | `IPropertyAuthorizationService.CanAccess` on every action | ✅ |
| OTA secret leakage | Detail JSON | `OtaIntegrationSummaryDto` whitelist + unit test | ✅ |
| Guest PII in aggregates | `BookingsSummary` | Counts + dates only | ✅ |
| Privileged cross-owner access | Admin/PropertyManager reads/writes | `IAdminAccessAuditService` structured Warning log | ✅ (impl); 🟡 (test gap H3) |
| Document upload bomb | POST `/documents` | 10 MB + MIME/extension gate | ✅ |
| Unauthenticated doc download | Static files | UUID paths only | 🟡 H1 |
| Frontend route bypass | `/properties/:id` | Inherited `ProtectedRoute` + `ContextRouteGuard` | ✅ |

---

## Test Evidence

| Suite | Result |
|---|---|
| Backend `dotnet test` | **386 passed**, 0 failed, 25 skipped |
| Frontend (per PR body) | 95 unit tests pass; E2E 21 passed (4 property-detail specs) |

---

## Recommendation

**Approve both PRs for merge to `develop`** — all harness gates G1–G10 pass with 0 🔴 critical findings.

Track follow-up for **H1** (authenticated document download) before production exposure of compliance documents at scale.

---

## Handoff → Stage 05

| Item | Value |
|---|---|
| Issue | #152 |
| Backend PR | #193 |
| Frontend PR | #96 |
| Design spec | `Sessions/design-152.md` |
| Review artifact | `Sessions/review-152.md` |
