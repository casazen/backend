# Stage 02 — Design

**Pattern**: builder-validator
**Input**: GitHub Issue from Stage 01

## Purpose

Produce a complete design specification before any code is written. The spec covers API contracts, frontend UX flow, and security considerations. Development cannot start without a passing spec.

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Orchestrates deliberation, synthesizes spec | `agents/coordinator.md` |
| api-designer | Endpoint contracts, request/response schemas, EF Core schema changes | `agents/api-designer.md` |
| frontend-designer | UI flow, component breakdown, state management plan, route changes | `agents/frontend-designer.md` |
| security-by-design | Auth gates, threat model, OTA key placement, GDPR data-flow | `agents/security-by-design.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full loop specification.

**Key gates**:
- `Sessions/design-<issue-id>.md` created and complete
- Every new endpoint specifies `[Authorize]` or public justification
- Frontend routes have `<ProtectedRoute>` specified
- OTA keys in `appsettings.json` config section (not hardcoded)
- GDPR: `ErasureRequested` + `DataRetentionUntil` in scope if Guest data involved

## Exit Artifact

`Sessions/design-<issue-id>.md` containing:
- API contract (endpoints, schemas)
- Frontend flow (routes, components, state)
- Security notes (auth, PII, secrets)
- EF Core migration plan (if schema change)
- Open questions resolved

## Chain

→ **Stage 03: Development** — developers implement from the spec
