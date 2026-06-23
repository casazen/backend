# Operations Report — 2026-06-20

## Release
- Backend: **v1.2.3** — Batch F0 (resolve-host, iCal spike, mobile init script)
- Frontend: **v1.1.24** — Batch F0 (GJ E2E steps 1–4, resolve-host contract test)
- Epic: **#286** closed

## Production health (Phase D)

| Check | URL | Result |
|---|---|---|
| Railway API health | `https://casazen-api.up.railway.app/api/health` | 200 |
| Auth gate | `/api/properties` | 401 |
| resolve-host (reserved) | `/api/public/resolve-host?host=www.casazen.it` | 404 (expected) |
| Vercel SPA | `https://casazen-app.vercel.app` | 200, `#root` present |

## Branch sync (G20)
- Backend: `main` merged into `develop` after v1.2.3
- Frontend: develop aligned with main content (squash history may show ahead count > 0)

## Issues closed
- #287, #288, #289, #290 (backend), #301 (frontend)

## Follow-ups (non-blocking)
1. Push `casazen/mobile` repo via `scripts/init-mobile-repo.ps1`
2. Execute manual GJ runbook on staging when seeded org slug available
3. Fase 1: GJ steps 5–12 (calendar, check-in, service loop)
