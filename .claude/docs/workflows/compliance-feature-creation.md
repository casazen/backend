# Compliance-Driven Feature Creation

**Agents**: `@regulatory_agent` → `@analyzer_agent` → `@scrum_master_casazen`
**Output**: GitHub issues pronte per implementazione

---

## Flow

1. **Regulatory Update** → `@regulatory_agent` aggiorna `.claude/context/regulations/`
2. **Gap Analysis** → `@analyzer_agent` confronta norme vs codebase
3. **Competitive Research** → WebSearch competitor features (Lodgify, Guesty, Hostaway)
4. **Feature Planning** → Consolida gap + insights, prioritizza
5. **Issue Creation** → `@scrum_master_casazen` apre issue su GitHub (FE + BE)

---

## Prerequisites

Prima di iniziare:
- Verifica se esiste `.claude/context/planning/product-roadmap.md`
- Verifica epics attive: `gh issue list --label epic --state open`
- Se mancano → trigger strategic planning refinement meeting (use `@architect` + `@product_owner`)

---

## Step 1: Regulatory Update

**Agent**: `@regulatory_agent`

**Actions**:
- WebSearch normative italiane 2026 (ministeroturismo.gov.it, gazzettaufficiale.it, agenziaentrate.gov.it)
- Aggiorna `.claude/context/regulations/*.md` con nuove norme
- Tag: `scope=national|regional|european`, `status=in_force|pending`, `urgency=immediate|upcoming_deadline`

**Taxonomy**: vedi `.claude/context/agent-guides/classify_topic.md`

---

## Step 2: Gap Analysis

**Agent**: `@analyzer_agent`

**Actions**:
- Leggi regulations updated in Step 1
- Grep/Glob codebase per funzionalità esistenti
- Identifica gap: cosa manca per compliance
- Classifica gap per severità (🔴 CRITICAL, 🟡 HIGH, 🟢 MEDIUM)

**Output**: Lista gap con:
- Normativa di riferimento
- Funzionalità mancante/incompleta
- File codebase coinvolti
- Sanzioni/scadenze

---

## Step 3: Competitive Research

**Actions**:
- WebSearch: "Lodgify [feature]", "Guesty [feature]", "Hostaway [feature]"
- Identifica best practices, UI patterns, automation disponibili
- Confronta gap con competitor

**Output**: Feature matrix (cosa offrono i competitor, cosa manca a CasaZen)

---

## Step 4: Feature Planning

**Actions**:
- Consolida gap analysis + competitive insights
- Priorità: compliance deadline > severity > competitor pressure
- Scope: FE vs BE vs Full-stack
- Stima effort: S (1-2 giorni), M (3-5 giorni), L (1-2 settimane), XL (>2 settimane)
- Dipendenze: infrastruttura, API esterne, database migration

---

## Step 5: Issue Creation

**Agent**: `@scrum_master_casazen`

**Actions**: Per ogni feature:
- Apri issue su `casazen/backend` se richiede API/DB/Services
- Apri issue su `casazen/frontend` se richiede UI/UX
- Cross-link: "Related: casazen/frontend#X" se full-stack
- Label: `compliance`, `priority:critical|high|medium`, `scope:backend|frontend|fullstack`, `effort:S|M|L|XL`
- Milestone: scadenza normativa (se presente)

**Issue Template**:
```markdown
**Compliance**: [Normativa riferimento]
**Scadenza**: [Data] (se applicabile)
**Sanzioni**: [Dettagli]

## Gap Identificato
[Descrizione gap]

## Competitor Benchmark
- Lodgify: [cosa offre]
- Guesty: [cosa offre]

## Tasks
- [ ] Backend: [dettagli]
- [ ] Frontend: [dettagli]
- [ ] Testing: [dettagli]
- [ ] Documentation: [dettagli]

## Acceptance Criteria
[Criteri compliance]

Related: casazen/[repo]#[issue]
```

---

## Invocation

**Manual**:
```bash
# Leggi questo file in contesto
Read .claude/docs/workflows/compliance-feature-creation.md

# Oppure usa skill (se configurato)
/compliance-feature
```

**Frequency**: Mensile o quando cambiano normative

---

## Output Atteso

- N issue su `casazen/backend`
- M issue su `casazen/frontend`
- Issue prioritizzate per scadenza compliance
- Cross-linking FE/BE per feature full-stack
- Regulatory context linkato in ogni issue
