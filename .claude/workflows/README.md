# Workflows

Canonical workflow specifications for CasaZen. These are the authoritative process docs.

Each workflow is invoked via a **command** (e.g., `/step1-refine`) or a **skill** (e.g., `/feature-implementation`). Commands embed the steps directly and reference the workflow spec; skills are self-contained. This directory holds the full reference specs for deeper reading.

---

## Available Workflows

### 3-Step Pipeline (Raw Requirement → Shipped Feature)

| Workflow | Command | Agents | Trigger |
|---|---|---|---|
| `step1-requirement-refine.md` | `/step1-refine` | requirement_clarifier, product-owner, architect, regulatory_agent, analyzer_agent, scrum_master_casazen | Label `raw-requirement` / `council-ready` |
| `step2-dispatcher.md` | `/step2-dispatch` | analyzer_agent, feature_developer (planning), scrum_master_casazen | Label `approved` |
| `step3-implementation.md` | `/step3-implement` | feature_developer, code_reviewer, scrum_master_casazen | Label `in-sprint` / PR merged / issue closed |

GitHub Actions auto-triggers: `.github/workflows/step-transitions.yml`

### Standalone Workflows

| Workflow | Command/Skill | Agents | Frequency |
|---|---|---|---|
| `feature-implementation.md` | `/feature-implementation` | scrum_master_casazen, feature_developer, code_reviewer, release_manager | Per issue |
| `compliance-feature-creation.md` | `/compliance-feature` | regulatory_agent, analyzer_agent, scrum_master_casazen, product_owner, architect | Monthly / ad-hoc |
| `contract-audit.md` | `/contract-audit` | scrum_master_casazen, github_agent | Bi-weekly / pre-release |

### Shared Process

| Document | Used by |
|---|---|
| `common/review-process.md` | step3-implementation, feature-implementation, any workflow that opens PRs |

---

## Typical Flows

### New Feature via 3-Step Pipeline

```
label raw-requirement (or /step1-refine N)
  → @requirement-clarifier: assess clarity
      → ambiguous: post ≤3 questions, set awaiting-clarification (STOP)
      → clear: set council-ready
  → Council (parallel): @product-owner + @architect + @regulatory-agent + @analyzer-agent
  → @scrum-master-casazen: create backlog item (pending-po-approval)
  [human PO adds 'approved']
  → /step2-dispatch N
      → @analyzer-agent: dependency map
      → @feature-developer: decompose into atomic tasks
      → @scrum-master-casazen: create BE + FE issues (sprint-candidate)
  [human SM adds 'in-sprint' to selected tasks]
  → /step3-implement N [N N ...]
      → pre-flight: verify dependencies
      → @feature-developer: branch + implement + PR (parallel if independent)
      → /code-review-local (max 3 iterations)
      → [human merges PR]
      → GitHub Actions (trigger-step3-post-merge): close task + label merged
      → @scrum-master-casazen: Epic closure check + update codebase_map.md
      → GitHub Actions (trigger-unblock-on-close): start next blocked in-sprint task
```

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
  README.md                          This file
  step1-requirement-refine.md        Raw issue → clarification → council → backlog item
  step2-dispatcher.md                Approved Epic → atomic cross-repo task issues
  step3-implementation.md            In-sprint task → branch → PR → review → Epic closure
  feature-implementation.md          Issue → PR → merge (standalone, no pipeline)
  compliance-feature-creation.md     Regulatory → backlog
  contract-audit.md                  FE/BE alignment audit
  common/
    review-process.md                Shared review protocol (max 3 iterations)
```

### Label Vocabulary (3-Step Pipeline)

| Label | Set by | Meaning |
|---|---|---|
| `raw-requirement` | human | Triggers Step 1 clarifier |
| `awaiting-clarification` | @requirement-clarifier | Pipeline paused, waiting for PO reply |
| `council-ready` | @requirement-clarifier | Triggers council review |
| `pending-po-approval` | @scrum-master-casazen | Backlog item needs PO sign-off |
| `approved` | human PO | Triggers Step 2 dispatcher |
| `sprint-candidate` | @scrum-master-casazen | Task created, available for sprint |
| `task` | @scrum-master-casazen | Atomic work item (BE or FE) |
| `be` / `fe` | @scrum-master-casazen | Repo scope |
| `effort:XS/S/M` | @scrum-master-casazen | Size estimate |
| `in-sprint` | human SM | Triggers Step 3 implementation |
| `merged` | GitHub Actions (Phase E) | Task PR has been merged; triggers auto-unblock of dependent tasks |

---

**Related**: `PLANNING.md` (root) | `DEVELOPMENT.md` (root) | `.claude/skills/` | `.claude/hooks/`
