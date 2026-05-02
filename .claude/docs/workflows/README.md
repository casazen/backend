# CasaZen Workflow Automation

> **Optimized architecture**: Reusable common processes + Specialized workflows

---

## Structure

```
.claude/docs/workflows/
├── README.md                      # This file — workflow index
├── feature-implementation.md      # Issue → PR with review cycle
├── compliance-feature-creation.md # Regulatory → Feature backlog
└── contract-audit.md              # FE/BE sync gap analysis

.claude/hooks/common/
└── review-process.md              # Reusable review process (max 3 iterations)
```

**Entry points** (root-level guides that reference these workflows):
- `PLANNING.md` — how to create the backlog
- `DEVELOPMENT.md` — how to implement features

---

## Available Workflows

### 1. Feature Implementation (Issue → PR)

**File**: `feature-implementation.md`
**Primary agent**: `@scrum_master_casazen`
**Collaborators**: `@feature_developer`, `@code_reviewer`, `@release_manager`
**When to use**: When there are issues to implement (sprint execution)

**What it does**:
0. **Prerequisites check**: Verify open issues exist
   - If none → auto-trigger `/compliance-feature` to generate backlog
1. Analyze open issues (FE + BE, excluding epics)
2. Group related features and plan implementation order
3. Coordinate `@feature_developer` to implement
4. Run review cycle (uses `common/review-process.md`)
5. Manage merge via `@release_manager`

**Output**:
- Implementation plan per feature group
- PR opened and reviewed (FE + BE)
- Issues closed after merge
- Final report (APPROVED or ESCALATION)

**Auto-trigger**: If backlog is empty, automatically runs `/compliance-feature`

**Invocation**:
```bash
# Via skill (recommended)
/feature-implementation

# Or load workflow file directly
Read .claude/docs/workflows/feature-implementation.md
```

---

### 2. Compliance-Driven Feature Creation

**File**: `compliance-feature-creation.md`
**Agents**: `@regulatory_agent`, `@analyzer_agent`, `@scrum_master_casazen`
**Supporting**: `@product_owner`, `@architect` (if roadmap missing)
**When to use**: Monthly (regulatory monitoring) or ad-hoc (new regulation published)

**What it does**:
0. **Planning & Epics check**: Verify roadmap and active epics exist
   - If missing → **Refinement Meeting** (in-memory):
     - `@product_owner`: Vision, personas, strategic goals, epic candidates
     - `@architect`: Technical feasibility, architecture, effort, risks
     - `@scrum_master_casazen`: Consolidation, final roadmap, epic creation
   - Output: Consolidated product roadmap + Epic issues on GitHub
1. Update Italian regulations (CIN, Alloggiati Web, Tourist Tax, GDPR)
2. Analyze gap between regulations and codebase
3. Research competitors (Lodgify, Guesty, Hostaway)
4. Verify existing features in codebase
5. Create prioritized backlog (P0 compliance → P3 nice-to-have)
6. Open GitHub Issues via `@scrum_master_casazen` (linked to epics)

**Output**:
- `.claude/context/planning/product-roadmap.md` (created if missing)
- Epic issues on GitHub (created if missing)
- `.claude/context/regulations/` updated
- `.claude/context/gap-analysis-YYYY-MM-DD.md` created
- Feature backlog (with priority, effort, scope)
- N GitHub Issues created (linked to epics)

**Note**: Refinement meeting happens in-memory (no intermediate files); only the final roadmap is written to disk.

**Invocation**:
```bash
# Via skill (recommended)
/compliance-feature

# Or load workflow file directly
Read .claude/docs/workflows/compliance-feature-creation.md
```

---

### 3. Contract Audit (FE/BE Sync)

**File**: `contract-audit.md`
**Agent**: `@scrum_master_casazen`
**When to use**: Every 2 weeks, or before a major release

