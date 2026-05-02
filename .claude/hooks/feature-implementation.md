# Feature Implementation: Issue → PR con Review Cycle

> **Agente principale**: `@scrum_master_casazen`
> **Agenti collaboratori**: `@feature_developer`, `@code_reviewer`, `@release_manager`
> **Accesso**: GitHub MCP

**Obiettivo**: Coordinare implementazione di feature a partire da issue GitHub, gestendo coerenza FE/BE, apertura PR e ciclo di review controllato.

---

## Workflow Overview

```
┌─────────────────┐
│ 0. Prerequisites│ → Verifica issue aperte
│    Check        │   → Se nessuna: TRIGGER compliance-feature-creation
└────────┬────────┘
         │
┌────────▼────────┐
│ 1. Analisi      │ → Issue aperte su GitHub (FE + BE)
│    Issue        │   (escluse epics)
└────────┬────────┘
         │
┌────────▼────────┐
│ 2. Pianificazione│ → Piano implementazione (dipendenze, ordine, API contract)
└────────┬────────┘
         │
┌────────▼────────┐
│ 3. Implementa   │ → @feature_developer crea branch, implementa, apre PR
│    (@feature_   │
│     developer)  │
└────────┬────────┘
         │
┌────────▼────────┐
│ 4. Review       │ → Usa @.claude/hooks/common/review-process.md
│    (@code_      │    (max 3 iterazioni)
│     reviewer)   │
└────────┬────────┘
         │
    ┌────▼────┐
    │ APPROVED│ → @release_manager merge a main
    │    o    │
    │ESCALATION│ → Report problemi residui
    └─────────┘
```

---

## STEP 0 — Prerequisites: Verify Open Issues

### Obiettivo

**CRITICAL**: Prima di procedere con implementazione, verificare se esistono issue aperte. Se non esistono, trigger compliance workflow per crearle.

### Check Open Issues

**Comandi**:
```bash
# Recupera issue aperte (escludendo epics)
gh issue list --state open --repo casazen/backend --json number,title,labels
gh issue list --state open --repo casazen/frontend --json number,title,labels
```

**Filtra**:
- ✅ Include: Issue con label `enhancement`, `bug`, `compliance`, `feature`
- ❌ Exclude: Issue con label `epic` (sono container, non implementabili direttamente)

### Decision Tree

```
Open issues exist? ──NO──> TRIGGER compliance-feature-creation
(excluding epics)           (Step 0.1)
    │
   YES
    │
    ▼
PROCEED to STEP 1 (Analisi Issue)
```

**Regola**: Se **nessuna issue aperta** (escluse epics) → Esegui Step 0.1
Altrimenti → **SKIP a STEP 1**

---

## STEP 0.1 — Trigger Compliance-Driven Feature Creation

**Trigger**: Eseguito SOLO se non ci sono issue aperte da implementare.

### Obiettivo

Invocare workflow `compliance-feature-creation` per generare backlog di issue pronte per implementazione.

**Prompt**:
```markdown
No open issues found for implementation. Triggering compliance-driven feature creation...

Execute compliance-feature-creation workflow:
Read .claude/hooks/compliance-feature-creation.md

This will:
1. ✅ Verify/create strategic planning and epics (if missing)
2. ✅ Update Italian regulatory requirements
3. ✅ Analyze compliance gaps vs codebase
4. ✅ Research competitor features
5. ✅ Create prioritized backlog
6. ✅ Open GitHub issues (ready for implementation)

After completion, resume feature-implementation workflow from STEP 1.
```

### Expected Output

