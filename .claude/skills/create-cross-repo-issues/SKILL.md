---
name: create-cross-repo-issues
description: Create bidirectionally linked GitHub issues across casazen/backend and casazen/frontend for full-stack features. Delegates to scrum-master-casazen agent which owns the cross-repo coordination workflow.
---

# Create Cross-Repo Issues

Creates paired, cross-linked GitHub issues on both repos for full-stack features.
Delegates to `@scrum-master-casazen`.

## When to use

When you have an architecture plan (from `@architect` or `@product-owner`) and need issues created on both repos simultaneously.

## Input to provide

Tell `@scrum-master-casazen`:
- Feature name and description
- Backend requirements: endpoints, DB changes, services
- Frontend requirements: pages, components, API integration
- Priority: critical / high / medium / low
- Effort: S (1-2d) / M (3-5d) / L (1-2w) / XL (>2w)

## What happens

1. `@scrum-master-casazen` creates issue on `casazen/backend` with backend tasks + API spec
2. Creates issue on `casazen/frontend` with frontend tasks + API integration notes
3. Cross-links both issues bidirectionally (`Related: casazen/<repo>#<N>`)
4. Creates `.claude/coordination/<feature-id>-status.md` tracking document

## Output

- `casazen/backend#<N>` — backend issue with tasks, API endpoints, acceptance criteria
- `casazen/frontend#<M>` — frontend issue with tasks, API integration, acceptance criteria
- Bidirectional cross-links via issue comments
- Coordination status document with Mermaid dependency graph

## Full cross-repo coordination protocol

`@scrum-master-casazen` agent — Phase 1 through Phase 4.