**What it does**:
1. Reads backend (API, DTOs, controllers)
2. Reads frontend (types, API client, query hooks)
3. Identifies misalignments (types, endpoints, documentation)
4. Opens a GitHub Issue per gap (categorized by severity)
5. Creates a summary issue

**Output**:
- N issues on `casazen/frontend` (types, API client, hooks)
- M issues on `casazen/backend` (docs, contract)
- 1 summary issue on backend

**Invocation**:
```bash
# Via skill (recommended)
/contract-audit

# Or load workflow file directly
Read .claude/docs/workflows/contract-audit.md
```

---

## Common Process: Code Review

**File**: `common/review-process.md`
**When it is used**: Automatically within `feature-implementation.md` and other workflows

**Characteristics**:
- Max 3 review iterations per PR
- Severity-based findings (🔴 🟡 🟢 ⚪)
- Anti-loop: automatic escalation after 3 iterations with unresolved blockers
- Incremental review: only delta changes re-examined between iterations

**Do not invoke directly** — it is a reusable process referenced by other workflows.

---

## Typical Flows

### Scenario 1: First Run (No Planning or Epics)

```
1. /feature-implementation
   → No issues exist → auto-trigger /compliance-feature

2. /compliance-feature (triggered)
   → No roadmap → Refinement Meeting (in-memory):
       @product_owner: Vision & epics
       @architect: Feasibility & risks
       @scrum_master_casazen: Consolidated roadmap + Epic creation
   → Creates product-roadmap.md
   → Creates 5 epic issues on GitHub

3. /compliance-feature (continues)
   → Updates regulations + gap analysis + competitive research
   → Creates feature issues (linked to epics)

4. /feature-implementation (resumes)
   → Implements P0 (critical) features first
   → Review cycle + merge

5. Deploy to production
```

### Scenario 2: Subsequent Runs (Planning Exists)

```
1. /compliance-feature
   → Roadmap exists → SKIP refinement meeting
   → Updates regulations + gap analysis + competitive research
   → Creates new feature issues (under existing epics)

2. /feature-implementation
   → Issues exist → proceed directly
   → Implements prioritized features
   → Review cycle + merge

3. Deploy to production
```

### Scenario 3: Sprint Planning with Contract Audit

```
1. /contract-audit
   → Identifies FE/BE misalignments
   → Creates sync issues

2. /feature-implementation
   → Implements all sync issues
   → + features from existing backlog

3. Review + merge
```

### Scenario 4: Pre-Release Audit

```
1. /contract-audit
   → Verifies FE and BE are aligned
   → 0 issues = OK for release
   → N issues = Fix before release

2. If issues found → /feature-implementation
```

---

## Metrics & Monitoring

### KPIs to Track

**Contract Audit**:
- FE/BE misalignments by category (types, API, docs)
- Average issue resolution time

**Feature Implementation**:
- Issues closed per sprint
- Average review iterations (target: <2)
- Escalation rate (target: <5%)

**Compliance**:
- Compliance score (% gaps resolved vs identified)
- Time-to-compliance (days from regulation to deploy)
- Competitive gap (missing features vs competitors)

---

## Adding a New Workflow

1. Create `.claude/docs/workflows/<workflow-name>.md`
2. If it uses code review, reference `common/review-process.md`:
   ```markdown
   ## Review
   Follow standard process: `@.claude/hooks/common/review-process.md`
   ```
3. Add a skill in `.claude/skills/<workflow-name>.md`
4. Add a section to this README
5. Reference from `PLANNING.md` or `DEVELOPMENT.md` as appropriate

---

## References

- **Entry points**: `PLANNING.md` (root), `DEVELOPMENT.md` (root)
- **GitHub Flow**: `.claude/rules/github-flow-mandatory.md`
- **Code Style**: `.claude/rules/code-style.md`
- **Security**: `.claude/rules/security.md`
- **Review Guidelines**: `REVIEW.md` (root)
- **Project Overview**: `CLAUDE.md` (root)

---

**Last Updated**: 2026-05-02
**Maintained By**: CasaZen Development Team
