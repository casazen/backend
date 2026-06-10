# Release — Pricing Adapter Verification

**Stage**: 05 Release  
**Date**: 2026-06-05  
**Spec**: `Sessions/specs/spec-pricing-adapter-verification.md`  
**Backend PR**: https://github.com/casazen/backend/pull/196 → release https://github.com/casazen/backend/pull/197  
**Frontend PR**: https://github.com/casazen/frontend/pull/99 → release https://github.com/casazen/frontend/pull/100

---

## Phase A — Merge to develop

| Gate | Status | Notes |
|---|---|---|
| G1 BE PR CI | ✅ | Build & Test |
| G2 FE PR CI | ✅ | e2e |
| G3 BE merged | ✅ | #196 squash |
| G4 FE merged | ✅ | #99 squash |

**Develop SHAs (post-feature):** BE `1d847d4` · FE `89153b7`

---

## Phase B — Staging validation

| Gate | Status | Evidence |
|---|---|---|
| G5 Railway test health | ✅ | `GET casazen-api-test.../api/health` → 200 |
| G6 Auth smoke | ✅ | `/api/properties` → 401 |
| G7 dotnet test | ✅ | 407 passed |
| G8 npm run test:e2e | ✅ | pricing-adapter 11/11 |
| G9 AC validated | ✅ | AC1–AC20 covered by automated tests |
| G10 Staging FE SPA | ✅ | Demo E2E via Playwright (develop tip) |

**Pricing smoke:** `GET .../api/pricing-adapter/config/{id}` → 401 (not 500)

---

## Phase C — Promote develop → main

| Gate | Status | Notes |
|---|---|---|
| G11 Semver | ✅ | BE v1.1.4 · FE v0.1.6 |
| G12 BE release merged | ✅ | #197 squash |
| G13 FE release merged | ✅ | #100 squash |
| G14 Tags pushed | ✅ | `v1.1.4` · `v0.1.6` |
| G15 GitHub Releases | ✅ | [BE](https://github.com/casazen/backend/releases/tag/v1.1.4) · [FE](https://github.com/casazen/frontend/releases/tag/v0.1.6) |

**Main SHAs:** BE `95d5e9e` · FE `1935610`

---

## Phase D — Production validation

| Gate | Status | Evidence |
|---|---|---|
| G16 Railway prod health | ✅ | `GET https://casazen-api.up.railway.app/api/health` → 200 |
| G17 Vercel prod SPA | ✅ | `https://casazen-app.vercel.app` → 200, `id="root"` |
| G18 Feature AC prod | ✅ | Pricing endpoints auth-gated (401); no 5xx |
| G19 Docker build | ⏭️ N/A | Docker daemon unavailable locally |
| G20 Branch alignment | ⚠️ PARTIAL | Tree parity ✅; commit-count drift (squash) — see below |
| G21 Build parity | ✅ | `dotnet build` + `npm run build` pass on both tips |

### Branch sync (G20)

| Repo | main ahead of develop | develop ahead of main | Tree diff |
|---|---|---|---|
| Backend | 0 | 7 | **empty** |
| Frontend | 0 | 7 | **empty** |

Sync-back: `main` → `develop` merge pushed (BE `c5aa27b`, FE `5ba9a2e`). Squash release leaves develop ahead by pre-squash commits (same tree as main).

---

## Release contents

- Integration tests `PricingAdapterIntegrationTests` (AC1–AC9)
- Config first-save fix (`Id = Guid.Empty`)
- CI verify-test pricing adapter smoke (AC21)
- Playwright `pricing-adapter.spec.ts` (AC16–AC20, 11 tests)
- Unit tests: preview, history pagination, job isolation (AC10–AC11)

---

## Handoff → Stage 06

See `Sessions/ops-report-2026-06-05-pricing-adapter.md`.
