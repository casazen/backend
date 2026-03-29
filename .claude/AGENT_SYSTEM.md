# CasaZen Agent System - Overview

## Architecture

CasaZen uses a **two-tier agent system**:

### 1. Global Agents (Reusable)
Located in: `~/.claude/agents/`

Generic agents used across ALL projects:
- `product_owner` - Requirements gathering
- `architect` - Architecture design
- `issue_planner` - Issue planning (single-repo)
- `feature_developer` - Implementation
- `test_engineer` - Testing
- `code_reviewer` - Code review
- `release_manager` - Deployment
- `doc_writer` - Documentation

### 2. Project-Specific Agents
Located in: `.claude/agents/`

CasaZen-specific agents with domain knowledge:
- `scrum_master` - **Cross-repo coordination (backend ↔️ frontend)**
- `regulatory_agent` - Italian compliance monitoring
- `analyzer_agent` - Compliance gap analysis
- `github_agent` - Automated issue creation for compliance

---

## When to Use Which Agent

### Feature Development (Backend + Frontend)
```
User: "We need to integrate with Booking.com"

Flow:
1. product_owner (global) → Gather requirements
2. architect (global) → Design backend API + frontend UI
3. scrum_master (project) → Create issues on both repos, coordinate
4. feature_developer (global) → Implement backend, then frontend
5. test_engineer (global) → Write tests
6. code_reviewer (global) → Review PRs
7. release_manager (global) → Deploy to production
8. scrum_master (project) → Close issues, completion report
```

### Single-Repo Work (Backend OR Frontend only)
```
User: "Refactor the payment service"

Flow:
1. architect (global) → Design refactoring
2. issue_planner (global) → Create issue
3. feature_developer (global) → Implement
4. test_engineer (global) → Tests
5. code_reviewer (global) → Review
6. release_manager (global) → Deploy

(No need for scrum_master - single repo)
```

### Regulatory Compliance (Automated, Monthly)
```
Scheduled run (1st of every month):

Flow:
1. regulatory_agent (project) → Scrape Italian government sites
2. analyzer_agent (project) → Analyze gaps in codebase
3. github_agent (project) → Create GitHub issues for gaps

(Runs automatically, no human intervention needed)
```

### Critical Issues
```
User: "Production is down! Payments failing!"

Flow:
1. Assess: Backend only? Frontend only? Both?

If single-repo:
  - issue_planner (global) with P0 priority
  - feature_developer (global) for hotfix
  - release_manager (global) for emergency deploy

If cross-repo:
  - scrum_master (project) coordinates both repos
  - feature_developer (global) implements hotfixes
  - scrum_master (project) coordinates deployment
```

---

## Key Principle: Don't Duplicate

❌ **DON'T** create project-specific agents for generic tasks:
- Don't create a "casazen_developer" agent (use `feature_developer`)
- Don't create a "casazen_architect" agent (use `architect`)
- Don't create a "casazen_tester" agent (use `test_engineer`)

✅ **DO** create project-specific agents for unique domain logic:
- ✅ `scrum_master` - Unique to CasaZen (coordinates 2 repos)
- ✅ `regulatory_agent` - Unique to CasaZen (Italian regulations)
- ✅ `analyzer_agent` - Unique to CasaZen (compliance gap analysis)

---

## Scrum Master: The Cross-Repo Orchestrator

**Purpose**: Coordinates features that span both backend and frontend repositories.

**Responsibilities**:
1. **Issue Creation**: Creates linked issues on both repos
2. **Coordination**: Tracks progress across both repos
3. **Synchronization**: Ensures backend API is ready before frontend starts
4. **Deployment**: Coordinates production deployment (backend first, then frontend)
5. **Completion**: Closes both issues when feature is live

**When to invoke**:
- Feature requires both backend AND frontend work
- Need to track progress across two repositories
- Need to synchronize deployments
- Critical issue affects both repos

**When NOT to invoke**:
- Single-repo work (use `issue_planner` instead)
- Just gathering requirements (use `product_owner`)
- Just designing architecture (use `architect`)

---

## Skills (Project-Specific)

Located in: `.claude/skills/`

Reusable operations specific to CasaZen:
- `classify_topic` - Classify by domain topic (OTA, regulatory, payments)
- `diff_context` - Compare context between versions
- `open_github_issue` - Create GitHub issues (enhanced for compliance)
- `scrape_source` - Scrape regulatory sources
- `write_user_story` - Generate user stories for Italian vacation rentals
- `create_cross_repo_issues` - Thin wrapper around scrum_master

---

## Workflows

Located in: `.claude/workflows/`

Complete process documentation:
- `feature_implementation.md` - Full workflow from PO request to production (15 steps)
- `critical_issue_response.md` - Emergency response workflow (9 steps)
- `regulatory_compliance.md` - (TODO) Automated compliance monitoring

---

## Directory Structure

```
.claude/
├── agents/                    # Project-specific agents
│   ├── scrum_master.md       # Cross-repo coordination ⭐
│   ├── regulatory_agent.md   # Compliance monitoring
│   ├── analyzer_agent.md     # Gap analysis
│   └── github_agent.md       # Issue creation
│
├── skills/                    # Project-specific skills
│   ├── classify_topic.md
│   ├── create_cross_repo_issues.md
│   ├── open_github_issue.md
│   └── write_user_story.md
│
├── workflows/                 # Process documentation
│   ├── README.md
│   ├── feature_implementation.md ⭐
│   └── critical_issue_response.md
│
├── context/                   # Domain knowledge
│   ├── domain.md
│   ├── codebase_map.md
│   └── regulations/          # Italian regulations
│       ├── cin.md
│       ├── alloggiati.md
│       └── ...
│
├── coordination/              # Active work tracking
│   └── [FEATURE-ID]-status.md
│
├── incidents/                 # Critical incidents
│   └── INC-YYYY-MM-DD-HH-MM.md
│
├── post-mortems/              # Post-incident reviews
│   └── INC-YYYY-MM-DD-HH-MM.md
│
└── config/
    ├── project.json          # Technical configuration
    └── README.md
```

