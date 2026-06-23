# Stage 03 — Development

**Pattern**: plan-execute-verify
**Input**: `Sessions/design-<issue-id>.md` from Stage 02

## Purpose

Implement the feature on a `feature/<name>` branch, covering backend and frontend, with tests, formatting, and an open PR as the exit artifact. Every quality gate must pass before leaving this stage.

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Plans work, verifies gates, opens PR | `agents/coordinator.md` |
| backend-developer | .NET 10 implementation, EF Core migration, service logic | `agents/backend-developer.md` |
| frontend-developer | React 19 components, API integration, Zustand/TanStack Query | `agents/frontend-developer.md` |
| test-engineer | xUnit backend tests, Vitest frontend tests, Playwright E2E | `agents/test-engineer.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full loop specification.

**Key gates** (all must exit 0):
- `dotnet test`
- `dotnet format --verify-no-changes`
- `npm test`
- `tsc -b --noEmit`
- `npm run lint`
- `npm run build`
- `npm run test:e2e:local` (requires local backend running via `.\scripts\start-backend-local.ps1`)
- `dotnet ef migrations script` (if schema changed)

**Compliance gates** (checked manually):
- CIN unit test passes if Property entity modified
- No secrets in `appsettings.Development.json` or `.env` files
- GDPR fields present if Guest entity modified

## Exit Artifact

Feature branch `feature/<name>` with open PR (`gh pr create --base develop`):
- Conventional Commits title
- PR body: summary + test plan + `Closes #N`
- All quality gates passed locally (E2E runs locally, NOT in CI — CI runs build + unit tests + format only)

## Chain

→ **Stage 04: Review** — PR handed to reviewers
