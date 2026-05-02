---
name: feature-implementation
description: Coordinate feature implementation from GitHub issues with review cycle
invocable: true
---

# Feature Implementation: Issue → PR Workflow

Coordinate implementation of features from GitHub issues, managing FE/BE coherence, PR creation, and review cycle.

## Workflow

Load full workflow definition:

```
Read .claude/hooks/feature-implementation.md
```

Then execute the workflow as documented, orchestrating:
- `@scrum_master_casazen` (coordination)
- `@feature_developer` (implementation)
- `@code_reviewer` (review)
- `@release_manager` (merge)

## Quick Summary

The workflow will:
0. ✅ **Verify open issues** - if none exist, auto-trigger `/compliance-feature` to create backlog
1. ✅ Analyze open issues (frontend + backend, excluding epics)
2. ✅ Group related features and plan implementation
3. ✅ Coordinate `@feature_developer` to implement
4. ✅ Run review cycle (max 3 iterations)
5. ✅ Merge via `@release_manager` when approved

**Output**: PR merged to main, issues closed, or escalation report if review blocked

**Auto-trigger**: If no issues exist, automatically runs `/compliance-feature` to generate backlog first
