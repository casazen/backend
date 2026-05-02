# Contract Audit: FE/BE Sync Gap Analysis

**Agent**: `@scrum_master_casazen`
**Output**: GitHub issues per ogni disallineamento FE/BE

**IMPORTANT**: Solo audit + issue creation, NO code changes, NO branches, NO PRs

---

## Repositories

- **Backend**: `casazen/backend` (.NET 10 / C#)
- **Frontend**: `casazen/frontend` (React + TypeScript + Vite)

---

## Step 1-2: Parallel Read (Backend + Frontend)

**Parallelize**: Launch 2 Explore agents simultaneously

### Backend Files
- `API_DOCUMENTATION.md`
- `Casazen.Web/Controllers/*.cs`
- `Casazen.Core/*.cs` (Entities, DTOs, Enums)

**Extract**: Endpoints (method, path, request/response), DTOs (fields, types), Enums, error patterns, pagination params

### Frontend Files
- `src/types/*.ts`
- `src/api/*.ts`
- `src/queries/*.ts`
- `src/config/`, `.env.example`

**Extract**: TypeScript interfaces, API calls (path, method, body, return type), Auth0 config, base URL

---

## Step 3: Gap Analysis

**Categories**:

### A. TypeScript Types
- ❌ Missing TS interface for backend DTO
- ❌ Missing field in TS interface (exists in backend DTO)
- ❌ Incompatible type (`string` vs `number`)
- ❌ Obsolete TS interface (backend DTO deleted)

### B. API Client
- ❌ Backend endpoint without frontend function
- ❌ Frontend call to non-existent backend endpoint
- ❌ Wrong HTTP method (POST vs GET)
- ❌ Wrong path (`/api/v1/resource` vs `/api/resource`)

### C. TanStack Query Hooks
- ❌ Missing query hook for GET endpoint
- ❌ Missing mutation hook for POST/PUT/DELETE
- ❌ Incorrect query key structure

### D. Documentation
- ❌ Endpoint in backend but not documented in API_DOCUMENTATION.md
- ❌ Documentation outdated (request/response changed)

---

## Step 4: Issue Creation

**Agent**: `@github_agent` (invoked by `@scrum_master_casazen`)

**Per ogni gap**:
- Open issue su repo appropriato (`casazen/frontend` per TS types, `casazen/backend` per docs)
- Label: `contract-sync`, `severity:critical|high|medium`
- Cross-reference: "Related: casazen/[other-repo]#X" se richiede changes su entrambi

**Issue Template**:
```markdown
**Category**: [TypeScript Types | API Client | Query Hooks | Documentation]
**Severity**: [Critical | High | Medium]

## Gap
[Descrizione disallineamento]

## Backend Status
[Endpoint/DTO esistente o mancante]

## Frontend Status
[Interface/call esistente o mancante]

## Action Required
- [ ] [Specific fix]

Related: casazen/[repo]#[issue]
```

**Summary Issue**: Apri 1 issue riepilogativa su `casazen/backend` con link a tutte le issue create

---

## Step 5: Report

**Output**:
- Total gaps found: N
- By category: Types (X), API (Y), Query Hooks (Z), Docs (W)
- By severity: Critical (A), High (B), Medium (C)
- GitHub issue links

---

## Invocation

**Manual**:
```bash
Read .claude/docs/workflows/contract-audit.md
```

**Skill** (if configured):
```bash
/contract-audit
```

**Frequency**: Bi-weekly o prima di major release

---

## Notes

- **NO code changes** durante audit (solo gap detection + issue creation)
- **Parallelization** Step 1-2 per velocità
- **Severity**: Critical se blocca funzionalità, High se causa bug, Medium se solo inconsistenza docs
