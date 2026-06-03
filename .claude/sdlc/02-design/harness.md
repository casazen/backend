# Stage 02: Design — Quality Harness

## Entry Criteria

- GitHub Issue `#N` from Stage 01 is open and fully structured (all gates G1–G5 passed)
- No `Sessions/design-<issue-N>.md` file exists yet (or it is incomplete)

## Council Run

Coordinator spawns: `api-designer`, `frontend-designer`, `security-by-design`

Topic handed to council:
> "Produce a complete design spec at Sessions/design-<issue-N>.md for Issue #N: [issue title]. Cover API contract, frontend flow, and security model."

## Quality Gates

All gates must pass before exiting.

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | Spec file exists | `Test-Path Sessions/design-<issue-N>.md` | File exists and non-empty |
| G2 | API contract complete | Read spec `## API Contract` section | Every new/changed endpoint has method, path, request schema, response schema, auth requirement |
| G3 | Every endpoint has auth decision | Read spec `## API Contract` | Each endpoint specifies `[Authorize]` or explicit public justification |
| G4 | Frontend flow defined | Read spec `## Frontend Flow` section | Route changes and component breakdown described |
| G5 | `<ProtectedRoute>` specified | Read spec `## Frontend Flow` | Every new authenticated route marked with `<ProtectedRoute>` |
| G6 | Security notes present | Read spec `## Security Notes` section | OTA keys in config, PII data flow, threat summary |
| G7 | Migration plan included | Read spec `## Migration Plan` section | Present if schema changes; `N/A — no schema changes` if not |
| G8 | GDPR scope stated | Read spec `## GDPR Scope` section | Present if Guest data involved; `N/A` if not |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G8 fails) AND (iteration < max_iterations):
  1. Coordinator identifies failed gates with specific missing content
  2. Assigns specialists: api-designer → G2/G3/G7, frontend-designer → G4/G5, security-by-design → G3/G6/G8
  3. Specialists update spec file
  4. Re-check all failed gates
  5. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE: add escalation block to spec file
  Human decision required before proceeding to development
```

## Exit Artifact

`Sessions/design-<issue-N>.md` with sections:
- `## API Contract` — full endpoint table
- `## Frontend Flow` — route + component plan
- `## Security Notes` — auth gates, PII, OTA keys
- `## Migration Plan` — EF Core changes or N/A
- `## GDPR Scope` — affected Guest fields or N/A
- `## Open Questions` — empty (all resolved) or with answers

## Handoff to Stage 03

Pass to development with:
- Issue number: `#N`
- Spec file: `Sessions/design-<issue-N>.md`
- Branch name to create: `feature/<issue-N>-<slug>`
