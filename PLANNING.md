# CasaZen - Planning Process

This document is the **authoritative guide** for how development work is planned in CasaZen. It covers backlog creation, regulatory gap analysis, product roadmapping, and sprint planning.

> **Related**: [DEVELOPMENT.md](./DEVELOPMENT.md) — how implementation begins after planning is complete.

---

## Overview

Planning in CasaZen is driven by two inputs:
1. **Italian regulatory requirements** — compliance obligations with legal deadlines
2. **Product vision** — features that improve the platform beyond compliance

Planning produces GitHub Issues. Implementation consumes them.

```
Italian Regulations (monthly scan)
    └── Gap Analysis (vs current codebase)
        └── Competitive Research (Lodgify, Guesty, Hostaway)
            └── Prioritized Backlog (GitHub Issues)
                └── → DEVELOPMENT.md takes over
```

---

## How to Start Planning

### Quick Start — Full Planning Workflow

```bash
# Invoke the full compliance-driven planning workflow
/compliance-feature
```

This automatically:
1. Checks if a product roadmap and epics exist
2. If not → runs a **strategic refinement meeting** (in-memory) between `@product_owner` and `@architect`
3. Updates Italian regulations from official sources
4. Runs gap analysis on the codebase
5. Researches what competitors (Lodgify, Guesty, Hostaway) offer
6. Creates prioritized GitHub Issues

---

## Step-by-Step Planning Process

### Step 0: Check prerequisites

Before planning, verify what already exists:

```bash
# Check if product roadmap exists
ls .claude/context/planning/

# Check existing epics
gh issue list --label epic --state open

# Check open issues (backlog)
gh issue list --state open --json number,title,labels | jq .
```

- **No roadmap + no epics** → Start from Step 1 (Strategic Planning)
- **Roadmap exists, no issues** → Start from Step 3 (Regulatory Update)
- **Issues exist** → Backlog is ready, go to [DEVELOPMENT.md](./DEVELOPMENT.md)

---

### Step 1: Strategic Planning (Refinement Meeting)

Run when there is no product roadmap or no active epics.

**Agents involved** (in-memory discussion, no intermediate files):
- `@product_owner`: Vision, personas, strategic goals, epic candidates
- `@architect`: Technical feasibility, architecture decisions, effort estimates, risks
- `@scrum_master_casazen`: Consolidation, roadmap finalization, epic creation on GitHub

**Output**:
- `.claude/context/planning/product-roadmap.md` — consolidated vision + technical plan + roadmap
- Epic issues on GitHub (with label `epic`)

**Manual invocation**:
```bash
# Read the planning workflow and execute
Read .claude/docs/workflows/compliance-feature-creation.md
# It will auto-trigger the refinement meeting if roadmap is missing
```

**Epic structure**:
Epics represent major functional areas, e.g.:
- Italian Regulatory Compliance (CIN, Alloggiati Web, Tourist Tax, GDPR)
- OTA Integration Platform
- Property & Booking Management
- Payment & Fiscal Reporting
- Guest Experience

---

### Step 2: Verify Roadmap

After the refinement meeting, confirm the roadmap exists and epics are created:

```bash
# Check roadmap was created
cat .claude/context/planning/product-roadmap.md

# Check epics are on GitHub
gh issue list --label epic --state open
```

---

### Step 3: Regulatory Update (Monthly)

**Agent**: `@regulatory_agent`

Scans official Italian sources for new or updated short-term rental regulations:
- `ministeroturismo.gov.it`
- `gazzettaufficiale.it`
- `agenziaentrate.gov.it`
- Regional governments (Regioni)
- EU sources (for OTA obligations, GDPR)

**Output**: Updated files in `.claude/context/regulations/`

```
.claude/context/regulations/
  cin.md              CIN codes (mandatory from 01/01/2025)
  alloggiati.md       Police check-in reporting (within 24h)
  imposta_soggiorno.md Tourist tax (1,409+ municipalities)
  fiscale.md          Tax regime, cedolare secca, 21% withholding
  ota_normativa.md    OTA platform obligations (DAC7, EU Reg 2024/1028)
  gdpr.md             GDPR data protection
  sicurezza.md        Safety requirements
  regionale.md        Regional regulations
```

**Manual trigger**:
```bash
gh workflow run regulatory-agents.yml
```

**Schedule**: Runs automatically on the 1st of each month at 08:00 UTC.

---

### Step 4: Gap Analysis

**Agent**: `@analyzer_agent`

Compares updated regulations against the current codebase. Identifies:

| Status | Meaning |
|---|---|
| MISSING | Feature not implemented at all |
| PARTIAL | Feature exists but incomplete |
| OUTDATED | Feature implemented but doesn't match current law |
| COMPLIANT | Feature fully implemented and current |

Priority levels:
| Priority | Trigger |
|---|---|
| 🔴 CRITICAL | Legal deadline within 30 days, or criminal/major fines |
| 🟡 HIGH | Legal deadline within 90 days, or significant fines |
| 🟢 MEDIUM | Compliance gap with no immediate deadline |
| ⚪ LOW | Best practice, competitive gap |

**Output**: Gap analysis report in `.claude/context/gap-analysis-YYYY-MM-DD.md`

---

### Step 5: Competitive Research

**Actions**:
- WebSearch: Lodgify, Guesty, Hostaway features for each compliance gap
- Identify best practices and UI patterns
- Compare CasaZen feature matrix vs competitors

This ensures CasaZen implements compliance in a way that matches or exceeds what established platforms offer.

---

### Step 6: Backlog Creation

**Agent**: `@scrum_master_casazen` (or `@github_agent` for pure compliance issues)

