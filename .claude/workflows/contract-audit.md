# Workflow: Contract Audit (FE/BE Sync)

**Agent**: `@scrum_master_casazen`
**Output**: GitHub issues per FE/BE misalignment

**IMPORTANT**: Audit + issue creation only. NO code changes, NO branches, NO PRs.

Invoked via: `/contract-audit` skill

---

## Repositories

- **Backend**: `casazen/backend` (.NET 10 / C#)
- **Frontend**: `casazen/frontend` (React + TypeScript + Vite)

---

## Step 1-2: Parallel Read

Parallelize: read backend and frontend simultaneously.

### Backend — extract
- `API_DOCUMENTATION.md`
- `Casazen.Web/Controllers/*.cs`
- `Casazen.Core/*.cs` (Entities, DTOs, Enums)

Extract: endpoints (method, path, request/response DTOs), error patterns, pagination params.

### Frontend — extract
- `src/types/*.ts`
- `src/api/*.ts`
- `src/queries/*.ts`

Extract: TypeScript interfaces, API calls (path, method, body, return type).

---

## Step 3: Gap Analysis

### A. TypeScript Types
- Missing TS interface for backend DTO
- Missing field in TS interface
- Incompatible type (`string` vs `number`)
- Obsolete TS interface (backend DTO removed)

### B. API Client
- Backend endpoint without frontend function
- Frontend call to non-existent backend endpoint
- Wrong HTTP method (POST vs GET)
- Wrong path (`/api/v1/resource` vs `/api/resource`)

### C. TanStack Query Hooks
- Missing query hook for GET endpoint
- Missing mutation hook for POST/PUT/DELETE
- Incorrect query key

### D. Documentation
- Endpoint not in `API_DOCUMENTATION.md`
- Documentation outdated (request/response changed)

---

## Step 4: Issue Creation (`@github_agent`)

Per gap:
```bash
gh issue create --repo casazen/frontend \
  --title "[CONTRACT] <gap description>" \
  --label "contract-sync,severity:critical" \
  --body "..."
```

Issue body:
```markdown
**Category**: TypeScript Types | API Client | Query Hooks | Documentation
**Severity**: Critical | High | Medium

## Gap
[Description of misalignment]

## Backend Status
[Endpoint/DTO existing or missing]

## Frontend Status
[Interface/call existing or missing]

## Action Required
- [ ] [Specific fix]

Related: casazen/<other-repo>#<N>
```

Create 1 summary issue on `casazen/backend` linking all created issues.

---

## Step 5: Report

```
Total gaps: N
By category: Types (X) / API (Y) / Query Hooks (Z) / Docs (W)
By severity: Critical (A) / High (B) / Medium (C)
GitHub issues: [links]
```

---

## Notes

- **Frequency**: Bi-weekly or before major release
- **Parallelization**: Steps 1-2 run in parallel for speed
- **Severity**: Critical = blocks functionality, High = causes bugs, Medium = docs inconsistency only
