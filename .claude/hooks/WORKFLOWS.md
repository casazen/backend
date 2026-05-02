# CasaZen Workflow Automation

> **Architettura ottimizzata**: Processi comuni riutilizzabili + Workflow specializzati

---

## 📋 Struttura

```
.claude/hooks/
├── common/
│   └── review-process.md          # ⚙️ Processo review riutilizzabile (max 3 iter)
├── contract-audit.md              # 🔍 FE/BE sync gap analysis
├── feature-implementation.md      # 🚀 Issue → PR con review cycle
├── compliance-feature-creation.md # 📜 Regulatory → Feature backlog
├── README.md                      # 📖 Preprocessing hooks documentation
└── WORKFLOWS.md                   # 📖 Questa documentazione
```

---

## 🎯 Workflow Disponibili

### 1. Contract Audit (FE/BE Sync)

**File**: `contract-audit.md`
**Agente**: `@scrum_master_casazen`
**Quando usarlo**: Periodicamente (es. ogni 2 settimane) o prima di major release

**Cosa fa**:
1. Legge backend (API, DTOs, controllers)
2. Legge frontend (types, api client, queries)
3. Identifica disallineamenti (types, endpoints, docs)
4. Apre GitHub Issue per ogni gap (categorizzate per severità)
5. Crea issue riepilogativa

**Output**:
- N issue su `casazen/frontend` (types, API client, hooks)
- M issue su `casazen/backend` (docs)
- 1 issue riepilogativa su backend

**Invocazione**:
```bash
# Manuale (carica file in contesto)
Read .claude/hooks/contract-audit.md

# Oppure via skill (se configurato)
/contract-audit
```

---

### 2. Feature Implementation (Issue → PR)

**File**: `feature-implementation.md`
**Agente principale**: `@scrum_master_casazen`
**Collaboratori**: `@feature_developer`, `@code_reviewer`, `@release_manager`
**Quando usarlo**: Quando ci sono issue da implementare (backlog grooming)

**Cosa fa**:
0. **Prerequisites Check**: Verifica issue aperte
   - Se nessuna issue → Auto-trigger `/compliance-feature` per generare backlog
1. Analizza issue aperte (FE + BE, escluse epics)
2. Raggruppa feature correlate
3. Crea piano implementazione (BE-first, FE-first, o parallelo)
4. Coordina `@feature_developer` per implementare
5. Avvia review cycle (usa `common/review-process.md`)
6. Gestisce merge via `@release_manager`

**Output**:
- Piano implementazione per ogni feature group
- PR aperte e revisionate (FE + BE)
- Issue chiuse dopo merge
- Report finale (APPROVED o ESCALATION)

**Auto-trigger**: Se backlog vuoto, esegue automaticamente `/compliance-feature`

**Invocazione**:
```bash
# Manuale
Read .claude/hooks/feature-implementation.md

# Oppure via skill
/feature-implementation
```

---

### 3. Compliance-Driven Feature Creation

**File**: `compliance-feature-creation.md`
**Agenti**: `@product_owner`, `@architect`, `@scrum_master_casazen`, `@regulatory_agent`, `@analyzer_agent`
**Quando usarlo**: Mensile (monitoring normativo) o ad-hoc (nuova legge pubblicata)

**Cosa fa**:
0. **Planning & Epics Check**: Verifica roadmap e epics esistenti
   - Se mancano → **Refinement Meeting** (in-memory discussion):
     - `@product_owner`: Vision, personas, strategic goals, epic candidates
     - `@architect`: Technical feasibility, architecture, effort, risks
     - `@scrum_master_casazen`: Consolidamento, roadmap finale, epic creation
   - Output: Product roadmap consolidato + Epic issues su GitHub
1. Aggiorna normative italiane (CIN, Alloggiati Web, Tourist Tax, GDPR)
2. Analizza gap tra norme e codebase
3. Ricerca competitor (cosa fanno Lodgify, Guesty, Hostaway)
4. Verifica feature esistenti in codebase
5. Crea backlog prioritizzato (P0 compliance → P3 nice-to-have)
6. Apre GitHub Issue via `@scrum_master_casazen` (linkate ad epics)

**Output**:
- `.claude/context/planning/product-roadmap.md` (se non esisteva - consolidato)
- Epic issues su GitHub (se non esistevano)
- `.claude/context/regulations/` aggiornato
- `.claude/context/gap-analysis-YYYY-MM-DD.md`
- Backlog feature (con priorità, effort, scope)
- N issue create su GitHub (pronte per implementazione, linkate ad epics)

