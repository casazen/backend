# Stage 06 — Operations Report

**Date**: 2026-06-03  
**Release**: casazen/frontend v0.1.1  
**Feature**: Long-term UI layer separation (issue #182, PR #86)  
**Scope**: Frontend-only — no backend deploy

---

## Compliance Status

| Regulation | Status | Notes |
|---|---|---|
| CIN (D.L. 145/2023) | ✅ N/A | No property flows changed |
| GDPR (Article 17) | ✅ Pass | No new PII collection; layer pref is non-PII enum in localStorage |
| Alloggiati Web | ✅ N/A | Short-stay layer unchanged for guest reporting |
| Tourist Tax | ✅ N/A | No pricing/tax logic touched |
| Auth0 / RBAC | ✅ Pass | `/leases/*` retains `ProtectedRoute role=\"LongTermLandlord\"`; backend policy authoritative |

---

## Release Verification

| Check | Status | Evidence |
|---|---|---|
| PR #86 merged to `develop` | ✅ | Squash merge 2026-06-03 |
| Tag `v0.1.1` pushed | ✅ | `git push origin v0.1.1` |
| GitHub Release created | ✅ | casazen/frontend releases |
| Vercel CI (PR) | ✅ | Preview deployment passed pre-merge |
| New Vitest tests (20) | ✅ | Guards, role helpers, layer switcher |
| Issue #182 closed | ✅ | Backend tracking issue |

---

## Incident Log

None for this release.

---

## KPI Snapshot

| Metric | Value |
|---|---|
| FE files changed | 22 |
| New unit tests | 20 |
| Backend changes | 0 |
| Open critical review findings | 0 |

---

## Action Items

| # | Item | Priority | Issue |
|---|---|---|---|
| 1 | Add E2E tests for dual-role layer switching | Low | Follow-up (deferred M1 from review) |
| 2 | Italian UI labels for layer switcher if product requires | Low | Optional UX polish |

---

## Pipeline Notes

- HITL gates removed from `sdlc-pipeline` skill per user request (2026-06-03)
- Stage 05 executed automatically: merge → tag → release without manual pause
