---
name: council-sdlc-architect
description: SDLC process design for CasaZen — 6-stage AI-SDLC with harness quality loops, agent councils, and full .NET 10 + React 19 coverage.
---

# Council domain — SDLC Architecture

## Context to load before acting

1. Read `council/domain-context.md` sections: `overview`, `tech-stack`, `bounded-context-pattern`, `testing-landscape`, `regulatory-environment`
2. Read `.claude/rules/github-flow-mandatory.md` — all stages producing code must enforce this
3. Read `docs/PROJECT.md` sections: "Key conventions", "Non-obvious rules / gotchas"
4. Read `docs/FRONTEND-PROJECT.md` sections: "Key conventions", "Non-obvious rules / gotchas"

## SDLC stage design principles

Each of the 6 stages must have:
- **Council pattern**: hub-and-spoke (decisions) | builder-validator (artifacts) | plan-execute-verify (regulated execution)
- **Agents**: 1 coordinator + 2–4 specialists, each with a distinct non-overlapping focus
- **Harness loop**: entry criteria → council run → quality gates → loop condition → exit + handoff artifact
- **Quality gates**: executable commands or specific checkable criteria — no vague language

## Stage assignments

| Stage | Pattern | Key agents | Primary gates |
|---|---|---|---|
| 01-planning | hub-and-spoke | product-strategist, tech-architect, regulatory-analyst | GitHub Issue completeness, acceptance criteria present |
| 02-design | builder-validator | api-designer, frontend-designer, security-by-design | API contract complete, frontend flows defined, security review passed |
| 03-development | plan-execute-verify | backend-developer, frontend-developer, test-engineer | `dotnet test`, `npm test`, `dotnet format --verify-no-changes`, `npm run lint`, migration applies |
| 04-review | builder-validator | code-reviewer, security-auditor | No critical findings, OWASP checks, compliance audit |
| 05-release | plan-execute-verify | release-manager, qa-validator | CI/CD pass, `docker build` success, `GET /api/health` returns 200 |
| 06-operations | hub-and-spoke | regulatory-monitor, incident-responder | Compliance drift check, KPI review |

## Harness loop template

```
WHILE quality_gates_fail AND iteration < max_iterations:
    1. Run council session (coordinator spawns specialists)
    2. Specialists deliberate and produce output
    3. Check quality gates (run commands or check criteria)
    4. If any gate fails → generate fix list → loop back
    5. If all gates pass → produce exit artifact → handoff

IF max_iterations reached: ESCALATE (human decision required)
```

## CasaZen-specific compliance gates (Development + Review only)

- CIN format: `IT-XXXXX-XXXXXXXXXX` — `[CinCode]` attribute on Property entity
- Tourist tax: verify no hardcoded amounts — use `TouristTaxRate` entity
- Alloggiati Web: check-in triggers background job, not inline processing
- GDPR: `ErasureRequested` flag + `DataRetentionUntil` present on Guest entity

## Output shape

Full stage-by-stage specification:
- Stage name, pattern, max_rounds
- Agent table: slug | role | focus | file path
- Harness: entry criteria → quality gates (executable) → loop condition → exit artifact
- Folder structure: `.claude/sdlc/NN-stage/harness.md` + `agents/`
