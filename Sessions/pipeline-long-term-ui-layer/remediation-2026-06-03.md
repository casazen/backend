# Remediation — Long-term UI layer (#182)

**Date**: 2026-06-03

## Root cause (prod / dev visibility)

`https://casazen.vercel.app` does **not** serve the CasaZen frontend. The response body is a stray `.env` template (`GEMINI_API_KEY`, `APP_URL`), not `dist/index.html`.

Stage 05 marked release green because gates only checked HTTP 200, not SPA content.

## Code + process fixes applied

| Area | Change |
|---|---|
| Stage 03 harness | Gate **G9**: `npm run test:e2e` required; tests mapped to Issue ACs |
| Stage 05 harness | **G7** `dotnet test`, **G8** E2E, **G10** SPA check (`id="root"`) **before** `develop` → `main` |
| QA validator / coordinators | Executable commands + block promote if G7–G10 fail |
| Frontend | `e2e/long-term-layer.spec.ts` (AC1–AC6), `vercel.json`, demo profiles, sessionStorage for `demoProfile` |
| CI | `e2e.yml` runs on `develop` and `main` |
| Docs | `AUTH0_SETUP.md` — `LongTermLandlord` role; `INFRA.md` — SPA sanity check |

## Required manual ops (Vercel)

1. Vercel dashboard → project linked to `casazen/frontend` → **Production** domain `casazen.vercel.app`
2. Build: `npm run build`, output **`dist`** (now in `vercel.json`)
3. Redeploy `main` after merge of frontend fixes
4. Verify: `curl -s https://casazen.vercel.app | findstr "id=\"root\""` (PowerShell) or grep on Linux

## Auth0 (real users, not demo)

Assign role **`LongTermLandlord`** (alone or with `PropertyOwner`) so `/leases` and the long-term shell are available. PropertyOwner-only users will not see long-term nav by design (AC1).

## Re-release checklist

- [ ] Merge frontend branch with E2E + `vercel.json` to `develop`
- [ ] `dotnet test` + `npm run test:e2e` on `develop`
- [ ] Staging SPA check passes
- [ ] Promote to `main`, confirm prod serves React app
- [ ] Spot-check `/leases` with LongTermLandlord user
