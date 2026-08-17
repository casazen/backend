# D-AC15

## Iteration 1 — dev
- `frontend/.github/workflows/e2e-golden-journey.yml` runs `golden-journey-web.spec.ts` on every PR/push to main/develop; Maestro job on main, nightly, or `e2e-app`.
- `backend/.github/workflows/e2e-golden-journey.yml` is the spec-named pointer.

## Review
STATO: APPROVED
AC15 named workflow exists. Web suite on PR. App suite gated as specified. Maestro job is a documented placeholder until mobile is checked out in CI (not a silent no-op of the web suite).
