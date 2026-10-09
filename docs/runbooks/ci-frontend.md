# Runbook: frontend CI (casazen/frontend, casazen/mobile)

Workflows in `casazen/frontend/.github/workflows/` (task FD-01, audit defects A9-23, A9-24/A6-04 frontend part).

| Workflow | Job (check name) | Trigger | Gate |
|---|---|---|---|
| `ci.yml` | `Lint, typecheck, unit tests, build` | PR and push to `develop` / `main` | **Required**: `npm ci`, `npm run lint` (0 errors), `npm run typecheck`, `npm run knip` (dead code), `npm test` (vitest), `npm run build` |
| `e2e.yml` | `E2E L2 (demo)` | PR, push `develop` / `main`, nightly | Playwright L2 demo suite (`page.route` mocks) |
| `e2e.yml` | `E2E staging smoke + GJ` | push `develop`, nightly, manual | L3 against the Railway test API; skipped with a warning when `E2E_AUTH0_EMAIL` is not set |
| `e2e-golden-journey.yml` | `GJ web suite` | PR, push `develop` / `main`, nightly | Golden Journey L2 demo shell checks (mocks); no `continue-on-error`, no placeholder jobs |
| `e2e-golden-journey.yml` | `GJ L3 (UI, ephemeral stack)` | PR, push `develop` / `main`, nightly, manual | **The Golden Journey gate** (FN-03): admin, host, supplier (phone, F1-F2) and guest driven from the UI against a real backend + throw-away PostgreSQL + mock IdP/mail; Stripe test mode with the secrets, otherwise that variant is SKIPPED loudly. [golden-journey-l3.md](golden-journey-l3.md) |

Not in CI yet: the Maestro app suite on an emulator (task FN-04). The real Golden Journey L3 from the UI on an ephemeral stack is the job `GJ L3 (UI, ephemeral stack)` (FN-03, secrets `BACKEND_REPO_TOKEN`, `STRIPE_TEST_SECRET_KEY`, `STRIPE_TEST_PUBLISHABLE_KEY`: see golden-journey-l3.md section 3). The former Maestro job only ran `echo` and was removed so it can no longer show a fake green check.

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

Add `E2E L2 (demo)`, `GJ web suite` and `GJ L3 (UI, ephemeral stack)` to the required checks only after they have been green on `develop` for a few runs: before FD-01, `e2e.yml` was rejected by GitHub at parse time (`secrets.*` inside a step `if:`): all its runs failed with 0 jobs, so the L2 suite never actually ran in CI and its current state on a runner is unknown.

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

No secrets or variables are needed. The bundle is built without `EXPO_PUBLIC_*`, so it only proves that the app bundles; it is not a release artifact. It also fails when the bundle contains the dev-only auth shortcuts or the values of the Maestro E2E build. Not in CI: native / EAS builds.

**Maestro on an emulator** (task FN-04) is a second workflow, `e2e-android.yml`, check name **`Maestro on Android emulator`**: real login, flows by `testID`, ephemeral backend. It needs two secrets and runs on PRs to `main`, on PRs labelled `e2e-app`, on pushes to `main` and nightly. Setup, secrets and what is and is not proven: [`mobile-e2e.md`](mobile-e2e.md).

Mobile-specific notes (details in `casazen/mobile/README.md`, section CI):

- Keep `"lint": "eslint ."`. `expo lint` on SDK 52 does not see the flat config and would generate a legacy `.eslintrc.js`.
- `expo-asset` must stay a direct dependency: otherwise npm nests it under `expo/` and `expo export` / `expo start` fail with "The required package `expo-asset` cannot be found".

## Rules for future changes

- `npm run lint` must stay at 0 errors. Disable a rule only on a single line with a comment that explains why.
- Do not add `continue-on-error: true` or `echo`-only jobs to gate workflows: a check that cannot fail is not a gate.

## 5. Dead code: knip (frontend)

Task FN-02 (audit defect A9-33). `npm run knip` is a **blocking** step of `ci.yml` (between typecheck and the tests): it fails on unused files, unused exports, unused dependencies and unlisted binaries. The sweep that introduced it removed the unreachable components (OTA, payments and properties lists, layer switcher and guards, login/logout buttons, tourist-tax page...), the dead hooks, the unused npm dependencies and the Vite template CSS; the baseline is 0 problems.

Rules for new code:

- Anything exported must be used: by the app (routes are reached from `src/main.tsx` through `ROUTE_MANIFEST`, whose entries use lazy `import()`), by a test, or by an e2e spec. A new file that nothing imports fails the check: wire it up or do not add it.
- Code behind a feature flag (`src/config/feature-flags.ts`, `featureFlag:` in the manifest) is reachable, so it is not reported. Do not delete it as dead.
- A symbol used only inside its own file loses `export` (knip `ignoreExportsUsedInFile` hides that case, it is not an error).
- Do **not** add a dependency "for later": an unused `dependencies` entry fails the check.

Configuration (`knip.json`) and the motivated ignores:

| Setting | Why |
|---|---|
| `entry`: `api/*.ts`, `scripts/*.mjs`, `e2e/auth.setup.ts` | Vercel functions, Node scripts and the Playwright setup (selected by a regex in `playwright.config.ts`, which knip cannot evaluate). `src/main.tsx`, `*.test.*`, `*.spec.ts`, `vite.config.ts` and `playwright.config.ts` are entries through the knip plugins (Vite, Vitest, Playwright). |
| `ignoreIssues` `src/components/ui/**` (exports, types) | shadcn/Radix primitives: the full set of parts is exported on purpose, even those not used yet. |
| `ignoreIssues` `src/types/**` (types) | DTO types mirror the backend contract; an unused field type is documentation of the API, not dead code. Unused exported *values* in `src/types` are still reported. |
| `ignoreBinaries`: `psql` | Used by `e2e/gj-l3/stack.ts` against the ephemeral stack, installed on the runner and on dev machines, not an npm package. |
| `compilers.css` | Lets knip follow `.css` imports, so `src/styles/*.css` are reachable and an orphan stylesheet is reported. |

Dynamic loading: `import.meta.glob` is only used by `src/i18n/i18n.test.ts` (reads the sources of the app), so it does not hide dead files; lazy `import('@/...')` calls in `route-manifest.ts` are followed by knip. If a file is loaded by a computed path (a string built at run time), add it to `entry` with a comment here.

Limit: tests are entries, so a file used **only by its own test** is not reported (a dead component with a test looks alive). To find those, run `npx knip --production` and read the "Unused files" list by hand: `src/test/*`, `src/features/leases/__tests__/lease-test-utils.tsx`, `src/config/robots-txt.ts` and `src/config/vercel-build-env.ts` are expected false positives (test setup and `vite.config.ts` helpers, which production mode does not trace).

Locally: `npm run knip`. Remedy for a finding: delete the code (and its test); if it is really needed, wire it to a route or a caller; ignore only with a reason added to the table above.
