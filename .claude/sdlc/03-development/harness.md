# Stage 03: Development — Quality Harness

## Entry Criteria

- `Sessions/design-<issue-N>.md` exists and all Stage 02 gates passed
- Branch `feature/<issue-N>-<slug>` created from **`develop`** in each affected repo
- No uncommitted changes from previous work on this branch
- **Local backend ready for E2E**: `.\scripts\start-backend-local.ps1` running on `http://localhost:5000` (starts backend with InMemory DB — zero remote dependencies)

## Council Run

Coordinator spawns: `backend-developer`, `frontend-developer`, `test-engineer` (**all three, always**)

Topic handed to council:
> "Implement Issue #N per spec Sessions/design-<issue-N>.md on branch feature/<issue-N>-<slug> in **both** casazen/backend and casazen/frontend. Backend first when API changes exist. Run all quality gates in both repos and open PR(s) targeting develop when all pass."

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
| G9 | E2E tests pass (local backend, AC-driven) | `npm run test:e2e:local` (in `../frontend`) | All Playwright tests pass against local .NET backend (InMemory DB); **must include specs mapped to Issue ACs** from design spec. Backend must be running via `.\scripts\start-backend-local.ps1` before executing this gate. |

### Compliance gates (both repos)

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G10 | CIN unit test | `dotnet test --filter CinCode` | Passes if Property entity modified; else N/A |
| G11 | No secrets committed | `git status` + grep in both repos | No `.env`, secrets, or real keys in staged files |
| G12 | GDPR fields present | Read modified Guest code | Required if Guest touched; else N/A |
| G13 | Tourist tax not hardcoded | `grep` in `Casazen.Core` | No hardcoded amounts; else N/A |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any applicable gate in G1–G13 fails) AND (iteration < max_iterations):
  1. Coordinator lists failing gates with exact error output per repo
  2. Route: G1–G4 → backend-developer, G5–G8 → frontend-developer, G9–G13 → test-engineer
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
- `## Acceptance criteria coverage` — table mapping each Issue AC → unit/integration/E2E test file
- Cross-repo PR link
- Gate status table (all ✅, including G9 E2E when FE touched — runs locally, NOT in CI)
- `Closes #N`

**test-engineer rule**: for every acceptance criterion in the Issue/design spec, add or extend at least one automated test (Vitest or Playwright E2E) before Stage 03 exits. E2E specs live in `e2e/` and run against the local backend (`npm run test:e2e:local`) with the backend started via `.\scripts\start-backend-local.ps1`. This gives real frontend↔backend integration without remote dependencies.

## Handoff to Stage 04

Pass to review stage:
- Issue `#N`
- `pr_backend` / `pr_frontend` numbers and URLs (either may be N/A)
- Design spec path
