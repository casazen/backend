---
name: sdlc-init
description: >-
  Initialize or resume a feature pipeline state file under Sessions/pipeline-<slug>/.
  Use when starting a new feature or resuming an interrupted inner pipeline.
---

# sdlc-init

## New pipeline

1. Inputs: description (required), type (`feat`|`fix`|`compliance`|`ota`, default feat), priority (default medium).
2. Slug: lowercase hyphens, max 30 chars.
3. If `Sessions/pipeline-<slug>/state.md` exists → ask resume vs restart (or resume when automation).
4. Else create directory + state file (format in process STATE-FORMAT / legacy template):

```markdown
# Pipeline: <title>
## Status
- status: running
- current_stage: 01-planning
- started: <ISO-8601>
- last_updated: <ISO-8601>
## Input
- description: ...
- type: feat
- priority: medium
## Artifacts
- issue: (pending)
- branch: (pending)
- design_spec: (pending)
- pr_backend: (pending)
- pr_frontend: (pending)
- release_report: (pending)
- tag: (pending)
- ops_report: (pending)
## Stage History
| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | (pending) | - | - | - |
| 02-design | (pending) | - | - | - |
| 03-development | (pending) | - | - | - |
| 04-review | (pending) | - | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | - | - |
```

5. Hand off to `sdlc-stage-run` for `current_stage` — do not claim gates PASS.

## Resume

If `status=running` → re-run current stage via `sdlc-stage-run`.  
If `escalated`|`completed` → stop and report.
