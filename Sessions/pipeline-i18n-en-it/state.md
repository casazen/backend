# Pipeline: i18n EN/IT — restore missing labels

## Status
- status: running
- current_stage: 04-review
- started: 2026-06-10T12:00:00Z
- last_updated: 2026-06-11T00:30:00Z

## Input
- description: Le label UI sono sparite durante un setup multilingua incompleto; ripristinare le label e introdurre supporto i18n inglese e italiano
- type: feat
- priority: high

## Artifacts
- issue: #251 — https://github.com/casazen/backend/issues/251
- branch: feature/251-i18n-en-it (frontend only; backend N/A)
- design_spec: Sessions/design-251.md
- pr_backend: N/A
- pr_backend_url: N/A
- pr_frontend: 129
- pr_frontend_url: https://github.com/casazen/frontend/pull/129
- release_report: (pending)
- tag: (pending)
- release_url: (pending)
- ops_report: (pending)

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | ✅ G1–G5 | #251 |
| 02-design | completed | 1 | ✅ G1–G8 | design-251.md |
| 03-development | completed | 1 | ✅ G5–G9 (G1–G4 N/A BE) | feature/251-i18n-en-it |
| 04-review | in_progress | 1 | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | - | - |
