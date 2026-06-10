# Release Report — Issue #202 Plan Management Extension (v1.1.7)

**Date:** 2026-06-09  
**Tag:** v1.1.7  
**Issue:** [#202](https://github.com/casazen/backend/issues/202)

## Summary

Extended tenant boundary (#202) with MVP plan management: org provisioning at onboarding, plan catalog APIs, owner plan change, admin plan override. Added release prod smoke script and CI hardening.

## PRs merged

| Repo | Feature PR | Release PR | Sync PRs |
|---|---|---|---|
| Backend | [#207](https://github.com/casazen/backend/pull/207) | [#208](https://github.com/casazen/backend/pull/208) | #209, #210, #211 |
| Frontend | [#107](https://github.com/casazen/frontend/pull/107) | [#108](https://github.com/casazen/frontend/pull/108) | #109, #110, #111 |

## Phase B — Staging (develop)

| Gate | Result |
|---|---|
| G5 Railway test health | ✅ 200 |
| G6d migrations test | ✅ up to date |
| G7 dotnet test | ✅ 448 passed |
| G8 npm run test:e2e | ✅ 49 passed |
| G6d migrations prod (pre-C) | ✅ up to date |

## Phase C — Promote

| Gate | Result |
|---|---|
| G11 tag v1.1.7 | ✅ semver valid |
| G12 backend release merge | ✅ #208 |
| G13 frontend release merge | ✅ #108 |
| G14 tags pushed | ✅ both repos |
| G15 GitHub releases | ✅ [BE](https://github.com/casazen/backend/releases/tag/v1.1.7) · [FE](https://github.com/casazen/frontend/releases/tag/v1.1.7) |

## Phase D — Production

| Gate | Result |
|---|---|
| G16 prod health | ✅ |
| G16b release-smoke.ps1 | ✅ migrations + auth gates + entitlement 401 |
| G17 FE prod SPA | ✅ casazen-app.vercel.app id="root" |
| G18 prod E2E | ⏭ CI on main (requires E2E Auth0 secrets) |
| G19 docker build | ✅ (implicit via Railway deploy) |
| G20 branch alignment | ⚠️ identical trees; tips differ by merge commits (develop 1 ahead) |
| G21 build parity | ✅ dotnet build + npm run build on main |

## Notes

- E2E fix commit `8982f98`: onboarding 2-step flow + pricing org mocks.
- Supersedes v1.1.6 tenant-boundary-only release for plan UI/API surface.
