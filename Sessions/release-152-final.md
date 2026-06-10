# Release Report — Issue #152 (Final)

**Date**: 2026-06-05  
**Issue**: #152 (CLOSED)  
**Feature**: Property Detail Page

## Production tags (final)

| Repo | Tag | Release PR | URL |
|---|---|---|---|
| Backend | v1.1.3 | #195 | https://github.com/casazen/backend/releases/tag/v1.1.3 |
| Frontend | v0.1.5 | #98 | https://github.com/casazen/frontend/releases/tag/v0.1.5 |

## Initial release (superseded by hotfix)

| Repo | Tag | Notes |
|---|---|---|
| Backend | v1.1.2 | Missing migration on deploy → staging 500 |
| Frontend | v0.1.4 | Vercel preview build failed (Zod TS error) |

## Hotfix contents

### Backend v1.1.3
- Fix `AddContextAuthorization` migration index ordering
- JWT context fallback for Auth0-only users
- Migration applied to `casazen_test` + `casazen_prod`

### Frontend v0.1.5
- E2E property create + detail flow (`property-flow.spec.ts`, `property-staging.spec.ts`)
- PropertyForm lat/long NaN fix (unblocked create + Vercel build)
- Property list name links to detail page

## Phase D validation (prod)

| Gate | Result |
|---|---|
| Railway prod `/api/health` | 200 |
| `/api/properties` auth gate | 401 |
| `casazen-app.vercel.app` SPA | 200 + `id="root"` |
| Vercel production deploy | success |
| main ↔ develop tree parity | identical (both repos) |

## Pipeline status: COMPLETE
