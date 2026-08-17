# Fix plan — attempt 1

Order (dependencies first). No code in this file.

1. **D-AC16** (backend) — add `Sessions/golden-journey-runbook.md`. No deps.
2. **D-AC15** (frontend + backend pointer) — add `.github/workflows/e2e-golden-journey.yml` that runs the web GJ suite on PR. App/Maestro job gated on label `e2e-app`.
3. **D-AC1 + D-AC5** (frontend) — add real-API 12-step path in `golden-journey-web.spec.ts` (unique emails/slugs; no `page.route` when `E2E_LOCAL=1`). Include in Playwright `local` project.
4. **D-AC13** (frontend) — add `golden-journey-supplier-mobile.spec.ts` F1–F2 on real API / shared seed.
5. **D-AC14** (frontend) — assert booking + service-request `status` parity after steps 7–10 in the L3 journey.
6. **D-AC6** (mobile) — point M1–M7 yaml at real API (`EXPO_PUBLIC_API_URL`), drop demo-only as the sole path. Live Maestro run stays environment-blocked until `maestro` exists (**D-M-LIVE**).

Repos: backend (runbook, optional workflow), frontend (Playwright + CI), mobile (Maestro yaml).
