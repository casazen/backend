---
name: contract-audit
description: Audit FE/BE API contract alignment. Reads backend controllers/DTOs and frontend TypeScript types/API client in parallel, identifies misalignments (types, endpoints, query hooks, docs), and creates a GitHub Issue per gap. No code changes — audit only.
invocable: true
---

# Contract Audit: FE/BE Sync

**Frequency**: bi-weekly or before major releases.
**IMPORTANT**: Audit + issue creation only. NO code changes, NO branches, NO PRs.

## Agent

`@scrum_master_casazen` (with `@github_agent` for issue creation)

## Execution Steps

**1-2. Parallel Read** (parallelize these):

Backend:
- `API_DOCUMENTATION.md`
- `Casazen.Web/Controllers/*.cs`
- `Casazen.Core/*.cs` (Entities, DTOs, Enums)

Frontend (`casazen/frontend`):
- `src/types/*.ts`
- `src/api/*.ts`
- `src/queries/*.ts`

**3. Gap Analysis** — check four categories:

| Category | Examples |
|---|---|
| **TypeScript Types** | Missing TS interface for DTO, incompatible field type |
| **API Client** | Frontend calls non-existent endpoint, wrong HTTP method |
| **Query Hooks** | Missing TanStack Query hook for GET/POST/PUT/DELETE |
| **Documentation** | Endpoint not in API_DOCUMENTATION.md, outdated docs |

Severity: Critical (blocks functionality) / High (causes bugs) / Medium (docs only).

**4. Issue Creation** (`@github_agent`):
```bash
gh issue create --repo casazen/frontend \
  --title "[CONTRACT] <gap description>" \
  --label "contract-sync,severity:high" \
  --body "**Category**: ...\n**Gap**: ...\n**Action**: ...\nRelated: casazen/backend#<N>"
```

Create 1 summary issue on `casazen/backend` linking all created issues.

**5. Report**:
```
Total gaps: N
By category: Types (X) / API (Y) / Query Hooks (Z) / Docs (W)
By severity: Critical (A) / High (B) / Medium (C)
```

## Output

- N GitHub Issues (frontend + backend)
- 1 summary issue on backend
- Report by category and severity

## Full Workflow Spec

`.claude/workflows/contract-audit.md`
