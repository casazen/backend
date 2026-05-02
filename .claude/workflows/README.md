# Workflows

Canonical workflow specifications for CasaZen. These are the authoritative process docs.

Each workflow is invoked via a **skill** (e.g., `/feature-implementation`). Skills are self-contained and embed the key steps; this directory holds the full reference specs for deeper reading.

---

## Available Workflows

| Workflow | Skill | Agents | Frequency |
|---|---|---|---|
| `feature-implementation.md` | `/feature-implementation` | scrum_master_casazen, feature_developer, code_reviewer, release_manager | Per issue |
| `compliance-feature-creation.md` | `/compliance-feature` | regulatory_agent, analyzer_agent, scrum_master_casazen, product_owner, architect | Monthly / ad-hoc |
| `contract-audit.md` | `/contract-audit` | scrum_master_casazen, github_agent | Bi-weekly / pre-release |

### Shared Process

| Document | Used by |
|---|---|
| `common/review-process.md` | feature-implementation, any workflow that opens PRs |

---

## Typical Flows

### First Run (no roadmap/epics)

```
/feature-implementation
  → no issues → auto-trigger /compliance-feature
    → no roadmap → Refinement Meeting (in-memory)
       @product_owner + @architect + @scrum_master_casazen
       → product-roadmap.md + Epic issues
    → @regulatory_agent updates regulations
    → @analyzer_agent runs gap analysis
    → GitHub Issues created
  → /feature-implementation resumes
    → @feature_developer implements P0 first
    → /code-review-local (max 3 iterations)
    → @release_manager merges
```

### Subsequent Runs (roadmap exists)

```
/compliance-feature          (monthly, or when new regulation published)
  → regulatory update + gap analysis + new issues

/feature-implementation      (daily)
  → pick oldest high-priority issue
  → implement → PR → review → merge
```

### Pre-Release / Sprint Planning

```
/contract-audit              (bi-weekly or before major release)
  → FE/BE alignment check
  → creates sync issues if misalignments found

/feature-implementation      (resolve sync issues + backlog features)
```

---

## Adding a New Workflow

1. Create `.claude/workflows/<name>.md` with full spec
2. Create `.claude/skills/<name>.md` as a self-contained skill (embed key steps, link to spec)
3. Add entry to this README
4. Reference from `PLANNING.md` or `DEVELOPMENT.md` if relevant

---

## Structure

```
.claude/workflows/
  README.md                        This file
  feature-implementation.md        Issue → PR → merge
  compliance-feature-creation.md   Regulatory → backlog
  contract-audit.md                FE/BE alignment audit
  common/
    review-process.md              Shared review protocol (max 3 iterations)
```

---

**Related**: `PLANNING.md` (root) | `DEVELOPMENT.md` (root) | `.claude/skills/` | `.claude/hooks/`
