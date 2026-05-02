---
name: create-cross-repo-issues
description: Create bidirectionally linked GitHub issues across casazen/backend and casazen/frontend. Use when a feature requires both backend API and frontend UI work. Delegates to @scrum_master_casazen which owns the full cross-repo coordination workflow.
invocable: true
---

# Create Cross-Repo Issues

Delegates to `@scrum_master_casazen` — Phase 1 (Issue Creation).

## When to Use

Use this skill when you have an architecture plan (from `@architect` or `@product_owner`) and need to create paired, cross-linked issues on both repos.

## Input Required

Provide to `@scrum_master_casazen`:
- Feature name and description
- Backend requirements (endpoints, DB changes, services)
- Frontend requirements (pages, components, API integration)
- Priority level
- Effort estimate (S/M/L/XL)

## What `@scrum_master_casazen` Does

1. Creates issue on `casazen/backend` with backend tasks + API endpoints
2. Creates issue on `casazen/frontend` with frontend tasks + API integration notes
3. Cross-links both issues bidirectionally
4. Creates `.claude/coordination/<feature-id>-status.md` tracking document

## Output

- `casazen/backend#<N>` — backend issue
- `casazen/frontend#<M>` — frontend issue
- Both cross-linked with "Related: ..." comments
- Coordination tracking document

## Full Protocol

See `@scrum_master_casazen` — Phase 1: Issue Creation.
