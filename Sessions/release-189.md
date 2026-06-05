# Release Report — Issue #189

**Date**: 2026-06-04  
**Feature**: Context workspace switcher (short-rent, long-rent, admin)

## Phase A — Merge to develop

| Repo | PR | SHA | Status |
|---|---|---|---|
| Backend | [#190](https://github.com/casazen/backend/pull/190) | squash merged to `develop` | MERGED |
| Frontend | [#94](https://github.com/casazen/frontend/pull/94) | squash merged to `develop` | MERGED |

## Phase B — Staging validation (develop)

| Gate | Result | Notes |
|---|---|---|
| G5 Railway test health | PASS | `GET https://casazen-api-test.up.railway.app/api/health` → 200 |
| G6 Auth smoke | PASS | `GET /api/properties` → 401 |
| G7 Backend tests | PASS | `dotnet test` on develop — 381 passed |
| G8 E2E tests | PASS | `npm run test:e2e` on develop — 17 passed, 7 skipped |
| G9 Feature AC | PASS | E2E specs cover context routing, switcher, legacy redirects |
| G10 Staging FE SPA | DEFERRED | Vercel preview URL varies; prod SPA verified in Phase D |

## Phase C — Promote develop → main

| Item | Value |
|---|---|
| Backend tag | v1.1.1 (patch from v1.1.0) |
| Frontend tag | v0.1.3 (patch from v0.1.2) |

## Phase D — Production validation

| Gate | Result |
|---|---|
| G16 Prod API health | PASS |
| G17 Prod FE SPA | FAIL — `casazen.vercel.app` serves non-SPA content (infra) |
| G20 main ↔ develop aligned | **FAIL at release time** → **FIXED** (see below) |

## Branch sync (G20) — required after prod promotion

At release time, `main` was **ahead** of `develop` (merge to `main` without sync-back):

| Repo | main ahead of develop | develop ahead of main |
|---|---|---|
| Backend | 9 | 0 |
| Frontend | 4 | 0 |

**Remediation** (2026-06-04): `git merge origin/main` into `develop` on both repos and push.

| Repo | SHA after sync | Aligned |
|---|---|---|
| Backend | `4de4951` | ✅ both tips equal |
| Frontend | `46fdf24` | ✅ both tips equal |

**Root cause**: local `develop` → `main` merge + push to `main` without merging `main` back to `develop`. Prefer release PR squash + mandatory G20, or always sync `develop` after tagging `main`.

### G21 failure — `use-auth.ts` TS6133 (2026-06-05)

`main` had `demoUser` from a bad sync (`main` → `develop` copied broken code). Vercel build on `main` failed: `'demoUser' is declared but its value is never read`.

**Fix (correct direction):**
1. Fix on `develop`: use `user: demoUser` instead of `getDemoUser()` in demo return.
2. `npm run build` ✅ on `develop`.
3. Promote **`develop` → `main`** (fast-forward `6443bbc`).

**Not** merge broken `main` into `develop` again without build check.
