# Stage 01 — Planning

**Pattern**: hub-and-spoke
**When to run**: before starting any new feature, bug fix, or regulatory change

## Purpose

Transform a raw idea or regulatory requirement into a well-scoped GitHub Issue with acceptance criteria, technical impact assessment, and compliance tagging. Nothing moves to design until the issue is complete.

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Orchestrates deliberation, synthesizes output | `agents/coordinator.md` |
| product-strategist | User story, acceptance criteria, priority | `agents/product-strategist.md` |
| tech-architect | Technical feasibility, migration impact, OTA scope | `agents/tech-architect.md` |
| regulatory-analyst | Italian compliance tagging (CIN, GDPR, Alloggiati, tax) | `agents/regulatory-analyst.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full loop specification.

**Key gates**:
- GitHub Issue exists and is fully structured
- Acceptance criteria are present and testable
- Technical dependencies identified (migrations, OTA platforms, background jobs)
- Regulatory labels applied if applicable

## Exit Artifact

GitHub Issue with:
- Title in Conventional Commits format
- Body: user story, acceptance criteria, technical notes, compliance labels
- Labels: `feature|fix|compliance|ota` + affected area + priority

## Chain

→ **Stage 02: Design** — link Issue ID in design spec file name
