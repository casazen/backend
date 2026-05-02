---
name: contract-audit
description: Audit FE/BE API contract alignment. Reads backend controllers and DTOs plus frontend TypeScript types and API client in parallel, identifies misalignments across four categories, and creates a GitHub Issue per gap. Audit only — no code changes.
---

# Contract Audit: FE/BE Sync

**IMPORTANT**: Audit + issue creation only. NO code changes, NO branches, NO PRs.

**Frequency**: bi-weekly or before major release.

## Agent Chain

```
@scrum-master-casazen (audit orchestrator)
  → reads backend + frontend in parallel
  → runs gap analysis across 4 categories
  → hands off to @github-agent

@github-agent (issue creator)
  → creates 1 issue per gap
  → creates 1 summary issue on backend
  → reports totals by category and severity
```

## Step 1-2 — Parallel Read

Run these two reads simultaneously:

**Backend** (`casazen/backend`):
- `API_DOCUMENTATION.md`
- `Casazen.Web/Controllers/*.cs` — extract: endpoint method, path, request/response type, auth requirement
- `Casazen.Core/Entities/*.cs` — extract: field names, types, required/optional
- `Casazen.Core/*.cs` (DTOs, Enums) — extract: all DTO shapes

**Frontend** (`casazen/frontend`):
- `src/types/*.ts` — extract: interface names, fields, types
- `src/api/*.ts` — extract: function names, URL paths, HTTP methods, request/response types
- `src/queries/*.ts` — extract: query key, hook name, endpoint called

**Handoff**: two structured inventories (backend contracts, frontend contracts) to gap analysis.

## Step 3 — Gap Analysis (`@scrum-master-casazen`)

Four categories, check each exhaustively:

### A. TypeScript Types
- Missing TS interface for a backend DTO
- Missing field in existing TS interface (present in backend, absent in frontend)
- Incompatible type (`string` vs `number`, `Date` vs `string`)
- Obsolete TS interface (backend DTO was deleted)

### B. API Client
- Backend endpoint has no corresponding frontend function
- Frontend function calls a non-existent backend endpoint
- Wrong HTTP method (`GET` instead of `POST`)
- Wrong path (`/api/v1/resource` vs `/api/resource`)
- Missing authentication header

### C. TanStack Query Hooks
- Missing `useQuery` hook for a GET endpoint
- Missing `useMutation` hook for POST / PUT / DELETE
- Incorrect query key (stale cache, no invalidation)

### D. Documentation
- Endpoint in controllers but missing from `API_DOCUMENTATION.md`
- Documentation describes outdated request/response shape

**Severity per gap**:
- Critical: blocks a user-facing feature from working
- High: causes silent data bugs or incorrect behavior
- Medium: documentation inconsistency only, no runtime impact

**Handoff artifact**: gap list with category, severity, backend status, frontend status, specific fix required.

## Step 4 — Issue Creation (`@github-agent`)

Input: gap list from Step 3.

Per gap:
```bash
# Frontend gap → create on casazen/frontend
gh issue create --repo casazen/frontend \
  --title "[CONTRACT] <gap description>" \
  --label "contract-sync,severity:critical" \
  --body "**Category**: TypeScript Types\n**Severity**: Critical\n\n## Gap\n[description]\n\n## Backend Status\n[existing DTO/endpoint]\n\n## Frontend Status\n[missing/wrong interface]\n\n## Action Required\n- [ ] [specific fix]\n\nRelated: casazen/backend#<N>"

# Backend gap → create on casazen/backend
gh issue create --repo casazen/backend \
  --title "[CONTRACT] <gap description>" \
  --label "contract-sync,severity:medium" \
  --body "..."
```

After all gaps → create 1 summary issue on `casazen/backend`:
```bash
gh issue create --repo casazen/backend \
  --title "[CONTRACT AUDIT] Summary - <date>" \
  --label "contract-sync" \
  --body "## Contract Audit Results\n\nTotal gaps: N\nCritical: A / High: B / Medium: C\n\n## Issues Created\n- casazen/frontend#X: ...\n- casazen/backend#Y: ..."
```

## Step 5 — Report

```
Contract Audit — <date>
────────────────────────
Total gaps: N

By category:
  TypeScript Types: X
  API Client:       Y
  Query Hooks:      Z
  Documentation:    W

By severity:
  Critical: A
  High:     B
  Medium:   C

Issues created: [links]
```

## Output

- N GitHub Issues on `casazen/frontend` and/or `casazen/backend`
- 1 summary issue on `casazen/backend`
- Report printed above

## Full workflow spec

`.claude/workflows/contract-audit.md`
