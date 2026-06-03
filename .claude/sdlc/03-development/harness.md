# Stage 03: Development — Quality Harness

## Entry Criteria

- `Sessions/design-<issue-N>.md` exists and all Stage 02 gates passed
- Branch `feature/<issue-N>-<slug>` created from `main`
- No uncommitted changes from previous work on this branch

## Council Run

Coordinator spawns: `backend-developer`, `frontend-developer`, `test-engineer`

Topic handed to council:
> "Implement Issue #N per spec Sessions/design-<issue-N>.md on branch feature/<issue-N>-<slug>. Run all quality gates and open a PR when all pass."

## Quality Gates

All gates must exit 0 (or pass their check condition) before exiting.

### Backend gates

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | Backend tests pass | `dotnet test` | All tests pass, 0 failures |
| G2 | Code format clean | `dotnet format --verify-no-changes` | Exit code 0 |
| G3 | No compiler warnings | `dotnet build /warnaserror` | Exit code 0 |
| G4 | Migration compiles | `dotnet ef migrations script --project Casazen.Infrastructure` | Exit code 0 (run only if schema changed) |

### Frontend gates

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G5 | Frontend unit tests pass | `npm test` | All Vitest tests pass |
| G6 | TypeScript clean | `tsc -b --noEmit` | Exit code 0 |
| G7 | Lint clean | `npm run lint` | Exit code 0, 0 errors |
| G8 | Build succeeds | `npm run build` | Exit code 0 |

### Compliance gates (manual verification)

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G9 | CIN unit test | `dotnet test --filter CinCode` | Passes if Property entity modified |
| G10 | No secrets committed | `git status` + grep check | No `appsettings.Development.json` or `.env` in staged files |
| G11 | GDPR fields present | Read modified entity code | `ErasureRequested` + `DataRetentionUntil` present if Guest modified |
| G12 | Tourist tax not hardcoded | `grep -r "tourist" Casazen.Core --include="*.cs"` | No hardcoded tax amounts; uses `TouristTaxRate` entity |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G12 fails) AND (iteration < max_iterations):
  1. Coordinator lists all failing gates with exact error output
  2. Route to specialist: G1–G4 → backend-developer, G5–G8 → frontend-developer, G9–G12 → test-engineer
  3. Specialist implements fix
  4. Re-run ONLY the gates that previously failed (plus their dependencies)
  5. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE: create issue comment with gate status table
  Human decision required — do NOT open PR with failing gates
```

## Exit Artifact

Branch `feature/<issue-N>-<slug>` with open PR:

```bash
gh pr create --base develop \
  --title "feat(<area>): <description> (#N)" \
  --body "## Summary\n...\n\n## Test Plan\n...\n\nCloses #N"
```

PR body must include:
- `## Summary` — what changed and why
- `## Test Plan` — how to verify
- `Closes #N`
- Gate status table (all ✅)

## Handoff to Stage 04

Pass PR number to review stage.
