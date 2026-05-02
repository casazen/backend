# Workflow: Compliance-Driven Feature Creation

**Agents**: `@regulatory_agent` → `@analyzer_agent` → `@scrum_master_casazen`
**Supporting** (if roadmap missing): `@product_owner`, `@architect`

Invoked via: `/compliance-feature` skill

---

## Flow

```
Roadmap + Epics exist?
  NO  → Refinement Meeting (in-memory) → product-roadmap.md + Epic issues
  YES → skip
↓
@regulatory_agent → update .claude/context/regulations/
↓
@analyzer_agent → gap analysis vs codebase
↓
Competitive research (Lodgify, Guesty, Hostaway)
↓
@scrum_master_casazen → create GitHub issues (max 10, linked to epics)
```

---

## Step 0: Prerequisites

```bash
# Check roadmap
Test-Path .claude/context/planning/product-roadmap.md

# Check epics
gh issue list --label epic --state open
```

If roadmap or epics are missing → trigger **Refinement Meeting** (in-memory):
- `@product_owner`: vision, personas, strategic goals, epic candidates
- `@architect`: technical feasibility, architecture decisions, effort, risks
- `@scrum_master_casazen`: consolidation → writes `product-roadmap.md` + creates epic issues on GitHub

Output: `.claude/context/planning/product-roadmap.md` + epic GitHub issues.

---

## Step 1: Regulatory Update (`@regulatory_agent`)

Sources:
- `ministeroturismo.gov.it`, `gazzettaufficiale.it`, `agenziaentrate.gov.it`
- `normattiva.it`, `bdsr.mef.gov.it`, EUR-Lex, European Commission

Actions:
- WebSearch + WebFetch for each of 8 regulatory topics
- Classify via `.claude/context/agent-guides/classify_topic.md`
- Update `.claude/context/regulations/*.md`
- Update `_index.md` + `_last_updated.json`

Tags per regulation: `scope=national|regional|european`, `status=in_force|pending`, `urgency=immediate|upcoming_deadline`

---

## Step 2: Gap Analysis (`@analyzer_agent`)

Actions:
- Read updated regulations (Step 1)
- Grep/Glob codebase for existing features
- Classify gap: `MISSING` | `PARTIAL` | `OUTDATED` | `COMPLIANT`
- Prioritize: 🔴 CRITICAL | 🟡 HIGH | 🟢 MEDIUM | ⚪ LOW

Output: gap list with regulation reference, missing feature, affected files, sanctions, deadlines.

---

## Step 3: Competitive Research

```
WebSearch: "Lodgify [feature]" / "Guesty [feature]" / "Hostaway [feature]"
```

Output: feature matrix (what competitors offer vs. what CasaZen lacks).

---

## Step 4: Feature Planning

Priority order: `compliance deadline` > `severity` > `competitor pressure`
Effort estimates: S (1-2 days), M (3-5 days), L (1-2 weeks), XL (>2 weeks)
Scope per issue: `backend` | `frontend` | `fullstack`

---

## Step 5: Issue Creation (`@scrum_master_casazen`)

Max 10 issues per run. Create CRITICAL first.

```bash
gh issue create --repo casazen/backend \
  --title "[COMPLIANCE] <title>" \
  --label "compliance,priority:critical,scope:backend,effort:M" \
  --milestone "<regulatory-deadline-date>" \
  --body "..."
```

Issue body template:
```markdown
**Compliance**: [Regulation reference]
**Deadline**: [Date if applicable]
**Penalties**: [Details]

## Gap Identified
[What is missing or incomplete]

## Competitor Benchmark
- Lodgify: [feature]
- Guesty: [feature]

## Tasks
- [ ] Backend: [details]
- [ ] Frontend: [details]
- [ ] Testing: [details]
- [ ] Documentation: [details]

## Acceptance Criteria
[Specific measurable criteria]

Related: casazen/frontend#<N>
```

Link FE issues cross-repo: `Related: casazen/frontend#<N>`.

---

## Output

- `.claude/context/planning/product-roadmap.md` (created if missing)
- Epic issues on GitHub (created if missing)
- `.claude/context/regulations/` updated
- `.claude/context/gap-analysis-YYYY-MM-DD.md` created
- N GitHub Issues (prioritized, linked to epics)
