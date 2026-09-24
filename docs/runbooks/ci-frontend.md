# Runbook: frontend CI (casazen/frontend, casazen/mobile)

Workflows in `casazen/frontend/.github/workflows/` (task FD-01, audit defects A9-23, A9-24/A6-04 frontend part).

| Workflow | Job (check name) | Trigger | Gate |
|---|---|---|---|
| `ci.yml` | `Lint, typecheck, unit tests, build` | PR and push to `develop` / `main` | **Required**: `npm ci`, `npm run lint` (0 errors), `npm run typecheck`, `npm test` (vitest), `npm run build` |
| `e2e.yml` | `E2E L2 (demo)` | PR, push `develop` / `main`, nightly | Playwright L2 demo suite (`page.route` mocks) |
| `e2e.yml` | `E2E staging smoke + GJ` | push `develop`, nightly, manual | L3 against the Railway test API; skipped with a warning when `E2E_AUTH0_EMAIL` is not set |
| `e2e-golden-journey.yml` | `GJ web suite` | PR, push `develop` / `main`, nightly | Golden Journey L2 demo; no `continue-on-error`, no placeholder jobs |

Not in CI yet: the real Golden Journey L3 from the UI on an ephemeral stack (task FN-03) and the Maestro app suite on an emulator (task FN-04). The former Maestro job only ran `echo` and was removed so it can no longer show a fake green check.

## 1. Make the CI check required (one-time, repo admin)

GitHub → `casazen/frontend` → **Settings → Branches** (or **Rules → Rulesets**), for **both** `develop` and `main`:

1. Add or edit the protection rule for the branch.
2. Enable **Require status checks to pass before merging** and **Require branches to be up to date before merging**.
3. Search and add the check **`Lint, typecheck, unit tests, build`** (it appears after the workflow has run at least once on a PR).
4. Save.

Same thing from the CLI, when branch protection already exists on the branch:

```bash
for b in develop main; do
  gh api -X PATCH "repos/casazen/frontend/branches/$b/protection/required_status_checks" \
    -F strict=true -f 'contexts[]=Lint, typecheck, unit tests, build'
done
```

Add `E2E L2 (demo)` and `GJ web suite` to the required checks only after they have been green on `develop` for a few runs: before FD-01, `e2e.yml` was rejected by GitHub at parse time (`secrets.*` inside a step `if:`): all its runs failed with 0 jobs, so the L2 suite never actually ran in CI and its current state on a runner is unknown.

## 2. Secrets and variables for `E2E staging smoke + GJ`

GitHub → `casazen/frontend` → **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
|---|---|---|
| Secret | `E2E_AUTH0_EMAIL` | Auth0 test user on the **test** tenant (never a real customer) |
| Secret | `E2E_AUTH0_PASSWORD` | Password of that user |
| Variable | `E2E_STAGING_API_URL` | Railway test API, e.g. `https://<test-service>.up.railway.app/api` (default in the workflow) |
| Variable | `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE` | Test-tenant SPA values |

The secrets are exposed as job-level `env` and the steps test `env.E2E_AUTH0_EMAIL != ''`: the `secrets` context is not allowed in `steps[*].if`.

## 3. Verify

1. Open a PR against `develop`: the checks list shows `Lint, typecheck, unit tests, build`, `E2E L2 (demo)` and `GJ web suite`, each with real jobs (not "0 jobs").
2. Locally, the same commands as CI: `npm ci && npm run lint && npm run typecheck && npm test && npm run build`.
3. Optional workflow syntax check: `pip install actionlint-py && actionlint .github/workflows/*.yml` (reports context errors such as `secrets` in a step `if:`).

## 4. Mobile app (casazen/mobile)

Workflow `casazen/mobile/.github/workflows/ci.yml` (task FD-03, audit defects A9-24 mobile part and A6-30 CI part), on PR and push to `develop` / `main`. One job, check name **`Typecheck, lint, unit tests, bundle`**:

| Step | Command |
|---|---|
| Install | `npm ci` |
| Typecheck | `npm run typecheck` (`tsc --noEmit`) |
| Lint | `npm run lint` (`eslint .`, flat config `eslint-config-expo/flat` + `eslint-plugin-react-hooks`, 0 errors) |
| Unit tests | `npm test -- --ci` (jest, preset `jest-expo`) |
| Bundle | `npx expo export --platform android --output-dir dist` (Metro + Hermes bytecode, no native build, no EAS account) |

Make it required on `develop` and `main` as in section 1, with the check name above:

```bash
for b in develop main; do
  gh api -X PATCH "repos/casazen/mobile/branches/$b/protection/required_status_checks" \
    -F strict=true -f 'contexts[]=Typecheck, lint, unit tests, bundle'
done
```

No secrets or variables are needed. The bundle is built without `EXPO_PUBLIC_*`, so it only proves that the app bundles; it is not a release artifact. Not in CI yet: native / EAS builds, and the Maestro flows on an emulator (task FN-04).

Mobile-specific notes (details in `casazen/mobile/README.md`, section CI):

- Keep `"lint": "eslint ."`. `expo lint` on SDK 52 does not see the flat config and would generate a legacy `.eslintrc.js`.
- `expo-asset` must stay a direct dependency: otherwise npm nests it under `expo/` and `expo export` / `expo start` fail with "The required package `expo-asset` cannot be found".

## Rules for future changes

- `npm run lint` must stay at 0 errors. Disable a rule only on a single line with a comment that explains why.
- Do not add `continue-on-error: true` or `echo`-only jobs to gate workflows: a check that cannot fail is not a gate.
