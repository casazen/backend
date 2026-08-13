# Stage 02: Design — Quality Harness

## Entry Criteria

- GitHub Issue `#N` from Stage 01 is open and fully structured (all gates G1–G5 passed)
- No `Sessions/design-<issue-N>.md` file exists yet (or it is incomplete)

## Council Run

Coordinator spawns: `api-designer`, `frontend-designer`, `security-by-design`

Topic handed to council:
> "Produce a complete design spec at Sessions/design-<issue-N>.md for Issue #N: [issue title]. Cover API contract, frontend flow, security model, and AC Test Map (L1/L2/L3)."

## Quality Gates

All gates must pass before exiting.

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | Spec file exists | `Test-Path Sessions/design-<issue-N>.md` | File exists and non-empty |
| G2 | API contract complete | Read spec `## API Contract` section | Every new/changed endpoint has method, path, request schema, response schema, auth requirement |
| G3 | Every endpoint has auth decision | Read spec `## API Contract` | Each endpoint specifies `[Authorize]` or explicit public justification |
| G4 | Frontend flow defined | Read spec `## Frontend Flow` section | Route changes and component breakdown described (or N/A with BE-only justification) |
| G5 | `<ProtectedRoute>` specified | Read spec `## Frontend Flow` | Every new authenticated route marked with `<ProtectedRoute>` (or Expo session gate for mobile) |
| G6 | Security notes present | Read spec `## Security Notes` section | OTA keys in config, PII data flow, threat summary |
| G7 | Migration plan included | Read spec `## Migration Plan` section | Present if schema changes; `N/A — no schema changes` if not |
| G8 | GDPR scope stated | Read spec `## GDPR Scope` section | Present if Guest data involved; `N/A` if not |
| G9 | AC Test Map complete | `.\scripts\quality\check-ac-matrix.ps1 -DesignPath Sessions/design-<N>.md` (path-exists enabled) | **Hard fail without `## AC Test Map`.** Every Issue AC has a row with REQ-ID (`SPEC:<slug>:ACn` and/or `ADR-00N-Rk` in AC column or Notes). UI ACs must name L2 **and** L3 files that **exist** (or Maestro paths). `N/A — non UI` only for non-UI ACs. Gate PASS only via `sdlc-gate-runner` evidence. |
| G9b | Spec verifiable outcomes | `.\scripts\quality\check-ac-depth.ps1 -SpecPath Sessions/specs/spec-<slug>.md` | Spec has `## Verifiable Outcomes` for every ACn; export/report ACs have `## Export / Report Criteria`; FE ACs have `## UX / UI Quality`. Exit 0. |
| G10 | Spec/ADR linkage | Read design header | Design cites `Sessions/specs/spec-…` and any informing `docs/adr/ADR-…`; ACs align to those REQ-IDs |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G10 fails) AND (iteration < max_iterations):
  1. Coordinator identifies failed gates with specific missing content
  2. Assigns specialists: api-designer → G2/G3/G7, frontend-designer → G4/G5/G9/G9b/G10, security-by-design → G3/G6/G8
  3. Specialists update spec file
  4. Re-check failed gates via **sdlc-gate-runner** (not narrative)
  5. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE via sdlc-escalate — do not hand off to Stage 03
```

## Exit Artifact

`Sessions/design-<issue-N>.md` with sections:
- `## API Contract` — full endpoint table
- `## Frontend Flow` — route + component plan (or mobile route map)
- `## Security Notes` — auth gates, PII, OTA keys
- `## Migration Plan` — EF Core changes or N/A
- `## GDPR Scope` — affected Guest fields or N/A
- `## AC Test Map` — required table (see template below)
- `## Open Questions` — empty (all resolved) or with answers

### AC Test Map template

```markdown
## AC Test Map

| AC | REQ-ID | L1 (unit/integration) | L2 (demo Playwright / Maestro UI) | L3 (real API local/staging) | Seed / fixture |
|---|---|---|---|---|---|
| AC1 | SPEC:example:AC1 | `Casazen.Tests/...` | `e2e/foo.spec.ts` | `e2e/l3/foo-l3.spec.ts` | InMemory seed X |
| AC2 | ADR-001-R2 | N/A — non UI | — | — | — |
```

Rules:
- **AC Test Map is mandatory** — Stage 02 cannot exit without it (G9).
- Spec must use [`Sessions/specs/_TEMPLATE.md`](../../../Sessions/specs/_TEMPLATE.md) sections: Verifiable Outcomes, UX/UI Quality, Export/Report Criteria when applicable (G9b).
- L2 may use `page.route()` mocks for **UI contract only**.
- L3 **must not** mock the API path under test (Auth0 setup may use storage state). L2 alone never closes a UI AC.
- **One titled test per AC** in L2 and L3 (`test('AC3: …')`). Mapping many ACs to one smoke file is a Stage 03 `check-ac-depth` FAIL.
- Export/report ACs: L1 and/or L3 must assert **content** (CSV headers/rows, PDF non-empty / magic bytes), not only button visibility or `Content-Type`.
- Mobile ACs use Maestro YAML paths under `../mobile/e2e/`.
- Referenced L1/L2/L3 paths must exist on disk (`check-ac-matrix.ps1`).

## Handoff to Stage 03

Pass to development with:
- Issue number: `#N`
- Spec file: `Sessions/design-<issue-N>.md`
- Branch name to create: `feature/<issue-N>-<slug>`
