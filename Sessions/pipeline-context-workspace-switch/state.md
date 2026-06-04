# Pipeline: Context / workspace switcher (split layer)

## Status
- status: running
- current_stage: 03-development
- started: 2026-06-04T00:00:00Z
- last_updated: 2026-06-04T12:00:00Z

## Input
- description: Introduce application contexts (short-rent, long-rent, admin) with canonical context-prefixed routing, centralized route manifest, workspace switcher in app shell, and backend-contextual authorization — per external analysis in Sessions/specs/spec-split-layer.md
- external_analysis: Sessions/specs/spec-split-layer.md
- pipeline_copy: Sessions/pipeline-context-workspace-switch/external-analysis.md
- related_pipeline: Sessions/pipeline-long-term-ui-layer (issue #182 — long-term UI layer; this work generalizes and structures the multi-area model)
- type: feat
- priority: medium

## Artifacts
- issue: #189 — https://github.com/casazen/backend/issues/189
- branch: feature/189-context-workspace-switch
- design_spec: Sessions/design-189.md
- pr_backend: (pending)
- pr_backend_url: (pending)
- pr_frontend: (pending)
- pr_frontend_url: (pending)
- release_report: (pending)
- tag: (pending)
- release_url: (pending)
- ops_report: (pending)

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | complete | 1 | G1–G5 pass | #189 |
| 02-design | complete | 1 | G1–G8 pass | design-189.md |
| 03-development | in_progress | 0 | - | - |
| 04-review | (pending) | - | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | - | - |
