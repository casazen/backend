# Pipeline: Long-term UI layer separation

## Status
- status: running
- current_stage: 06-operations
- started: 2026-06-03T00:00:00Z
- last_updated: 2026-06-03T21:22:00Z

## Input
- description: Esponi la nuova sezione long-term in UI come un suo layer separato, con distinzione tra utenti short e long
- type: feat
- priority: medium

## Artifacts
- issue: #182 — https://github.com/casazen/backend/issues/182 (closed)
- branch: feature/182-long-term-ui-layer (merged)
- design_spec: Sessions/design-182.md
- review_report: Sessions/review-182.md
- release_report: Sessions/release-182.md
- pr_frontend: 86 (feature), 87 (release develop→main)
- pr_backend: N/A
- tag: v0.1.2 (frontend, main)
- release_url: https://github.com/casazen/frontend/releases/tag/v0.1.2
- ops_report: (pending — Stage 06 on main)

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | ✅ | #182 |
| 02-design | completed | 1 | ✅ | design-182.md |
| 03-development | completed | 1 | ✅ | PR #86 |
| 04-review | completed | 1 | ✅ | review-182.md |
| 05-release | completed | 1 | Phase A–D ✅ | release-182.md, v0.1.2 on main |
| 06-operations | pending | - | - | - |
