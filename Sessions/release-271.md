# Release — Issue #271 (Onboarding PLG)

**Status:** BLOCKED (Stage 05 Phase A incomplete)  
**Tick:** delivery-15  
**Evidence:** `Sessions/loop/evidence/delivery-15/gates.json` (`overall: fail`)  
**Date:** 2026-08-14T00:26:23Z

## Phase A — Merge to develop

| Gate | Result | Notes |
|---|---|---|
| G_ENTRY_S04 | ✅ | `Sessions/review-404.md` (+ code/security) present |
| G3 BE merged | ✅ | PR https://github.com/casazen/backend/pull/404 MERGED `6ed50de` on `origin/develop` |
| G4 FE merged | ❌ | No FE PR for #271; `casazen/frontend` push **403** (`cursor[bot]` denied) |
| G_FE_WRITE | ❌ | `permissions.push=false` |

## Phase B — Staging validation

**Not entered** — blocked on G4 / FE write access.  
Informational smoke only (not promote evidence):

| Check | Result |
|---|---|
| `GET https://casazen-api-test.up.railway.app/api/health` | HTTP 200 `{"status":"healthy"}` |
| `GET .../api/properties` (no auth) | HTTP 401 |

UI ACs AC8–AC12 require FE L2/L3 on `casazen/frontend` (`e2e/onboarding-plg.spec.ts`, ActivationChecklist, SubprocessorsPage). Local FE patch from tick 14 cannot be pushed.

## Phase C — Promote develop → main

**Not entered.** No tag. No release PR. No issue close.

## Phase D — Production

**Not entered.**

## Unblock checklist

1. Grant Automation write access to `casazen/frontend` (or land FE PR #271 manually from tick-14 patch).
2. Merge FE PR → `develop`.
3. Re-run Stage 05: Phase B G5–G10 (+ G9 AC map) via `sdlc-gate-runner`.
4. Only then Phase C promote + Phase D.

## Decision

**BLOCKED** — sticky pipeline remains `05-release`. Do not escalate yet (access/secrets blocker, not impl FAIL × 3).