**Note**: Refinement meeting avviene in-memory (no file intermedi), solo roadmap finale scritto su disco

**Invocazione**:
```bash
# Manuale
Read .claude/hooks/compliance-feature-creation.md

# Oppure via skill
/compliance-feature
```

---

## ⚙️ Processo Comune: Code Review

**File**: `common/review-process.md`
**Quando si usa**: Automaticamente in `feature-implementation.md` e altri workflow

**Caratteristiche**:
- ✅ Max 3 iterazioni review per PR
- ✅ Severity-based findings (🔴 🟡 🟢 ⚪)
- ✅ Anti-loop: dopo 3 iter, escalation automatica
- ✅ Review incrementale: solo modifiche delta, non tutto

**Non invocare direttamente** - è un processo riutilizzabile referenziato da altri workflow.

---

## 🔄 Flusso Tipico

### Scenario 1: Primo Avvio (No Planning/Epics)

```
1. /feature-implementation
   → Nessuna issue → Auto-trigger /compliance-feature

2. /compliance-feature (triggered)
   → Nessun planning → Refinement Meeting (in-memory):
     - @product_owner: Vision & epics
     - @architect: Feasibility & risks
     - @scrum_master_casazen: Roadmap consolidato + Epic creation
   → Crea product-roadmap.md
   → Crea 5 epic issues su GitHub

3. /compliance-feature (continua)
   → Aggiorna norme + gap analysis + competitive research
   → Crea feature issues (linkate ad epics)

4. /feature-implementation (resume)
   → Implementa feature P0 (critical) prima
   → Review cycle + merge

5. Deploy to production
```

### Scenario 2: Run Successivi (Planning Esiste)

```
1. /compliance-feature
   → Planning esiste → SKIP refinement meeting
   → Aggiorna norme + gap analysis + competitive research
   → Crea nuove feature issues (sotto epics esistenti)

2. /feature-implementation
   → Issue esistono → Procedi direttamente
   → Implementa feature prioritizzate
   → Review cycle + merge

3. Deploy to production
```

### Scenario 3: Sprint Planning con Contract Audit

```
1. /contract-audit
   → Identifica disallineamenti FE/BE
   → Crea issue sync

2. /feature-implementation
   → Implementa tutte le issue sync
   → + feature dal backlog prodotto

3. Review + merge
```

### Scenario 3: Pre-Release Audit

```
1. /contract-audit
   → Verifica che FE e BE siano allineati
   → 0 issue = OK per release
   → N issue = Fix prima di release

2. Se issue trovate → /feature-implementation
```

---

## 📊 Metriche & Monitoring

### KPI da Tracciare

- **Contract Audit**:
  - Disallineamenti FE/BE per categoria (types, API, docs)
  - Tempo medio risoluzione issue sync

- **Feature Implementation**:
  - Issue chiuse per sprint
  - Review iterations media (target: <2)
  - Escalation rate (target: <5%)

- **Compliance**:
  - Compliance score (% gap risolti vs identificati)
  - Time-to-compliance (giorni da normativa a deploy)
  - Competitive gap (feature mancanti vs competitor)

---

## 🛠️ Customizzazione

### Aggiungere Nuovo Workflow

1. Crea `.claude/hooks/<workflow-name>.md`
2. Se usa review, referenzia `common/review-process.md`:
   ```markdown
   ## Review
   Segui processo standard: `@.claude/hooks/common/review-process.md`
   ```
3. Aggiungi sezione in questo WORKFLOWS.md
4. Opzionale: crea skill invocabile in `.claude/skills/`

### Modificare Review Process

Edita `common/review-process.md`:
- Cambia max iterazioni (default 3)
- Aggiungi severity level custom
- Modifica criteri APPROVED/ESCALATION

**Attenzione**: modifiche si propagano a tutti i workflow che lo usano.

---

## 📚 Riferimenti

- **GitHub Flow**: `.claude/rules/github-flow-mandatory.md`
- **Code Style**: `.claude/rules/code-style.md`
- **Security**: `.claude/rules/security.md`
- **Review Guidelines**: `REVIEW.md` (root)
- **Project Overview**: `CLAUDE.md` (root)

---

## 🔐 Permessi GitHub

I workflow richiedono GitHub MCP con permessi:
- ✅ Read: Issues, PR, Code
- ✅ Write: Issues, PR comments
- ❌ No merge diretto (solo `@release_manager`)

Configurazione: `.claude/settings.json` → MCP servers

---

**Last Updated**: 2026-05-01
**Maintained By**: CasaZen Development Team
