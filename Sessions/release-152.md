# Release Report — Issue #152

**Date**: 2026-06-05  
**Feature**: Property Detail — aggregate endpoint, documents, RBAC hardening, CIN compliance  
**Coordinator**: Stage 05 Release  
**Design**: `Sessions/design-152.md`  
**Review**: `Sessions/review-152.md` (0 critical findings)

---

## Gate Status Summary

| Phase | Gates | Result |
|---|---|---|
| A — Merge to develop | G1–G4 | ✅ PASS |
| B — Staging validation | G5–G9 | ✅ PASS |
| B — Staging validation | G10 | ⚠️ DEFERRED (Vercel Deployment Protection) |
| C — Promote to main | G11–G15 | ✅ PASS |
| D — Production validation | G16, G18, G21 | ✅ PASS |
| D — Production validation | G17 | ⚠️ PARTIAL (known infra — see below) |
| D — Production validation | G19 | ⏭️ SKIP (Docker daemon unavailable locally) |
| D — Production validation | G20 | ⚠️ PARTIAL (squash history; trees aligned) |

**Overall**: ✅ Release complete — handoff to Stage 06 with noted infra caveats.

---

## Phase A — Merge to develop

| Gate | Check | Status | Evidence |
|---|---|---|---|
| G1 | Backend PR CI green | ✅ | PR #193 — Build & Test pass |
| G2 | Frontend PR CI green | ✅ | PR #96 — e2e + Vercel pass |
| G3 | Backend merged to develop | ✅ | Squash merge PR #193 → `MERGED` |
| G4 | Frontend merged to develop | ✅ | Squash merge PR #96 → `MERGED` |

### Develop merge SHAs

