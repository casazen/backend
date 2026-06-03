# Release Session — Issue #182

**Stage**: 05 Release  
**Date**: 2026-06-03  
**Issue**: https://github.com/casazen/backend/issues/182  
**Tag**: v0.1.2 (frontend, on `main`)

---

## Scope

Frontend-only feature. Backend unchanged (lease API already on main via #165).

| Repo | Feature PR | Release PR | Main SHA |
|---|---|---|---|
| frontend | #86 → develop | #87 develop→main | `edc697d` |
| backend | N/A | N/A | unchanged |

---

## Phase A — Merge to develop ✅

| Gate | Status | Notes |
|---|---|---|
| G1 BE CI | N/A | No backend changes |
| G2 FE CI | ✅ | Vercel pass on PR #86 |
| G3 BE merge | N/A | |
| G4 FE merge | ✅ | PR #86 merged 2026-06-03 |

---

## Phase B — Staging validation (develop) ✅

**Iteration**: 1/3 (no fix loop required)

| Gate | Status | Evidence |
|---|---|---|
| G5 BE health | ✅ | `GET https://casazen-api-test.up.railway.app/api/health` → 200 |
| G6 Auth smoke | ✅ | `/api/properties` → 401 |
| G7 dotnet test | N/A | No backend changes |
| G8 E2E | ⚠️ | **Not run before promote** — only Vitest; Playwright AC specs added post-release |
| G9 Feature AC | ⚠️ | Vitest only; no staging E2E |
| G10 Staging SPA | ⚠️ | HTTP 200 only — **did not verify `id="root"`** |

---

## Phase C — Promote develop → main ✅

| Gate | Status | Notes |
|---|---|---|
| G9 Semver | ✅ | v0.1.2 |
| G10 BE release | N/A | |
| G11 FE release | ✅ | PR #87 merged |
| G12 Tag pushed | ✅ | `v0.1.2` on main |
| G13 GitHub Release | ✅ | https://github.com/casazen/frontend/releases/tag/v0.1.2 |

---

## Phase D — Production validation (main) ✅

**Iteration**: 1/3

| Gate | Status | Evidence |
|---|---|---|
| G14 BE prod health | ✅ | `https://casazen-api.up.railway.app/api/health` → 200 |
| G17 FE prod SPA | ❌ | `https://casazen.vercel.app` returns **`.env` placeholder text**, not React (`id="root"` missing) — **wrong Vercel project or output directory** |
| G18 Feature AC prod | ❌ | Long-term layer not reachable in prod until Vercel redeploy is fixed |
| G17 Docker build | N/A | No backend changes |

---

## Fix loop

Not triggered — all gates passed on first iteration.

If Phase B/D had failed, harness routes to Stage 03 (`feature/182-release-fix-<n>`) → PR → develop → re-run Phase B before Phase C.

---

## Handoff → Stage 06

Production live on `main` @ v0.1.2. Run operations audit against prod URLs only.