---

## Usage Examples

### Example 1: OTA Integration (Expedia)

**Request**: "Integrate with Expedia"

**Execution**:
```bash
# Step 1: Requirements
Task: product_owner
Prompt: "Gather requirements for Expedia integration"
Output: Requirements document

# Step 2: Architecture
Task: architect
Prompt: "Design Expedia integration (backend API + frontend UI)"
Input: Requirements from step 1
Output: Architecture plan (API endpoints, adapter, UI components)

# Step 3: Coordination
Task: scrum_master
Prompt: "Create cross-repo issues for Expedia integration"
Input: Architecture plan
Output:
  - casazen/backend#456 (Expedia API adapter)
  - casazen/frontend#789 (Expedia property management UI)
  - .claude/coordination/expedia-integration-status.md

# Step 4-7: Implementation (backend)
Task: feature_developer
Repository: backend
Issue: #456
Output: Expedia adapter implemented

Task: test_engineer
Output: Tests written

Task: code_reviewer
Output: PR approved

Task: release_manager
Output: Deployed to staging

# Step 8: Notification
Task: scrum_master
Prompt: "Notify frontend team that Expedia API is ready"

# Step 9-12: Implementation (frontend)
Task: feature_developer
Repository: frontend
Issue: #789
Output: Expedia UI implemented

Task: test_engineer
Output: Tests written

Task: code_reviewer
Output: PR approved

Task: release_manager
Output: Deployed to staging

# Step 13: Integration testing
Task: test_engineer
Output: Integration verified

# Step 14: Production deployment
Task: scrum_master
Prompt: "Deploy Expedia integration to production (both repos)"

# Step 15: Closure
Task: scrum_master
Prompt: "Close Expedia integration issues"
Output: Both issues closed, completion report
```

**Result**: Expedia integration live in ~10-12 days

---

### Example 2: Critical Bug (Payment Failure)

**Incident**: "Stripe payments returning 500 errors"

**Execution**:
```bash
# Step 1: Assessment
Severity: P0 (Critical - payment processing down)
Affected: Backend only (API error)

# Step 2: Incident declaration
Task: issue_planner (with P0 priority)
Prompt: "Create P0 incident for Stripe payment failures"

# Step 3: Containment
Action: Rollback to previous version
Result: Payments working (temporary)

# Step 4: Root cause
Task: feature_developer
Prompt: "Investigate Stripe payment failures"
Result: Stripe API version mismatch

# Step 5: Hotfix
Task: feature_developer
Prompt: "Create hotfix for Stripe API version"
Output: Hotfix PR

# Step 6: Review
Task: code_reviewer (fast-track)
Output: Approved

# Step 7: Deploy
Task: release_manager
Output: Deployed to production

# Step 8: Verify
Result: Payments working correctly

# Step 9: Post-mortem
Task: doc_writer
Output: Post-mortem with action items
```

**Resolution time**: 1.5 hours (within P0 SLA)

---

## Best Practices

### 1. Start with the Right Agent
- Requirements unclear? → `product_owner`
- Architecture needed? → `architect`
- Cross-repo work? → `scrum_master`
- Single-repo work? → `issue_planner`

### 2. Chain Agents Logically
```
product_owner → architect → scrum_master → feature_developer
```

Don't skip steps (e.g., don't go straight from requirements to implementation without architecture).

### 3. Use Scrum Master for Cross-Repo Only
If the work is backend-only or frontend-only, use `issue_planner` instead. Scrum master is specifically for coordinating both repos.

### 4. Document Everything
All agents produce artifacts:
- product_owner → Requirements doc
- architect → Architecture plan
- scrum_master → Coordination status
- feature_developer → Code + PRs
- etc.

Keep these documents for reference and audit trail.

### 5. Leverage Domain Knowledge
Project-specific agents (regulatory, analyzer, github) have deep knowledge of:
- Italian vacation rental regulations
- CasaZen codebase structure
- Compliance requirements
- OTA integrations

Use them for domain-specific tasks.

---

## Maintenance

### When to Create New Project-Specific Agents
Only when you have unique domain logic that:
1. Is specific to CasaZen (not reusable across projects)
2. Requires deep domain knowledge (regulations, business rules)
3. Doesn't fit into generic agent categories

Examples:
- ✅ `scrum_master` - Unique cross-repo coordination for CasaZen
- ✅ `regulatory_agent` - Italian regulations (domain-specific)
- ❌ `casazen_coder` - Just use `feature_developer`

### When to Update This System
- New repositories added (e.g., mobile app) → Update scrum_master
- New compliance requirements → Update regulatory_agent
- New OTA platforms → Update context, not agents
- Team structure changes → Update coordination workflows

---

## Questions?

- **How to start a feature?** → See `workflows/feature_implementation.md`
- **How to handle critical issues?** → See `workflows/critical_issue_response.md`
- **Which agent for X?** → See "When to Use Which Agent" section above
- **Scrum master vs issue_planner?** → Scrum master for cross-repo, issue_planner for single-repo

---

**Last Updated**: 2026-03-29
**Maintained By**: CasaZen Development Team
