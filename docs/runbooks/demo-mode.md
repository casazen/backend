# Runbook: web demo mode (no login)

Task PL-01 (defects A1-18, A9-38). The code is in place; the product owner applies the Vercel steps below.

Demo mode opens the web app **without Auth0**, as a fake "Demo User" whose roles come from a persona
(`?demoProfile=short-stay|long-term|dual|admin|triple|supplier|onboarding`, or `VITE_DEMO_PROFILE`, default
`long-term`). It is meant for presentations, UI reviews and the Playwright L2 suite. The backend still requires a
real JWT: every API call of a demo build carries the fake token `demo-token` and is rejected (401) unless a mock
answers it (Playwright `page.route`).

## What the frontend does

| Concern | Behaviour | Code |
|---|---|---|
| When demo mode is on | `VITE_DEMO_MODE=true` **and** either the Vite dev server (`npm run dev:demo`, Playwright) or a bundle built in Vite mode `demo` (`npm run build:demo`) | `src/config/demo.config.ts` (`isDemoMode`) |
| Normal build with the variable | `npm run build` (mode `production`) with `VITE_DEMO_MODE=true` in the environment or in a `.env*` file **fails** with an explicit error: a leaked variable can never ship an app without login | `vite.config.ts` → `src/config/demo-build-guard.ts` |
| Demo build on Vercel Production | `npm run build:demo` fails when `VERCEL_ENV=production` | same |
| Demo build | `npm run build:demo` = `tsc -b && cross-env VITE_DEMO_MODE=true vite build --mode demo` (before PL-01 the variable reached only `tsc`, so the bundle was a normal one that asked for the login) | `package.json` |
| Profile (`GET /users/me`) | Tried first (Playwright mocks it); when it fails (401 with the demo token, API unreachable) the profile of the persona is used: org and completed onboarding for the host and supplier personas, no org for `admin` and `onboarding`. No loop between the guard and `/onboarding` | `src/queries/use-users.ts` (`useMe`), `src/lib/demo-profile.ts` |
| Onboarding in demo | The wizard does not call the API: the chosen rental type switches the persona (session storage) and the page reloads on its home | `src/features/onboarding/onboarding-page.tsx`, `src/lib/demo-onboarding.ts` |

## Vercel

1. **Production and Preview of the main project** (`casazen-app`): do **not** set `VITE_DEMO_MODE`, or set it to
   `false`. With `true` the build now fails on purpose (message: `VITE_DEMO_MODE=true in a normal build`). Build
   command stays `npm run build` (`vercel.json`).
2. **Optional demo deployment**, for presentations: a separate Vercel project (for example `casazen-demo`) on the same
   repository, never promoted to the production domain.
   - Settings → Build & Development → Build Command: `npm run build:demo` (overrides `vercel.json`).
   - Environment variables (Preview and Production of **that** project): `VITE_DEMO_PROFILE` (optional, e.g.
     `short-stay`), `VITE_API_BASE_URL` pointing at the **test** API (never the production API),
     `VITE_AUTH0_*` not needed. `VITE_DEMO_MODE` is set by the script: no need to add it.
   - Deploy it as a Preview deployment: a demo build refuses to run on `VERCEL_ENV=production`. If a stable URL is
     needed, assign a custom domain to the Preview branch of the demo project instead of promoting it.

## Check after a deploy

1. Main project: open the site in a private window → the Auth0 login page appears (no yellow demo banner).
2. Demo deployment: the yellow demo banner is visible; `/?demoProfile=short-stay` opens the short-rent dashboard,
   `/?demoProfile=admin` the admin area, `/?demoProfile=onboarding` the onboarding wizard. None of them bounces back to
   `/onboarding` in a loop.
3. Local check of the guard: `npm run build` with `VITE_DEMO_MODE=true` exported in the shell must fail;
   `npm run build:demo` must succeed.
