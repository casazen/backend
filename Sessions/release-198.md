# Release — Issue #198 Role-Based Onboarding

**Date**: 2026-06-05

## Phase A — Merge to develop
- BE #199 merged
- FE #101 merged

## Phase B — Staging validation
- `dotnet test`: 414 pass
- E2E onboarding: 5 pass
- Railway test health: 200

## Phase C — Promote to main
- BE release PR #200 → `v1.1.5`
- FE release PR #102 → `v0.1.7`

## Phase D — Production smoke
- API health: 200
- `/api/users/onboarding`: 401
- Vercel SPA: OK

## G20/G21
- `main` and `develop` aligned (fast-forward sync)
- Build pass on both tips (BE + FE)
