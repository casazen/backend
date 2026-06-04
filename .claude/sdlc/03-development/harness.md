# Stage 03: Development — Quality Harness

## Entry Criteria

- `Sessions/design-<issue-N>.md` exists and all Stage 02 gates passed
- Branch `feature/<issue-N>-<slug>` created from **`develop`** in each affected repo
- No uncommitted changes from previous work on this branch

## Council Run

Coordinator spawns: `backend-developer`, `frontend-developer`, `test-engineer` (**all three, always**)

Topic handed to council:
> "Implement Issue #N per spec Sessions/design-<issue-N>.md on branch feature/<issue-N>-<slug> in **both** casazen/backend and casazen/frontend. Follow TDD (Red → Green → Refactor): write a failing test before each production code unit. Backend first when API changes exist. Run all quality gates in both repos and open PR(s) targeting develop when all pass."

**TDD rule (non-negotiable):** every service method, repository method, controller action, query hook, and non-trivial component must have a failing test written before its production code. Skipping the Red phase is a harness violation equivalent to a failing gate.

## Quality Gates

All applicable gates must pass before exiting. Mark N/A only when the design spec explicitly scopes a layer out **and** the specialist confirms zero file changes.

### Backend gates (repo: `casazen/backend`)

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | Backend tests pass | `dotnet test` | All tests pass, 0 failures |
| G2 | Code format clean | `dotnet format --verify-no-changes` | Exit code 0 |
| G3 | No compiler warnings | `dotnet build /warnaserror` | Exit code 0 |
| G4 | Migration compiles | `dotnet ef migrations script --project Casazen.Infrastructure` | Exit code 0 if schema changed; N/A otherwise |

### Frontend gates (repo: `casazen/frontend`)

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G5 | Frontend unit tests pass | `npm test` | All Vitest tests pass |
| G6 | TypeScript clean | `tsc -b --noEmit` | Exit code 0 |
| G7 | Lint clean | `npm run lint` | Exit code 0, 0 errors |
| G8 | Build succeeds | `npm run build` | Exit code 0 |
| G9 | E2E coverage table complete | See E2E Coverage Phase below | AC→E2E mapping table produced; every AC marked ✅ or `N/A (no UI)` |
| G10 | E2E tests pass | `npm run test:e2e` (in `../frontend`) | All Playwright tests pass; 0 failures |

### E2E Coverage Phase (mandatory — runs before G10, after G5–G8)

This is a **discrete council step**, not a background task. `test-engineer` must:

1. Read every AC in the Issue / design spec.
2. For **each AC that has a UI surface** (new page, new component, new route, new form action):
   - Create or extend a Playwright spec in `e2e/` with a `test()` that:
     - Navigates to the relevant route (demo mode or with `page.route()` mocks)
     - Asserts the page renders without exception (no error-boundary fallback, no console error with stack trace)
     - Asserts the critical UI element is visible (table row, button, form field, KPI card, etc.)
3. For ACs with no UI surface (backend-only, job, migration): mark `N/A (no UI)` — still required to appear in the table.
4. Produce the **AC→E2E Coverage Table** and include it in the PR body:

```
| AC | Description | E2E file | Test name | Status |
|---|---|---|---|---|
| AC1 | Admin sees stats KPIs | e2e/admin.spec.ts | AC1 /admin renders KPI cards | ✅ |
| AC10 | CIN compliance table | e2e/admin-cin.spec.ts | AC10 /admin/cin renders table | ✅ |
| AC7 | Role change syncs to Auth0 | N/A (no UI) | — | N/A |
```

**G9 fails if any UI-bearing AC has no row in this table.** The coordinator must verify the table before marking G9 ✅.

### Compliance gates (both repos)

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G11 | CIN unit test | `dotnet test --filter CinCode` | Passes if Property entity modified; else N/A |
| G12 | No secrets committed | `git status` + grep in both repos | No `.env`, secrets, or real keys in staged files |
| G13 | GDPR fields present | Read modified Guest code | Required if Guest touched; else N/A |
| G14 | Tourist tax not hardcoded | `grep` in `Casazen.Core` | No hardcoded amounts; else N/A |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any applicable gate in G1–G14 fails) AND (iteration < max_iterations):
  1. Coordinator lists failing gates with exact error output per repo
  2. Route: G1–G4 → backend-developer, G5–G8 → frontend-developer, G9 (coverage table) + G10 (E2E run) + G11–G14 → test-engineer
  3. Specialist implements fix
  4. Re-run failed gates (plus dependencies)
  5. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE — do NOT open PR with failing gates
```

## Exit Artifact

One open PR per repo with changes, **base branch = `develop`**:

```bash
# Backend (if changes)
gh pr create --base develop --repo casazen/backend \
  --title "feat(<area>): <description> (#N)" \
  --body "## Summary\n...\n\n## Frontend PR\n<URL or N/A>\n\n## Test Plan\n...\n\nCloses #N"

# Frontend (if changes)
gh pr create --base develop --repo casazen/frontend \
  --title "feat(<area>): <description> (#N)" \
  --body "## Summary\n...\n\n## Backend PR\n<URL or N/A>\n\n## Test Plan\n...\n\nCloses casazen/backend#N"
```

PR body must include:
- `## Summary` — BE + FE changes (or explicit N/A per layer)
- `## Test Plan` — how to verify full-stack behaviour on develop after merge
- `## AC→E2E Coverage Table` — every AC from the Issue listed; each UI-bearing AC maps to an `e2e/*.spec.ts` file + test name; backend-only ACs marked `N/A (no UI)`. **This table is required for G9 to pass.**
- Cross-repo PR link
- Gate status table (all ✅, including G9 + G10 when FE touched)
- `Closes #N`

**test-engineer rule**: the E2E Coverage Phase is a **mandatory council step** — not optional, not skippable. For every AC that has a UI surface, there must be a Playwright test that (a) navigates to the route and (b) asserts the page renders without exception before Stage 03 exits.

## Handoff to Stage 04

Pass to review stage:
- Issue `#N`
- `pr_backend` / `pr_frontend` numbers and URLs (either may be N/A)
- Design spec path
