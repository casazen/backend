# Review — Batch F0 implementation (#288, #289, #287, #301)

**Date:** 2026-06-19  
**Backend PR:** [#310](https://github.com/casazen/backend/pull/310)  
**Frontend PR:** [#157](https://github.com/casazen/frontend/pull/157)  
**Design:** `Sessions/design-batch-f0.md`

## Code review (Stage 04)

| ID | Severity | Area | Finding | Status |
|---|---|---|---|---|
| — | — | — | No critical or high findings | ✅ |

### Security (G5–G8)

- `resolve-host` is `[AllowAnonymous]` — returns public branding only, no Stripe/plan fields ✅
- Reserved subdomain allowlist blocks `api`, `www`, etc. ✅
- iCal spike has no HTTP surface; no PII in export test fixtures ✅
- No secrets in diff ✅

### Cross-repo (G4)

- FE E2E mocks `resolve-host` contract matching BE DTO shape ✅
- GJ steps 1–4 use existing branded-booking mocks aligned with public org API ✅

## Devil's Advocate (post-design)

### Challenge 1: Custom domain deferred without guardrail
**Category:** assumption  
**Reference:** `Sessions/design-batch-f0.md` — "Custom domain CNAME → out of scope F0"  
**Issue:** Staging PoC AC for #288 requires subdomain resolution on real infra; design does not specify which org slug is seeded on Railway test for manual verification.

### Challenge 2: Epic closure vs mobile repo
**Category:** completeness-gap  
**Reference:** #287 AC — "Expo builds on iOS/Android simulator"  
**Issue:** `scripts/init-mobile-repo.ps1` does not prove simulator build; epic cannot fully close #287 without `casazen/mobile` repo push and CI.

### Challenge 3: GJ manual runbook not automated
**Category:** vagueness  
**Reference:** Epic #286 AC — "Staging GJ steps 1–4 pass once (manual runbook)"  
**Issue:** Playwright demo mocks pass in CI but do not substitute staging manual run; epic AC partially satisfied only.

**Verdict:** OBJECT (3 substantive issues) — acceptable for F0 batch **code merge**; document residual manual steps before epic close.

## Recommendation

**Approve merge to `develop`** — code quality and tests sufficient. Epic #286 remains **partially open** until mobile repo (#287) and manual GJ runbook executed on staging.

## Gate status

| Gate | Status |
|---|---|
| G1 PR mergeable | pending CI |
| G2 No critical | ✅ |
| G3 High addressed | ✅ |
| G4 Cross-repo | ✅ |
| G5–G10 | ✅ / N/A |
