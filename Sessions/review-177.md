# Review Session — Issue #177 / PR #84
# [FE] Long-Term Lease — UI section

**Stage**: 04 Review  
**Date**: 2026-06-02  
**Issue**: https://github.com/casazen/backend/issues/177  
**PR**: https://github.com/casazen/frontend/pull/84  
**Design ref**: `Sessions/design-177.md`

---

## Council

| Agent | Scope |
|---|---|
| code-reviewer | Logic, AC coverage, tests, async patterns |
| security-auditor | Auth routes, PII handling, IDOR (backend delegated) |
| coordinator | Gate validation, finding triage |

---

## Findings

### 🔴 Critical — 0 open

None.

### 🟡 High — resolved in review iteration 1

| ID | Finding | Resolution |
|---|---|---|
| H1 | No unit tests for new lease/auth modules | Added `auth-roles.test.ts` + `lease.schema.test.ts` |
| H2 | `useLeaseRegistration` fetched on Draft leases → 404 noise | Gated `enabled` to registration-relevant statuses only; `retry: false` |
| H3 | APE validation false-positive while documents loading | Wait for `documentsLoaded` before blocking submit |

### 🟢 Medium — deferred

| ID | Finding | Disposition |
|---|---|---|
| M1 | Signing URLs lost on page refresh | Accepted per design spec; re-initiate signing |
| M2 | No lease-specific E2E tests | Follow-up issue recommended post-merge |
| M3 | `tsc`/`build` fail on main (pricing module) | Pre-existing; out of scope for #177 |

### ⚪ Low — noted

| ID | Finding |
|---|---|
| L1 | Sidebar adds nav item — no mobile nav update (desktop-only sidebar unchanged pattern) |

---

## Security Audit

| Check | Result |
|---|---|
| G5 IDOR (backend) | ✅ Server enforces owner scope on `/api/leases/*` |
| G6 Raw SQL | N/A — FE only |
| G7 PII in errors | ✅ Generic toasts; no API error body surfaced |
| G8 Stripe webhook | N/A |
| G9 GDPR Guest fields | N/A — Party PII displayed only to authenticated owner |
| G10 ProtectedRoute | ✅ All 3 `/leases/*` routes use `<ProtectedRoute role="LongTermLandlord">` |

**PII display**: Party names/emails shown on detail page — intentional for owner workflow. Fiscal codes collected in form but not echoed in toasts.

---

## Acceptance Criteria Verification

| AC | Status | Evidence |
|---|---|---|
| AC1 Role-protected list | ✅ | `routes/index.tsx` + `ProtectedRoute role` |
| AC2 Empty state | ✅ | `LeasesPage` + `EmptyState` |
| AC3 Create draft | ✅ | `LeaseCreateForm` + `useCreateLease` |
| AC4 Initiate signing | ✅ | `LeaseDetailPage` + `LeaseSigningPanel` |
| AC5 Register | ✅ | `RegistrationStatusPanel` + `useTriggerRegistration` |
| AC6 Download receipt | ✅ | `leasesApi.downloadReceipt` blob |
| AC7 APE pre-validation | ✅ | `getDocuments` + client guard (fixed loading race) |
| AC8 Extra-EU banner | ✅ | `ExtraEUWarningBanner` |

---

## CI Status

| Check | Result | Notes |
|---|---|---|
| `npm test` | ✅ Pass (72 tests after review fixes) | |
| E2E workflow | ❌ Fail | Pre-existing flaky `pricing-ai-flow.spec.ts` (unrelated to leases) |
| `tsc -b` | ❌ Fail on main | Pre-existing pricing type errors |

---

## Harness Gate Status

| Gate | Status | Notes |
|---|---|---|
| G1 PR approval | ✅ | Approved after review fixes pushed |
| G2 No critical findings | ✅ | |
| G3 High findings addressed | ✅ | H1–H3 fixed |
| G4 Test coverage | ✅ | Schema + auth-roles unit tests added |
| G5–G8 Security (BE) | N/A / ✅ | Backend unchanged |
| G9 GDPR Guest | N/A | |
| G10 Frontend auth routes | ✅ | |

**Result**: Ready for Stage 05 Release (merge when E2E flakiness accepted or fixed separately).

---

## Handoff → Stage 05

- Approved PR: `#84` on `casazen/frontend`
- Merge after stakeholder accepts E2E deferral or pricing E2E fix lands
