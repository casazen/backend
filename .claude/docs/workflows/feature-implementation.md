# Feature Implementation: Issue → PR

**Agent**: `@scrum_master_casazen` coordina `@feature_developer` → `/code-review-local` → `@release_manager`
**Output**: PR aperta, reviewed, pronta per merge

---

## Flow

1. **Prerequisites** → Verifica issue aperte (se nessuna → trigger compliance workflow)
2. **Issue Analysis** → Analizza issue FE + BE, identifica dipendenze
3. **Planning** → Piano implementazione (ordine, API contract, DB migrations)
4. **Implementation** → `@feature_developer` implementa + apre PR
5. **Review** → `/code-review-local` max 3 iterazioni (vedi `.claude/hooks/common/review-process.md`)
6. **Merge** → `@release_manager` approva e merge

---

## Step 0: Prerequisites

**Check issue aperte**:
```bash
gh issue list --state open --repo casazen/backend --json number,title,labels
gh issue list --state open --repo casazen/frontend --json number,title,labels
```

**Filtra**: Include `enhancement|bug|compliance|feature`, exclude `epic`

**Se nessuna issue** → Trigger `compliance-feature-creation` workflow

---

## Step 1: Issue Analysis

**Actions**:
- Leggi tutte le issue aperte (FE + BE)
- Identifica dipendenze: API contract FE-BE, DB migration, infra setup
- Ordina per priorità: compliance deadline > priority label > effort
- Cross-check issue linked (FE↔BE)

---

## Step 2: Planning

**Output Plan**:
- **Implementazione order**: BE first (API) → FE (UI)
- **API Contract**: endpoint, request/response DTOs, error codes
- **DB Migrations**: schema changes, seed data
- **Testing strategy**: unit + integration tests
- **Dipendenze esterne**: Auth0, Stripe, SendGrid, OTA adapters

**Handoff to** `@feature_developer` con piano dettagliato

---

## Step 3: Implementation

**Agent**: `@feature_developer`

**Actions** (vedi `.claude/agents/feature_developer.md`):
- Crea branch `feature/descriptive-name`
- Implementa seguendo `.claude/rules/*`
- Write tests (unit + integration)
- Commit con Conventional Commits (`feat:`, `fix:`)
- Push branch
- Apri PR su GitHub (template con Summary, Test Plan, Closes #X)

**CRITICAL**: NON merge a main direttamente (vedi `.claude/rules/github-flow-mandatory.md`)

---

## Step 4: Review

**Review Process**: Vedi `.claude/hooks/common/review-process.md`

**Steps**:
1. `@feature_developer` apre PR
2. Run `/code-review-local` skill
3. Fix 🔴 Critical + 🟡 High issues
4. Push updates
5. Re-run review (max 3 iterazioni)
6. Se still blockers → Escalate

**Output**: PR approved o escalation report

---

## Step 5: Merge

**Agent**: `@release_manager`

**Conditions**:
- All CI checks pass
- Code review approved (🔴 Critical fixed)
- Tests pass
- No merge conflicts

**Action**:
```bash
gh pr merge <number> --squash --delete-branch
```

---

## Invocation

**Manual**:
```bash
Read .claude/docs/workflows/feature-implementation.md
```

**Auto-trigger**: Quando `compliance-feature-creation` completa

---

## Notes

- **Max 3 review iterations** per PR (evita loop infinito)
- **Escalation path**: Se blockers persistono dopo 3 review → manual intervention
- **Cross-repo sync**: Per feature full-stack, coordinate BE + FE PRs via `@scrum_master_casazen`
