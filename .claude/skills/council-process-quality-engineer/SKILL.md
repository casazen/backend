---
name: council-process-quality-engineer
description: Quality gate validation for CasaZen AI-SDLC harness loops — ensures gates are executable, coverage targets are numeric, and loops terminate.
---

# Council domain — Process Quality Engineering

## Context to load before acting

1. Read `council/domain-context.md` section: `testing-landscape`
2. Read `.claude/rules/code-style.md` — coverage targets, async rules, test naming
3. Read `docs/PROJECT.md` section: "Key conventions"

## CasaZen test infrastructure

**Backend:**
- Framework: xUnit
- Run: `dotnet test` (all tests) | `dotnet test --filter "ClassName"` (specific)
- Coverage: `dotnet test /p:CollectCoverage=true`
- Format check: `dotnet format --verify-no-changes`
- Coverage targets: critical paths 100%, services 80%, controllers 70%

**Frontend:**
- Unit: `npm test` (Vitest)
- E2E: `npm run test:e2e` (Playwright)
- Build: `npm run build` | `tsc -b --noEmit`
- Lint: `npm run lint`

## Quality gate validation criteria

A gate is **✅ executable** if: it is a specific shell command that returns pass/fail
A gate is **⚠️ vague** if: it describes intent without a verifiable command or specific criterion
A gate is **❌ missing** if: a known failure mode for this stage has no gate

## CasaZen-specific failure modes to check per stage

| Stage | Failure mode | Gate that catches it |
|---|---|---|
| 03-development | Tests pass but migration breaks | `dotnet ef migrations script` compiles + applies |
| 03-development | Frontend builds but TypeScript errors ignored | `tsc -b --noEmit` |
| 03-development | Code passes tests but fails format check | `dotnet format --verify-no-changes` |
| 04-review | Critical issue found post-merge | Review gate: no critical findings allowed before merge |
| 05-release | Deploy succeeds but API is broken | `GET /api/health` returns 200 post-deploy |
| 06-operations | Compliance drift undetected | Monthly CIN/GDPR audit gate |

## Loop termination validation

Every harness must have:
- `max_iterations: N` (recommended: 3)
- `escalation: human decision required if max reached`

A harness without termination condition is invalid — flag it as ❌ and require a fix.

## Output shape

Gate-by-gate assessment table per stage:
`Stage | Gate | Status (✅/⚠️/❌) | Specific improvement if needed`

Plus loop termination summary for all 6 stages.
