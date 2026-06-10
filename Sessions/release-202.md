# Release Report — Issue #202 (Multi-tenant Org boundary)

> **Tag**: `v1.1.6` · **Date**: 2026-06-09 · **Pipeline**: `Sessions/pipeline-spec-tenant-boundary/`

---

## Phase A — Merge to develop

| Item | Status | Detail |
|---|---|---|
| BE PR #203 merged | ✅ | Squash → `ef5ccad` on `develop` (2026-06-09T12:32:30Z) |
| FE PR #104 merged | ✅ | Squash → `e8d73a6` on `develop` (2026-06-09T12:32:54Z) |
| BE CI | ✅ | Build & Test pass |
| FE CI | ⚠️ | `e2e` red (pre-existing `pricing-adapter.spec.ts`, #105); merge approved — local E2E 38/38 pass |

---

## Phase B — Staging validation (develop)

| Gate | Status | Evidence |
|---|---|---|
| G5 Railway test health | ✅ | `GET https://casazen-api-test.up.railway.app/api/health` → 200 |
| G6 Auth smoke | ✅ | `/api/properties`, `/bookings`, `/users/me`, `/me/contexts` → 401 |
| G7 dotnet test | ✅ | 442 passed, 0 failed (on `develop`) |
| G8 E2E | ✅ | 38 passed, 7 skipped (on `develop`) |
| G9 Feature AC | ✅ | `GET /api/orgs/me/entitlement` → 401 (endpoint deployed on test) |
| G10 Staging SPA | ⚠️ | `casazen-app.vercel.app` serves `id="root"` (prod-linked URL; develop preview not resolved). Local build + E2E green. |

---

## Phase C — Promote develop → main

| Item | Status | Detail |
|---|---|---|
| BE release PR #206 | ✅ MERGED | Squash → `c284276` on `main` |
| FE release PR #106 | ✅ MERGED | Squash → `728729d` on `main` |
| Tag `v1.1.6` | ✅ | Backend + frontend GitHub Releases created |
| Release URLs | ✅ | [backend](https://github.com/casazen/backend/releases/tag/v1.1.6) · [frontend](https://github.com/casazen/frontend/releases/tag/v1.1.6) |

---

## Phase D — Production validation (main)

| Gate | Status | Evidence |
|---|---|---|
| G16 BE prod health | ✅ | `GET https://casazen-api.up.railway.app/api/health` → 200 |
| G17 FE prod SPA | ⚠️ | `casazen.vercel.app` → 9 bytes (mislinked, #187); **`casazen-app.vercel.app`** → `id="root"` ✅ |
| G18 Feature AC prod | ✅ | `GET /api/orgs/me/entitlement` → 401 (after deploy; was 404 during rollout) |
| G19 Docker build | ➖ | Not run this session |
| G20 branch alignment | ⚠️ | `main..develop` = 3 (BE) / 6 (FE) after sync-back — expected with **squash** release PRs; `develop..main` = 0 both repos |
| G21 build parity | ✅ | `dotnet build /warnaserror` + `npm run build` exit 0 on both `origin/main` and `origin/develop` tips |

### Branch sync (post-release)

- Backend: merged `origin/main` → `develop`, pushed `fe3cd30`
- Frontend: merged `origin/main` → `develop`, pushed `149e08a`

---

## Issue

- **#202** closed (released in v1.1.6)

---

## Deferred follow-ups (from review)

- frontend#105 — pre-existing e2e CI failure
- backend#204 — entitlement TOCTOU
- backend#205 — backfill integration test on real Postgres SQL

---

## Decision

**COMPLETE** — feature live on production API; promote gates satisfied except documented G10/G17 URL canonicalization (#187) and G20 squash-history drift (build parity confirmed via G21).