Al termine di compliance-feature-creation:
- ✅ Planning & epics created (if didn't exist)
- ✅ N GitHub issues created (P0 → P3)
- ✅ Issues prioritized and labeled
- ✅ Ready for implementation

**Next**: **RESUME STEP 1** - Analizza le nuove issue create e procedi con implementazione.

---

## STEP 1 — Analisi Issue Aperte

**Prerequisite**: Questo step viene eseguito SOLO se esistono issue aperte (verificato in STEP 0).

### Recupera Issue

Usa GitHub MCP per recuperare:
- Issue aperte su `casazen/frontend` (escluse epics)
- Issue aperte su `casazen/backend` (escluse epics)

### Identifica Correlazioni

Per ogni issue, rileva se:
- ✅ È collegata a un'altra issue (cross-repo)
- ✅ Ha dipendenze (blocca/è bloccata da altre issue)
- ✅ Coinvolge un **contratto API** (endpoint condiviso FE/BE)
- ✅ È parte di una **feature più ampia** (epic)

### Raggruppa Feature

Raggruppa issue correlate in **feature logiche**:

```markdown
## Feature Group: <Nome Feature>

**Epic**: <Link epic se esiste>

### Issue Frontend
- #123: [Sync] Type — Missing interface for BookingDto
- #124: [Sync] API Client — POST /bookings wrong payload

### Issue Backend
- #456: [Sync] Docs — GET /bookings not documented

### Contratto API Coinvolto
- Endpoint: `POST /api/bookings`
- Request: `CreateBookingDto`
- Response: `BookingResponseDto`

### Dipendenze
- #123 dipende da #456 (backend deve esporre DTO prima)
```

---

## STEP 2 — Pianificazione Implementazione

Per ogni feature group, crea un **piano di implementazione**:

### Template Piano

```markdown
## Piano Implementazione: <Feature Name>

### Issue Collegate
- Frontend: #123, #124
- Backend: #456

### Ordine Implementazione
<Scegli strategia basata su dipendenze>

**Opzioni**:
1. **BE-first**: Backend implementa prima, poi frontend consuma
   - ✅ Usa quando: nuovo endpoint, cambio contratto API
   - 📝 Sequenza: BE PR → merge → FE PR → merge

2. **FE-first**: Frontend prepara interfacce, backend implementa dopo
   - ✅ Usa quando: UI mockup pronto, backend può adattarsi
   - 📝 Sequenza: FE PR (con mock) → merge → BE PR → merge

3. **Parallelo**: FE e BE lavorano insieme su contratto concordato
   - ✅ Usa quando: contratto API già definito e stabile
   - 📝 Sequenza: FE PR + BE PR contemporaneamente → merge insieme

**Scelta**: <BE-first/FE-first/Parallelo>
**Motivazione**: <Perché questa strategia?>

### Contratto API (se applicabile)

**Endpoint**: `<METHOD> <PATH>`

**Request**:
```json
{
  "field1": "type",
  "field2": "type"
}
```

**Response**:
```json
{
  "field1": "type",
  "field2": "type"
}
```

**DTO Backend** (C#):
```csharp
public class ExampleDto
{
    public string Field1 { get; set; }
    public int Field2 { get; set; }
}
```

**Interface Frontend** (TypeScript):
```typescript
interface ExampleDto {
  field1: string;
  field2: number;
}
```

### Acceptance Criteria

Dedotti dalle issue:
- [ ] Criterio 1 (da issue #123)
- [ ] Criterio 2 (da issue #124)
- [ ] Criterio 3 (da issue #456)

### Checklist Implementazione

- [ ] Branch creati: `feature/<name>-fe`, `feature/<name>-be`
- [ ] Backend: DTO creato, endpoint implementato, test scritti
- [ ] Frontend: Interface TS creata, API client implementato, hook React Query creato
- [ ] Contratto API validato (schema matching)
- [ ] PR aperte e collegate
- [ ] Issue collegate alle PR (Closes #123)
```

---

## STEP 3 — Coordinamento Implementazione

### Passa Piano a @feature_developer

Invoca `@feature_developer` con il piano di implementazione:

```markdown
@feature_developer, implementa questa feature seguendo il piano:

<Incolla piano da Step 2>

**Task**:
1. Crea branch dedicati per frontend e backend (segui naming: feature/<name>-fe, feature/<name>-be)
2. Implementa modifiche mantenendo allineati FE e BE secondo ordine pianificato
3. Rispetta contratto API definito (endpoint, payload, response)
4. Scrivi test unitari/integration per backend, test per API client frontend
5. Apri PR per frontend e backend
6. Collega PR tra loro (menziona PR cross-repo nella descrizione)
7. Collega PR alle issue (Closes #123, Closes #456)

**Segui**:
- `.claude/rules/github-flow-mandatory.md` (CRITICAL)
- `.claude/rules/code-style.md`
- `.claude/rules/security.md`

**NON**:
- ❌ Non mergeare direttamente a main (solo PR)
- ❌ Non bypassare test
- ❌ Non modificare codice non correlato
```

### Output Atteso da @feature_developer

Dopo implementazione:
- ✅ Branch creati e pushed
- ✅ Codice implementato
- ✅ Test passati (`dotnet test` per BE, `npm test` per FE)
- ✅ PR aperte su GitHub con:
  - Titolo: `feat: <descrizione>` (Conventional Commits)
  - Body: Summary + Test Plan + Closes #issues
  - Link cross-repo se applicabile
  - Label appropriata (frontend/backend/sync)

---

## STEP 4 — Review Cycle

**Segui processo standard**: `@.claude/hooks/common/review-process.md`

### Quick Reference

1. Invoca `@code_reviewer` per validare PR (FE e BE)
2. Se APPROVED → passa a Step 5 (Merge)
3. Se CHANGES_REQUESTED → feedback a `@feature_developer`, max 3 iterazioni
4. Se ESCALATION (3 iterazioni superate) → Report e stop

**Verifiche specifiche per sync FE/BE**:
- ✅ Contratto API rispettato (DTO backend ↔️ Interface TS)
- ✅ Naming consistency (camelCase in TS, PascalCase in C#)
- ✅ Enum values allineati
- ✅ Error handling compatibile

---

## STEP 5 — Merge e Chiusura

### Merge PR

**SOLO `@release_manager` può mergeare a main**.

Invoca `@release_manager`:
```markdown
@release_manager, le seguenti PR sono approvate e pronte per merge:

- Frontend PR: <link>
- Backend PR: <link>

**Verifica**:
- [x] Review approvata
- [x] CI checks passati
- [x] Nessun conflitto
- [x] Issue collegate

**Procedi con merge** (squash + delete branch).
```

### Chiusura Issue

Dopo merge, verifica che le issue siano automaticamente chiuse (keyword "Closes #N" nelle PR).

Se non chiuse automaticamente:
- Chiudi manualmente le issue
- Aggiungi commento: "Fixed in PR #<num> and merged to main"

---

## STEP 6 — Report Finale

Produci report riepilogativo:

```markdown
## Feature Implementation Report: <Feature Name>

**Data**: YYYY-MM-DD
**Coordinatore**: @scrum_master_casazen

### Issue Implementate
- Frontend: #123 ✅, #124 ✅
- Backend: #456 ✅

### PR Merged
- Frontend: casazen/frontend#789 (merged YYYY-MM-DD)
- Backend: casazen/backend#101 (merged YYYY-MM-DD)

### Review Iterations
- Iteration 1: 2 findings (🟡 High) → Fixed
- Iteration 2: APPROVED

### Status Finale
✅ **APPROVED** - Feature implementata e merged to main

### Acceptance Criteria
- [x] Criterio 1
- [x] Criterio 2
- [x] Criterio 3

### Deployment
- Backend: Deployed to staging (auto)
- Frontend: Deployed to staging (auto)

---
🤖 Generated by @scrum_master_casazen
```

---

## Regole di Coordinamento

### Best Practices

- ✅ **Tracciabilità**: Issue → Plan → Branch → Commit → PR → Review → Merge
- ✅ **Source of truth**: Backend definisce contratto API, frontend lo consuma
- ✅ **Test-driven**: Test scritti durante implementazione, non dopo
- ✅ **Small PRs**: Preferisci PR piccole e focalizzate (1 feature = 1 PR)
- ✅ **Parallelismo agenti**: Usa agenti in parallelo quando possibile, ma **un solo agente per ciclo completo** (no sottoprocessi paralleli dentro lo stesso workflow)

### Anti-Patterns

- ❌ **NO loop infiniti**: Max 3 iterazioni review, poi escalation
- ❌ **NO scope creep**: Implementa solo ciò che è nelle issue, niente extra
- ❌ **NO assunzioni**: Se contratto API è ambiguo, chiedi decisione esplicita
- ❌ **NO silent changes**: Ogni modifica deve essere tracciata in issue/PR

### Escalation

Se incontri problemi:
1. **Ambiguità requisiti** → Chiedi chiarimenti su issue GitHub (commenta)
2. **Breaking change necessario** → Apri nuova issue "Breaking Change Proposal"
3. **Review bloccata** → Escalation report dopo 3 iterazioni
4. **Conflitto FE/BE** → Apri issue "Contract Conflict" su entrambe le repo

---

## Output Standard

Al termine del workflow:
- ✅ N issue chiuse (implementate)
- ✅ N PR merged (FE + BE)
- ✅ Report finale con esito APPROVED o ESCALATION
- ✅ Feature deployata su staging (automatico via CI/CD)

---

**Last Updated**: 2026-05-01
**Maintained By**: CasaZen Development Team
