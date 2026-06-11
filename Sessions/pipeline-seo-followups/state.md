# Pipeline: SEO Follow-ups (#258 post-release)

## Status
- status: in_progress
- current_stage: 03-development
- started: 2026-06-11T15:00:00Z
- last_updated: 2026-06-11T16:10:00Z

## Input
- description: Populate prod sitemap — expand comune registry, bootstrap generation on startup, bulk approve, admin "Genera tutti"
- type: growth
- priority: high
- parent_issue: #258

## Artifacts
- issue: (pending)
- branch: feature/seo-followups
- pr_backend: (pending)
- pr_frontend: (pending)

## Changes (local, uncommitted)
### Backend
- `ItalianComuneRegistry` expanded to 12 comuni
- `SeoBootstrapHostedService` — auto-seed when DB empty + `Seo:BootstrapOnStartup=true`
- `AdminSeoController` — `GET /comuni`, `POST /approve-all-drafts`, generate all when empty codes
- `SeoPageGenerationJob` — `autoApproveCounsel` overload
- `appsettings.json` — Seo bootstrap config

### Frontend
- Admin SEO dashboard — "Genera tutti", "Approva tutte", registry fetch
- E2E updated — AC13 covers bulk actions

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 03-development | in_progress | 1 | BE build ✅, E2E 3/3 ✅ | local changes |
| 04-review | (pending) | - | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | sitemap populated | - |
