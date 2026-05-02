---
name: compliance-feature
description: Full compliance-driven planning workflow. Scans Italian regulations, runs gap analysis vs. codebase, benchmarks competitors, and creates a prioritized GitHub issue backlog. If no product roadmap exists, runs a strategic refinement meeting first (product_owner + architect + scrum_master_casazen in-memory).
---

# Compliance-Driven Feature Creation

Full pipeline: regulations → gap analysis → competitor benchmark → GitHub issues.

## Agent Chain

```
[Prerequisites check]
  roadmap missing? → Refinement Meeting (in-memory)
    @product-owner (vision + personas + epics)
    @architect (feasibility + effort + risks)
    @scrum-master-casazen (consolidate → product-roadmap.md + epic issues)

@regulatory-agent
  → WebSearch + WebFetch 8 regulatory topics
  → update .claude/context/regulations/*.md
  → produce regulatory summary

@analyzer-agent (receives summary from regulatory-agent)
  → grep/glob codebase
  → classify: MISSING / PARTIAL / OUTDATED / COMPLIANT
  → produce gap report with priorities

[Competitive research — inline]
  → WebSearch: Lodgify, Guesty, Hostaway per gap found

@scrum-master-casazen (receives gap report)
  → create GitHub Issues (max 10, CRITICAL first)
  → link FE↔BE issues cross-repo
```

## Step 0 — Prerequisites

```bash
# Check roadmap
ls .claude/context/planning/product-roadmap.md 2>/dev/null && echo "EXISTS" || echo "MISSING"

# Check active epics
gh issue list --label epic --state open --json number,title
```

**If roadmap or epics missing** → run Refinement Meeting before proceeding:

**Refinement Meeting protocol (in-memory)**:
1. `@product-owner`: articulate vision, define personas, propose 4-6 epic candidates with strategic justification
2. `@architect`: evaluate each candidate for feasibility, estimate effort (S/M/L/XL), identify technical risks and dependencies
3. `@scrum-master-casazen`: synthesize → write `product-roadmap.md` → create epic issues on GitHub

No intermediate files during the meeting — only the final roadmap is persisted.

## Step 1 — Regulatory Update (`@regulatory-agent`)

Sources to check:
- `ministeroturismo.gov.it`, `gazzettaufficiale.it`, `agenziaentrate.gov.it`
- `normattiva.it`, `bdsr.mef.gov.it`
- `eur-lex.europa.eu` (EU directives + regulations)

8 regulatory topics:
1. Codice CIN
2. Comunicazione Alloggiati Web
3. Imposta di Soggiorno
4. Regime Fiscale / Cedolare Secca
5. Normativa OTA e Intermediari (DAC7, EU Reg 2024/1028)
6. GDPR e Protezione Dati
7. Sicurezza e Requisiti Strutturali
8. Normativa Regionale

For each topic:
- `WebSearch` → find recent updates
- `WebFetch` → extract full regulation text
- Classify via `.claude/context/agent-guides/classify_topic.md`
- Update `.claude/context/regulations/<topic>.md`
- Tag: `scope=national|regional|european`, `status=in_force|pending`, `urgency=immediate|upcoming_deadline`

Update `_index.md` + `_last_updated.json`.

**Handoff artifact**: Regulatory summary (new/changed regulations with impact on CasaZen).

## Step 2 — Gap Analysis (`@analyzer-agent`)

Input: regulatory summary from Step 1.

Actions:
- Read updated regulations
- `Grep` + `Glob` codebase for existing implementations (keywords: CIN, alloggiati, imposta, soggiorno, GDPR, consent, cedolare)
- Classify each regulatory requirement:
  - **MISSING**: no implementation
  - **PARTIAL**: started but incomplete
  - **OUTDATED**: present but not aligned with current law
  - **COMPLIANT**: fully implemented

Priority assignment:
- 🔴 CRITICAL: obligation in force with immediate penalties
- 🟡 HIGH: obligation in force, penalties foreseeable
- 🟢 MEDIUM: obligation in force, moderate risk
- ⚪ LOW: best practice or future obligation

**Handoff artifact**: Structured gap report (priority, regulation ref, missing feature, affected files, sanctions, deadlines).

## Step 3 — Competitive Research

For each CRITICAL + HIGH gap found in Step 2:
```
WebSearch: "Lodgify [feature name]"
WebSearch: "Guesty [feature name]"
WebSearch: "Hostaway [feature name]"
```

Build feature matrix: what competitors offer vs. what CasaZen lacks.

## Step 4 — Feature Planning

Consolidate gap report + competitive insights:
- Priority: `compliance deadline` > `severity` > `competitor pressure`
- Effort: S (1-2d) / M (3-5d) / L (1-2w) / XL (>2w)
- Scope: `backend` | `frontend` | `fullstack`
- Dependencies: DB migration, external API, cross-repo

## Step 5 — Issue Creation (`@scrum-master-casazen`)

Input: gap report + competitive insights from Steps 2-3.

Max 10 issues per run. CRITICAL first.

```bash
gh issue create --repo casazen/backend \
  --title "[COMPLIANCE] <concise title>" \
  --label "compliance,priority:critical,scope:backend,effort:M" \
  --milestone "<regulatory-deadline-date>" \
  --body "<template below>"
```

Issue body template:
```markdown
## User Story
As a property owner, I want [feature] so that [compliance benefit].

## Regulatory Context
- **Reference**: [law/decree, e.g. D.L. 145/2023 art. 13-ter]
- **Deadline**: [date]
- **Penalties**: [details]

## Current State
[What exists in the codebase today]

## Competitor Benchmark
- Lodgify: [what they offer]
- Guesty: [what they offer]

## Implementation Requirements
- [ ] Backend: [details]
- [ ] Frontend: [details]
- [ ] Testing: [details]
- [ ] Documentation: [details]

## Acceptance Criteria
- [ ] [Specific measurable criterion]

Related: casazen/frontend#<N>
```

For full-stack features: create paired issue on `casazen/frontend`, cross-link bidirectionally.

## Output

- `.claude/context/planning/product-roadmap.md` (created if missing)
- Epic issues on GitHub (created if missing)
- `.claude/context/regulations/*.md` updated
- `.claude/context/gap-analysis-YYYY-MM-DD.md` created
- N GitHub Issues (prioritized, linked to epics)

**Next**: invoke `feature-implementation` skill to start implementing P0/P1 issues.

**Cadence**: Monthly (automated via `regulatory-agents.yml`) or ad-hoc on new regulation.

## Full workflow spec

`.claude/workflows/compliance-feature-creation.md`
