# Workflow Documentation — Moved

> **This directory is superseded.** Workflow specs have been consolidated into `.claude/workflows/`.

## New locations

| Document | Old path | New path |
|---|---|---|
| Feature Implementation | `docs/workflows/feature-implementation.md` | `.claude/workflows/feature-implementation.md` |
| Compliance Feature Creation | `docs/workflows/compliance-feature-creation.md` | `.claude/workflows/compliance-feature-creation.md` |
| Contract Audit | `docs/workflows/contract-audit.md` | `.claude/workflows/contract-audit.md` |
| Review Process | `hooks/common/review-process.md` | `.claude/workflows/common/review-process.md` |

## Why

The old structure split workflow specs across `docs/workflows/` and `hooks/common/` with no clear ownership. Skills loaded them via `Read <old-path>` (thin-invoker anti-pattern). The new structure:

- `.claude/workflows/` — all workflow specs (canonical, single location)
- `.claude/hooks/scripts/` — real executable shell scripts (actual hooks)
- `.claude/skills/` — self-contained invocable skills (no thin-invoker layer)

## Workflow Index

See `.claude/workflows/` for the canonical workflow documentation.

Skills to invoke workflows:
- `/feature-implementation` — implement issues through to merged PR
- `/compliance-feature` — regulatory scan → gap analysis → GitHub issues
- `/contract-audit` — FE/BE alignment audit
