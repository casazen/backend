# D-AC1 + D-AC5

## Iteration 1 — dev
`frontend/e2e/golden-journey-web.spec.ts`: L2 demo skipped when `E2E_LOCAL=1`; new L3 describe walks steps 1–12 against `http://localhost:5000/api` with unique `gj-{timestamp}` emails/slugs and no `page.route`. Local Playwright project includes this file.

## Review
STATO: APPROVED
Real-API path exists in the spec-named file. Unique slugs (AC5). L2 demo retained for CI without Auth0. Steps 8–9 remain auth-gated (401) without a supplier token — documented, not mocked.