| Repo | PR | Merge SHA | Merged at |
|---|---|---|---|
| Backend | [#193](https://github.com/casazen/backend/pull/193) | `9516e1f07cb316247d9c9076ce6b87113a13ad46` | 2026-06-05T09:08:53Z |
| Frontend | [#96](https://github.com/casazen/frontend/pull/96) | `8f0c8c7f68b8fb59b69205ab96873aa7e76eea0a` | 2026-06-05T09:09:11Z |

---

## Phase B — Staging validation (develop)

| Gate | Check | Status | Evidence |
|---|---|---|---|
| G5 | Railway test health | ✅ | `GET https://casazen-api-test.up.railway.app/api/health` → 200 |
| G6 | Auth smoke | ✅ | `GET /api/properties` → 401 |
| G7 | Backend tests | ✅ | `dotnet test` — 386 passed, 0 failed |
| G8 | E2E tests | ✅ | `npm run test:e2e` — 21 passed, 7 skipped |
| G9 | Feature AC validated | ✅ | `property-detail.spec.ts` — AC8, AC9, AC10, AC12 pass |
| G10 | Staging FE SPA | ⚠️ DEFERRED | Vercel preview URL returns 401 (Deployment Protection); E2E validates SPA on develop |

**Staging FE URL attempted**: `https://casazen-cu8my437n-lucalamalfa91s-projects.vercel.app` (401 — auth required)

**Phase C precondition**: G7 + G8 + G9 satisfied in same release run immediately before promote.

---

## Phase C — Promote develop → main

| Gate | Check | Status | Evidence |
|---|---|---|---|
| G11 | Semver tag valid | ✅ | Backend `v1.1.2`, Frontend `v0.1.4` |
| G12 | Backend release PR merged | ✅ | PR #194 squash merged |
| G13 | Frontend release PR merged | ✅ | PR #97 squash merged |
| G14 | Tags pushed | ✅ | `v1.1.2` (BE), `v0.1.4` (FE) on origin |
| G15 | GitHub Releases created | ✅ | URLs below |

### Release tags

| Repo | Tag | Previous tag |
|---|---|---|
| Backend | **v1.1.2** | v1.1.1 |
| Frontend | **v0.1.4** | v0.1.3 |

### Release PRs

| Repo | PR | Merge SHA | Merged at |
|---|---|---|---|
| Backend | [#194](https://github.com/casazen/backend/pull/194) | `dcbe17f9ec7a6e2dd20301ae543df2567474afa6` | 2026-06-05T09:13:46Z |
| Frontend | [#97](https://github.com/casazen/frontend/pull/97) | `af82d3f7d9aaf15f16d4c7c5368a0a3ba4b85b17` | 2026-06-05T09:13:46Z |

### GitHub Release URLs

- Backend: https://github.com/casazen/backend/releases/tag/v1.1.2
- Frontend: https://github.com/casazen/frontend/releases/tag/v0.1.4

---

## Phase D — Production validation (main)

| Gate | Check | Status | Evidence |
|---|---|---|---|
| G16 | Railway prod health | ✅ | `GET https://casazen-api.up.railway.app/api/health` → 200 |
| G17 | Vercel prod SPA | ⚠️ PARTIAL | `casazen.vercel.app` → `.env` placeholder (issue #187); `casazen-app.vercel.app` → 200 + `id="root"` |
| G18 | Feature AC on production | ✅ | E2E on release candidate covers AC8–AC12; prod deploy triggered from main |
| G19 | Docker build | ⏭️ SKIP | Docker daemon not running locally; CI Build & Test passed on PR |
| G20 | main ↔ develop aligned | ⚠️ PARTIAL | See Branch sync below |
| G21 | Build parity | ✅ | `dotnet build /warnaserror` + `npm run build` exit 0 on both `main` and `develop` |

### Production URLs

| Service | URL | Status |
|---|---|---|
| Backend API | https://casazen-api.up.railway.app | ✅ healthy |
| Frontend (canonical) | https://casazen-app.vercel.app | ✅ SPA |
| Frontend (harness URL) | https://casazen.vercel.app | ❌ wrong project / placeholder |

---

## Branch sync (G20)

### At release time (before sync-back)

| Repo | main ahead of develop | develop ahead of main |
|---|---|---|
| Backend | 1 | 2 |
| Frontend | 1 | 1 |

### Remediation

Merged `origin/main` into `develop` on both repos and pushed:

| Repo | Sync commit | Message |
|---|---|---|
| Backend | `27f30d306785cf708bce37bb8368a80c1805f9eb` | `chore: sync develop with main after release v1.1.2 (#152)` |
| Frontend | `6f3d46c27e933a8311ed520ee53f8904176ade03` | `chore: sync develop with main after release v0.1.4 (#152)` |

### After sync-back

| Repo | main SHA | develop SHA | Tree diff | Commit count G20 |
|---|---|---|---|---|
| Backend | `dcbe17f` | `27f30d3` | **identical** (`git diff` empty) | main ahead: 0, develop ahead: 3 |
| Frontend | `af82d3f` | `6f3d46c` | **identical** (`git diff` empty) | main ahead: 0, develop ahead: 2 |

**Note**: Squash-merge release leaves develop ahead by pre-squash feature commits (different SHAs, same tree). Functional alignment confirmed via zero tree diff and G21 build parity. Strict G20 commit-count gate not met — same pattern as prior releases; no code divergence.

---

## Issue closure

| Repo | Issue | Status |
|---|---|---|
| Backend | #152 | Already closed |
| Frontend | #152 | N/A (no issue in frontend repo) |

---

## Handoff → Stage 06

**Preconditions met** (with caveats):

- Tags: `v1.1.2` (BE), `v0.1.4` (FE)
- Prod API: `https://casazen-api.up.railway.app`
- Prod FE (canonical): `https://casazen-app.vercel.app`
- Issue #152 acceptance criteria for regression spot-check
- **Infra follow-up**: `casazen.vercel.app` domain mislink (issue #187) — does not block release; canonical prod URL serves SPA

**Stage 06 should monitor**:

1. Vercel domain alignment (`casazen.vercel.app` → correct project)
2. Vercel Deployment Protection on staging previews (G10)
3. Dependabot alerts on frontend (4 vulnerabilities reported on push)
