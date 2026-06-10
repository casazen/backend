# Release Report — Issue #212 Public Booking Read-Model (v1.1.8)

**Date:** 2026-06-09  
**Tag:** v1.1.8

## PRs

| Repo | Feature | Release |
|---|---|---|
| Backend | [#213](https://github.com/casazen/backend/pull/213) | [#214](https://github.com/casazen/backend/pull/214) |
| Frontend | [#112](https://github.com/casazen/frontend/pull/112) | [#113](https://github.com/casazen/frontend/pull/113) |

## Phase B (staging)

| Gate | Result |
|---|---|
| dotnet test | ✅ 463 passed |
| npm run test:e2e | ✅ 52 passed |
| migrate prod (pre-C) | ✅ |

## Phase C–D (production)

| Gate | Result |
|---|---|
| Tag v1.1.8 | ✅ [BE](https://github.com/casazen/backend/releases/tag/v1.1.8) · [FE](https://github.com/casazen/frontend/releases/tag/v1.1.8) |
| release-smoke.ps1 | ✅ |
| Public search no ownerId | ✅ verified on prod |

## Review

`Sessions/review-212.md` — 0 critical findings.
