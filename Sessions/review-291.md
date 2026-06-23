# Review — MVP F1 Epic Wave 1 (#291 / #292)

**Date:** 2026-06-22  
**PRs:** BE [#312](https://github.com/casazen/backend/pull/312) · FE [#159](https://github.com/casazen/frontend/pull/159)  
**Design:** `Sessions/design-291.md` (Wave 1 scope)

## Gate status

| Gate | Status | Notes |
|---|---|---|
| G1 PR mergeable | ✅ | Both PRs `MERGEABLE` |
| G2 Critical findings | ✅ | 0 open 🔴 |
| G3 High findings | ✅ | 1 deferred (see below) |
| G4 Cross-repo consistency | ✅ | FE `/supplier/*` calls match BE contract |
| G5 IDOR | ✅ | `/api/supplier/*` scoped via `IOrgContextResolver` |
| G6 Raw SQL | ✅ | None in supplier code |
| G7 PII exposure | ✅ | No Guest entity changes |
| G8 Stripe webhook | N/A | Not modified |
| G9 GDPR fields | N/A | No Guest flows |
| G10 ProtectedRoute | ✅ | `/supplier/*` wrapped; admin invite in admin context |

## Findings

### 🟡 High (deferred)

| ID | Area | Finding | Action |
|---|---|---|---|
| R1 | BE | `CreateInviteAsync` persists invite record but does not send email yet | Accept for Wave 1; track in #292 follow-up or Wave 2 |

### 🟢 Medium

| ID | Area | Finding | Action |
|---|---|---|---|
| R2 | BE | `POST /api/suppliers/register` is public without rate limiter | Add rate limit in Wave 2 hardening |
| R3 | BE | Self-serve register creates org without Auth0 user linkage | Document: supplier must sign up via Auth0 with `Supplier` role post-register |
| R4 | FE | Global `npm run lint` has pre-existing errors unrelated to this PR | No new supplier-specific lint errors |

## Cross-repo checklist

- [x] `GET/PUT /api/supplier/profile` ↔ `supplier-api.ts`
- [x] `GET/POST /api/supplier/profile/activation*` ↔ activation wizard
- [x] `GET /api/supplier/inbox` ↔ inbox shell (empty until #293)
- [x] `PUT /api/supplier/availability` ↔ availability page
- [x] `POST /api/admin/suppliers/invite` ↔ admin invite page
- [x] Demo profile `supplier` added for E2E

## Verdict

**PASS** — 0 critical findings. Ready for Stage 05 after CI green and PR approval.

## CI

- BE #312: Build & Test in progress at review time
- FE #159: E2E in progress at review time
