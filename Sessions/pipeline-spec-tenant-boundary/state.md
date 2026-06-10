# Pipeline: spec-tenant-boundary (Org + OrgId tenant boundary)

## Status
- status: completed
- current_stage: 06-operations
- last_updated: 2026-06-09T14:50:00+02:00
- tag: v1.1.6
- release_url: https://github.com/casazen/backend/releases/tag/v1.1.6
- release_report: Sessions/release-202.md
- ops_report: Sessions/ops-report-2026-06-09-pr202-tenant-boundary.md

## Input
- description: Implement the multi-tenant Org boundary (Org entity + OrgId FK + plan entitlement) per Sessions/specs/spec-tenant-boundary.md (US-004). Foundational: OrgId migration must land first (nullable -> backfill default Org per OwnerId -> NOT NULL + FK), with tested down-migrations and a pre-flight NULL-OrgId check (DA amendment AC10b). Adds Org.StripeConnectedAccountId field consumed by later specs.
- type: feat
- priority: high
- source_spec: Sessions/specs/spec-tenant-boundary.md
- pipeline_set: Phase 1 (1 of 7) — dependency-first

## Artifacts
- issue: 202
- issue_url: https://github.com/casazen/backend/issues/202
- branch: feature/202-tenant-boundary
- design_spec: Sessions/design-202.md
- pr_backend: 203
- pr_backend_url: https://github.com/casazen/backend/pull/203
- pr_frontend: 104
- pr_frontend_url: https://github.com/casazen/frontend/pull/104
- release_report: Sessions/release-202.md
- tag: v1.1.6
- release_url: https://github.com/casazen/backend/releases/tag/v1.1.6
- ops_report: Sessions/ops-report-2026-06-09-pr202-tenant-boundary.md

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | 5/5 ✅ | issue #202 |
| 02-design | completed | 1 | 8/8 ✅ | Sessions/design-202.md |
| 03-development | completed | 2 | BE all ✅; FE G5/G6/G8/G9 ✅, G7 ⚠️ pre-existing lint baseline (0 new) | PR be#203 / fe#104 |
| 04-review | completed | 2 | 0 critical; G3 satisfied (F-H1/F-H2 fixed, F-H3/F-M1/F-M2 deferred) | Sessions/review-202.md (iter-2 append pending) |
| 05-release | completed | 1 | Phase A–D complete (G20/G10/G17 partial — documented) | Sessions/release-202.md, v1.1.6 |
| 06-operations | completed | 1 | G1/G9 prod smoke ✅; G3–G8 N/A (no prod DB access) | Sessions/ops-report-2026-06-09-pr202-tenant-boundary.md |
