# Stage 03: Development — Quality Harness

## Entry Criteria

- `Sessions/design-<issue-N>.md` exists and all Stage 02 gates passed (including **G9 AC Test Map**)
- Branch `feature/<issue-N>-<slug>` created from **`develop`** in each affected repo
- No uncommitted changes from previous work on this branch
- **Local backend ready for L3 E2E**: `.\scripts\quality\run-l3-local.ps1` (or `.\scripts\start-backend-local.ps1` on `http://localhost:5000`)

## Council Run

Coordinator spawns: `backend-developer`, `frontend-developer`, `test-engineer` (**always**), and `mobile-developer` when design scopes `casazen/mobile`.

Topic handed to council:
> "Implement Issue #N per spec Sessions/design-<issue-N>.md on branch feature/<issue-N>-<slug>. Pass L1 + L2 + L3 gates. Open PR(s) targeting develop. Do NOT close the GitHub issue."

## Quality Gates

All applicable gates must pass before exiting. Mark N/A only when the design spec explicitly scopes a layer out **and** the specialist confirms zero file changes.

### Backend gates (repo: `casazen/backend`) — L1

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | Backend tests pass | `dotnet test` | All tests pass, 0 failures |
| G2 | Code format clean | `dotnet format --verify-no-changes` | Exit code 0 |
| G3 | No compiler warnings | `dotnet build /warnaserror` | Exit code 0 |
| G4 | Migration compiles | `dotnet ef migrations script --project Casazen.Infrastructure` | Exit code 0 if schema changed; N/A otherwise |

### Frontend gates (repo: `casazen/frontend`) — L1

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G5 | Frontend unit tests pass | `npm test` | All Vitest tests pass |
| G6 | TypeScript clean | `tsc -b --noEmit` | Exit code 0 |
| G7 | Lint clean | `npm run lint` | Exit code 0, 0 errors |
| G8 | Build succeeds | `npm run build` | Exit code 0 |

### E2E / quality gates — L2 + L3

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G9a | L2 demo UI contract | `cd ../frontend && npm run test:e2e -- <L2 specs from AC Test Map>` | All listed L2 specs pass (demo + `page.route` OK) |
| G9b | L3 real API | `.\scripts\quality\run-l3-local.ps1 -SpecFilter "<L3 specs>"` | All L3 specs pass against local .NET InMemory; **no `page.route` on paths under test** |
| G9c | Anti-stub | `.\scripts\quality\check-no-shipped-stubs.ps1` | Exit 0 — no shipped-path stubs/TODO Implement / silent skips outside allowlist |
| G9d | Mobile Maestro | `cd ../mobile && maestro test e2e/` | When mobile in scope: all M* flows pass with **non-optional** asserts; N/A otherwise |
| G9e | AC matrix present | `.\scripts\quality\check-ac-matrix.ps1 -DesignPath Sessions/design-<N>.md -PrBodyPath <draft>` | Design + PR body AC tables complete; **paths exist** |
| G9f | Contract check | Skill `sdlc-contract-check` → `Sessions/pipeline-<slug>/contract-check.md` | Overall PASS; FE client aligned when FE diff non-empty |
| G9g | L3 hard for UI | Evidence that every UI AC L3/Maestro path from AC Test Map was executed | L2-only exit is **FAIL**; N/A only if `git diff --name-only` shows zero FE/mobile files |

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
  2. Route: G1–G4 → backend-developer, G5–G8 → frontend-developer,
     G9a–G9g → test-engineer (+ mobile-developer for G9d), G10–G13 → test-engineer
  3. Specialist implements fix
  4. Re-run failed gates via **sdlc-gate-runner** (write evidence); never self-certify
  5. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE via sdlc-escalate — do NOT open PR with failing gates
```

**N/A rule (enforced):** any gate marked N/A requires empty `git diff --name-only` for that layer (BE/FE/mobile).

## Exit Artifact

One open PR per repo with changes, **base branch = `develop`**.

**Do NOT close Issue `#N`.** Use `Refs #N` (not `Closes #N`) until Stage 05 Phase B passes.

PR body must include:

```markdown
## Summary
...

## AC Test Map
| AC | L1 | L2 | L3 | Status |
|---|---|---|---|---|
| AC1 | ... | ... | ... | ✅ |

## Gate status
| Gate | Status |
| G1–G8 | ✅ |
| G9a L2 | ✅ |
| G9b L3 | ✅ |
| G9c anti-stub | ✅ |
| G9d mobile | N/A or ✅ |

## Cross-repo
Backend PR: ...
Frontend PR: ...
Mobile PR: ...

Refs #N
```

**test-engineer rule**: every AC in the Issue/design AC Test Map must have L1 and (for UI) L2 + L3 automated coverage before Stage 03 exits. Vacuous tests (conditional no-op, optional asserts on critical path) are failures.

## Handoff to Stage 04

Pass to review stage:
- Issue `#N` (still open)
- `pr_backend` / `pr_frontend` / `pr_mobile` numbers and URLs
- Design spec path with AC Test Map