For each gap identified:

**Priority order** (P0 = highest):
- **P0** — Critical compliance gaps (immediate legal risk)
- **P1** — High compliance gaps (near-term deadline)
- **P2** — Product features (competitive, roadmap-driven)
- **P3** — Nice-to-have (low urgency)

Issue labels:
```
compliance        Italian regulatory requirement
priority:critical P0 issues
priority:high     P1 issues
priority:medium   P2 issues
priority:low      P3 issues
scope:backend     Backend only
scope:frontend    Frontend only
scope:fullstack   Both repos required
effort:S          1-2 days
effort:M          3-5 days
effort:L          1-2 weeks
effort:XL         >2 weeks
epic              Top-level epic (groups related issues)
```

**Issue template** (compliance issues):

```markdown
**Compliance**: [Normativa reference — e.g., D.L. 145/2023 Art. 13-ter]
**Deadline**: [Date if applicable]
**Penalties**: [Fine details]

## Gap Identified
[What is missing or incomplete in the current codebase]

## Competitor Benchmark
- Lodgify: [what they offer]
- Guesty: [what they offer]

## Tasks
- [ ] Backend: [details]
- [ ] Frontend: [details]
- [ ] Testing: [details]
- [ ] Documentation: [details]

## Acceptance Criteria
[Specific measurable criteria]

Related: casazen/frontend#<N> (if full-stack)
```

**Maximum 10 issues per planning run** (to avoid flooding the backlog).

---

## Planning Scenarios

### Scenario A: First Run (No Planning Exists)

```
1. /compliance-feature
   → No roadmap detected
   → Refinement Meeting (in-memory):
       @product_owner: Vision, personas, strategic goals
       @architect:     Feasibility, architecture, risks
       @scrum_master_casazen: Roadmap + Epic creation
   → .claude/context/planning/product-roadmap.md created
   → Epic issues created on GitHub

2. /compliance-feature continues:
   → @regulatory_agent updates regulations
   → @analyzer_agent runs gap analysis
   → Competitive research
   → GitHub Issues created (linked to epics)

3. Backlog ready → /feature-implementation starts
```

### Scenario B: Subsequent Runs (Roadmap Exists)

```
1. /compliance-feature
   → Roadmap exists → skip refinement meeting
   → @regulatory_agent updates regulations (new/changed only)
   → @analyzer_agent re-runs gap analysis
   → New issues created for new gaps

2. /feature-implementation continues implementing
```

### Scenario C: Emergency Compliance Issue

When a new regulation is published that requires immediate action:

```bash
# Manual regulatory scan (doesn't wait for monthly schedule)
gh workflow run regulatory-agents.yml

# Or invoke directly
/compliance-feature
```

Flag the resulting issue as `priority:critical` and assign a milestone with the legal deadline.

### Scenario D: Sprint Planning Meeting

To run a planning review before a sprint:

```bash
# Check current backlog state
gh issue list --state open --json number,title,labels,milestone | jq .

# Check what was completed recently
gh issue list --state closed --json number,title,closedAt | jq .

# Run contract audit to find FE/BE misalignments
/contract-audit
```

---

## Maintaining the Roadmap

The roadmap lives at `.claude/context/planning/product-roadmap.md`. It contains:
- Product vision and personas
- Strategic goals (12-month horizon)
- Active epics with status
- Technical architecture decisions

Update it when:
- A major new epic is completed
- Strategic direction changes
- New regulatory requirements change the compliance roadmap
- After a retrospective reveals gaps in the plan

---

## GitHub Workflows for Planning

| Workflow | Schedule | Manual Trigger |
|---|---|---|
| `regulatory-agents.yml` | 1st of month, 08:00 UTC | `gh workflow run regulatory-agents.yml` |
| `daily-development.yml` (Mode B) | Daily 08:00 UTC (if no issues) | `gh workflow run daily-development.yml -f force_new_issues=true` |

---

## Planning Artifacts

| Artifact | Location | Purpose |
|---|---|---|
| Product Roadmap | `.claude/context/planning/product-roadmap.md` | Vision + architecture + roadmap |
| Regulations | `.claude/context/regulations/*.md` | Italian regulatory reference |
| Gap Analysis | `.claude/context/gap-analysis-YYYY-MM-DD.md` | Latest compliance gap report |
| Regulatory Index | `.claude/context/_index.md` | Classification of 8 regulatory macro-topics |
| Last Updated | `.claude/context/_last_updated.json` | When regulatory agent last ran |

---

## Regulatory Compliance Topics

CasaZen tracks 8 regulatory macro-topics for Italian short-term rentals:

| Topic | Key Obligation | Status |
|---|---|---|
| CIN Codes | Mandatory registration code on all listings | Mandatory from 01/01/2025 |
| Alloggiati Web | Police check-in report within 24h of arrival | Permanent obligation |
| Tourist Tax | Municipal tax per guest per night | Varies by municipality (1,409+) |
| Cedolare Secca | Flat tax (21%/26%) + 21% OTA withholding | Modified 01/01/2026 |
| OTA Obligations | DAC7 reporting, EU Reg 2024/1028 | In force from 20/05/2026 |
| GDPR | Guest data protection | Permanent obligation |
| Safety | Smoke detectors, fire extinguishers, signage | Permanent obligation |
| Regional Rules | Varies by region | Evolving (Constitutional Court ruling) |

---

**Last Updated**: 2026-05-02
**Maintained By**: CasaZen Development Team
**Related Docs**: [DEVELOPMENT.md](./DEVELOPMENT.md) | [CLAUDE.md](./CLAUDE.md) | `.claude/README.md`
