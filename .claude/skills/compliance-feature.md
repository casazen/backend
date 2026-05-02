---
name: compliance-feature
description: Run the full compliance-driven planning workflow. Updates Italian regulations, runs gap analysis vs. codebase, researches competitors, and creates a prioritized GitHub issue backlog. If no product roadmap exists, runs a strategic refinement meeting first.
invocable: true
---

# Compliance-Driven Feature Creation

## Agents

| Agent | Role |
|---|---|
| `@product_owner` | Vision, personas, epics (only if roadmap missing) |
| `@architect` | Feasibility, architecture, effort (only if roadmap missing) |
| `@regulatory_agent` | Scan Italian regulations (8 topics) |
| `@analyzer_agent` | Gap analysis: MISSING / PARTIAL / OUTDATED / COMPLIANT |
| `@scrum_master_casazen` | Issue creation on GitHub (max 10, linked to epics) |

## Prerequisites

```bash
# Check if roadmap exists
Test-Path .claude/context/planning/product-roadmap.md

# Check epics
gh issue list --label epic --state open
```

**If roadmap or epics are missing** → run Refinement Meeting (in-memory):
- `@product_owner`: vision + personas + strategic goals + epic candidates
- `@architect`: feasibility + architecture + effort + risks
- `@scrum_master_casazen`: consolidate → write `product-roadmap.md` + create epic issues

## Execution Steps

**1. Regulatory Update** (`@regulatory_agent`):
- WebSearch + WebFetch: ministeroturismo.gov.it, gazzettaufficiale.it, agenziaentrate.gov.it, normattiva.it, EUR-Lex
- Classify via `.claude/context/agent-guides/classify_topic.md`
- Update `.claude/context/regulations/*.md`, `_index.md`, `_last_updated.json`

**2. Gap Analysis** (`@analyzer_agent`):
- Read updated regulations
- Grep/Glob codebase for existing features
- Classify: MISSING | PARTIAL | OUTDATED | COMPLIANT
- Prioritize: 🔴 CRITICAL | 🟡 HIGH | 🟢 MEDIUM | ⚪ LOW

**3. Competitive Research**:
- WebSearch: "Lodgify [feature]", "Guesty [feature]", "Hostaway [feature]"
- Build feature matrix: what competitors offer vs. what CasaZen lacks

**4. Feature Planning**:
- Priority: compliance deadline > severity > competitor pressure
- Effort: S (1-2 days) / M (3-5 days) / L (1-2 weeks) / XL (>2 weeks)
- Scope: backend | frontend | fullstack

**5. Issue Creation** (`@scrum_master_casazen`):
```bash
gh issue create --repo casazen/backend \
  --title "[COMPLIANCE] <title>" \
  --label "compliance,priority:critical,scope:backend,effort:M" \
  --milestone "<deadline>" \
  --body "<template>"
```
Max 10 issues per run. Create CRITICAL first. Cross-link FE↔BE issues.

## Output

- `.claude/context/planning/product-roadmap.md` (created if missing)
- Epic issues on GitHub (created if missing)
- `.claude/context/regulations/` updated
- `.claude/context/gap-analysis-YYYY-MM-DD.md`
- N GitHub Issues (prioritized, linked to epics)

**Next step**: `/feature-implementation` to implement P0/P1 features.

**Cadence**: Monthly or when a new regulation is published.

## Full Workflow Spec

`.claude/workflows/compliance-feature-creation.md`
