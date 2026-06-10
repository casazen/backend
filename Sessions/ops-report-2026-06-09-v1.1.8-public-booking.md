# Operations Report — v1.1.8 Public Booking Read-Model

**Date:** 2026-06-09  
**Environment:** Production  
**Tag:** v1.1.8 · Issue [#212](https://github.com/casazen/backend/issues/212)

## Production health

| Check | Result |
|---|---|
| API health | ✅ 200 |
| Auth gates | ✅ 401 on protected endpoints |
| FE SPA | ✅ casazen-app.vercel.app |
| Migrations | ✅ casazen_prod up to date |

## Feature verification

- `GET /api/properties/search` — anonymous, returns DTO array (no `ownerId` in JSON)
- `GET /api/properties/{id}/public` — deployed behind auth gate pattern (401 without token on owner routes; public route anonymous)

## Follow-ups

- Branded booking site will consume `GET /api/properties/{id}/public` (downstream spec)
- Frontend eslint debt (47 errors) — separate cleanup issue recommended
