# Workflow: Step 2 — Dispatcher (Approved Epic → Atomic Tasks)

**Orchestrator**: `@scrum-master-casazen`
**Analyzer**: `@analyzer-agent`
**Planner**: `@feature-developer` (planning mode only — no code)
**Issue Creator**: `@scrum-master-casazen`

Invoked via: `/step2-dispatch <issue_number>`
Auto-triggered by: GitHub Actions on label `approved`

---

## Label State Machine

```
approved  ← input (backlog item from Step 1)
  ↓ [Phase A — @analyzer-agent: dependency map]
  ↓ [Phase B — @feature-developer: decompose into tasks]
  ↓ [Phase C — @scrum-master-casazen: create GitHub issues]
sprint-candidate  (on all created task issues)
  ↓ [human Scrum Master adds label to selected tasks]
in-sprint  → triggers Step 3 per task
```

---

## Phase A — Dependency Mapping (`@analyzer-agent`)

```bash
gh issue view $ISSUE_NUMBER --json number,title,body,labels
```

Read the Epic/Feature body. Identify which of the 9 canonical layers are touched by looking for these signals:

| Layer | Signals in issue body |
|---|---|
| DB migrations | "schema change", "new table", "add column", "entity", "migration" |
| Domain entities/models | "model", "entity", "domain", "business rule", "value object" |
| Repositories/Services | "service", "repository", "business logic", "validation", "calculation" |
| API controllers | "endpoint", "API", "controller", "route", "HTTP", "REST" |
| Swagger/OpenAPI docs | any API change → Swagger update required |
| FE API service layer | any BE API change → FE TypeScript types + client methods |
| FE State management | "state", "store", "context", "hook", "Zustand", "Redux" |
| FE UI components/pages | "page", "component", "form", "UI", "view", "screen", "display" |
| E2E / integration tests | always required when FE UI changes are present |

Produce a dependency map:

```
## Dependency Map for Epic #ISSUE_NUMBER

Layers touched: [list only touched layers]

Canonical execution order:
1. DB migrations        [touched / skipped]
2. Domain entities      [touched / skipped]
3. Repos + Services     [touched / skipped]
4. API controllers      [touched / skipped]
5. Swagger update       [touched / skipped]
6. FE API service       [touched / skipped]
7. FE State             [touched / skipped]
8. FE UI                [touched / skipped]
9. E2E tests            [touched / skipped]
```

---

## Phase B — Task Decomposition (`@feature-developer`, planning mode)

**PLANNING ONLY** — no file edits, no git operations, no `Write`/`Edit` tool calls.

Input: dependency map from Phase A + original Epic body.

### Decomposition rules

- Each task = 1 atomic unit of work, max 1–2 days
- Never mix BE and FE work in a single task
- Skip layers not touched by the Epic
- DB migration task is always first if schema changes are needed
- E2E tests task is always last if FE UI changes are included
- Max 12 tasks per Epic — flag for Epic splitting if more are needed

### BE task body template

```markdown
## What to Build
[1-3 sentences: exact deliverable, not implementation details]

## API Contract
[Only if this task exposes or changes an endpoint. Otherwise omit.]
- **Method + path**: `GET /api/resource/{id}`
- **Request**: `{ field: type }`
- **Response**: `{ field: type }`
- **Error codes**: 400 (validation), 401 (unauthorized), 404 (not found)

## Definition of Done
- [ ] Code compiles, all tests pass (`dotnet test`)
- [ ] Unit tests for new logic (AAA pattern, Moq)
- [ ] EF Core migration included (if schema change)
- [ ] Swagger docs updated (if endpoint added/changed)
- [ ] Code review passes with no 🔴 Critical findings

## Dependencies
- **Blocked by**: [task title or "none"]
- **Blocks**: [downstream task title or "none"]
- **Part of**: casazen/backend#EPIC_NUMBER
```

### FE task body template

```markdown
## What to Build
[1-3 sentences: exact deliverable]

## API Contract (consumed)
[Endpoint this task calls — copy from the BE task it depends on]
- **Method + path**: `GET /api/resource/{id}`
- **Response shape (TypeScript)**:
  ```typescript
  interface ResourceDto { ... }
  ```

## Definition of Done
- [ ] Component renders correctly (visual + functional)
- [ ] API integration works against backend (staging or msw mock)
- [ ] Unit tests (Vitest/Jest + React Testing Library)
- [ ] E2E test (Playwright) — required on the final FE task only

## Dependencies
- **Blocked by**: casazen/backend#BE_TASK_NUMBER (must be merged first)
- **Blocks**: [downstream FE task or "none"]
- **Part of**: casazen/backend#EPIC_NUMBER
```

### Effort sizing

| Label | Duration |
|---|---|
| `effort:XS` | < 4 hours (trivial migration, Swagger update) |
| `effort:S` | 0.5–1 day (single-layer change, straightforward) |
| `effort:M` | 1–2 days (multi-step, moderate complexity) |

---

## Phase C — Issue Creation (`@scrum-master-casazen`)

Create issues in the canonical order produced by Phase B.

### BE tasks

```bash
gh issue create \
  --repo casazen/backend \
  --title "[BE] <action verb> <noun>" \
  --label "task,sprint-candidate,be,effort:S" \
  --body "<task body from Phase B BE template>"
```

### FE tasks

```bash
gh issue create \
  --repo casazen/frontend \
  --title "[FE] <action verb> <noun>" \
  --label "task,sprint-candidate,fe,effort:S" \
  --body "<task body from Phase B FE template>"
```

### Dependency cross-linking

After all issues are created, edit each FE issue to add the backend issue number:

```bash
# Already embedded in issue body via "Blocked by: casazen/backend#N"
# Add a comment on the BE issue pointing forward:
gh issue comment $BE_ISSUE --repo casazen/backend \
  --body "Unblocks: casazen/frontend#$FE_ISSUE"
```

### Epic summary comment

```bash
gh issue comment $EPIC_ISSUE_NUMBER --repo casazen/backend --body "## Task Breakdown — Step 2 Dispatch Complete

**Total**: N backend + M frontend = X tasks

### Backend Tasks (casazen/backend)
- [ ] #N1 — [BE] Task 1 title \`effort:XS\`
- [ ] #N2 — [BE] Task 2 title \`effort:S\`

### Frontend Tasks (casazen/frontend)
- [ ] casazen/frontend#M1 — [FE] Task 1 title \`effort:S\`
- [ ] casazen/frontend#M2 — [FE] Task 2 title \`effort:M\`

### Execution Order
\`\`\`
#N1 → #N2 → #N3 → #N4  (BE sequence)
               ↓
         casazen/frontend#M1 → #M2  (FE, starts after #N3 merged)
\`\`\`

**Next**: Scrum Master adds \`in-sprint\` label to selected tasks to trigger Step 3."
```

---

## Notes

- `@feature-developer` in Phase B is in **planning mode only** — it must not create branches, write code, or use Write/Edit tools
- If the Epic exceeds 12 tasks, post a flag comment on the Epic and suggest splitting into sub-Epics before proceeding
- Human in the loop: Scrum Master manually adds `in-sprint` to tasks selected for the current sprint
- All tasks are created with `sprint-candidate` so the Scrum Master can choose which ones to pull in
- See `.claude/rules/github-flow-mandatory.md` — no code is written in this workflow, only GitHub issues
