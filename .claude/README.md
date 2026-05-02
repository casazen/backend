# CasaZen - Automated Development System

> **Start here**: Before reading this file, see the two core process guides at the repository root:
> - **[PLANNING.md](../PLANNING.md)** — how to create the backlog (regulatory analysis, roadmap, epics)
> - **[DEVELOPMENT.md](../DEVELOPMENT.md)** — how to implement features (branch → code → PR → review → merge)

This document explains the automated development system powered by Claude agents and GitHub Actions.

## Overview

CasaZen uses an **automated agent-based development workflow** that runs daily via GitHub Actions. The system handles:
- Regulatory compliance monitoring (monthly)
- Feature planning and implementation (daily)
- Automated testing and code review (daily)
- Pull request management (daily)

---

## GitHub Actions Workflows

### 1. Regulatory Intelligence (Monthly)
**File**: `.github/workflows/regulatory-agents.yml`
**Schedule**: 1st day of each month at 08:00 UTC
**Manual**: `gh workflow run regulatory-agents.yml`

**What it does**:
1. **Regulatory Agent**: Scans Italian government sources for new short-term rental regulations
2. **Analyzer Agent**: Analyzes gaps between regulations and current codebase
3. **GitHub Agent**: Creates GitHub issues for compliance gaps

**Agents used**:
- `regulatory_agent` - Collects regulatory updates
- `analyzer_agent` - Gap analysis
- `github_agent` - Issue creation

**Output**: GitHub issues labeled `regulatory` and `compliance`

---

### 2. Daily Development (Morning - 08:00 UTC)
**File**: `.github/workflows/daily-development.yml`
**Schedule**: Every day at 08:00 UTC (9:00 CET, 10:00 CEST)
**Manual**: `gh workflow run daily-development.yml`

**What it does**:

#### Mode A: Implementation (if backlog has issues)
1. Picks the oldest open issue (excluding regulatory issues)
2. **Issue Planner Agent**: Creates implementation plan
3. **Feature Developer Agent**: Implements the feature
4. Commits changes to a feature branch
5. Adds comment to issue with progress

#### Mode B: Planning (if backlog is empty)
1. **Product Owner Agent**: Analyzes project needs and identifies 2-3 high-priority features
2. **Architect Agent**: Designs technical implementation approach
3. **Scrum Master Agent**: Creates GitHub issues for top 2 priorities

**Agents used**:
- Global: `issue_planner`, `feature_developer`, `product_owner`, `architect`
- Project: `scrum_master_casazen`

**Output**: Implemented code + GitHub issue comments OR new GitHub issues

---

### 3. Daily Testing & Review (Evening - 17:00 UTC)
**File**: `.github/workflows/daily-testing-and-review.yml`
**Schedule**: Every day at 17:00 UTC (18:00 CET, 19:00 CEST)
**Manual**: `gh workflow run daily-testing-and-review.yml`

**What it does**:
1. **Test Engineer Agent**: Runs full test suite with coverage analysis
2. **Code Reviewer Agent**: Reviews today's commits for quality and security
3. **Release Manager Agent**: Reviews open PRs and decides whether to merge
4. Sends email report with daily summary

**Agents used**:
- Global: `test_engineer`, `code_reviewer`, `release_manager`

**Output**:
- Test results uploaded as artifacts
- GitHub PR comments/approvals
- Email report to `luca.lamal@hotmail.it`

---

### 4. Standard CI/CD
**File**: `.github/workflows/ci-cd.yml`
**Trigger**: On push to any branch

**What it does**:
- Builds the .NET solution
- Runs tests
- Standard continuous integration checks

---

## Agent System

### Global Agents (from `~/.claude/agents/`)
Reusable agents for any project:
- `product_owner` - Requirements gathering
- `architect` - Architecture design
- `issue_planner` - Issue breakdown and planning
- `feature_developer` - Feature implementation
- `test_engineer` - Testing and coverage analysis
- `code_reviewer` - Code quality and security review
- `release_manager` - PR management and deployment
- `doc_writer` - Documentation

### Project-Specific Agents (from `.claude/agents/`)
Domain-specific agents for CasaZen:

#### `regulatory_agent.md`
- Searches Italian government sources for short-term rental regulations
- Updates regulatory context in `.claude/context/regulations/`
- Tracks: CIN codes, Alloggiati Web, tourist tax, GDPR, fiscal regulations

#### `analyzer_agent.md`
- Compares regulatory requirements vs implemented features
- Identifies gaps: MISSING, PARTIAL, OUTDATED, COMPLIANT
- Prioritizes: CRITICAL, HIGH, MEDIUM, LOW
- Outputs analysis for GitHub agent

#### `github_agent.md`
- Creates GitHub issues from gap analysis
- Checks for duplicates before creating
- Adds labels: `regulatory`, `compliance`, `priority:*`
- Updates `.claude/context/open_issues.md`

#### `scrum_master_casazen.md`
- Coordinates between backend and frontend repositories (if needed)
- Creates well-structured GitHub issues from requirements
- Follows project issue templates

---

## Skills

Custom skills available in `.claude/skills/`:

- `classify_topic.md` - Classifies regulations by topic
- `create_cross_repo_issues.md` - Creates issues across multiple repos
- `diff_context.md` - Compares context files
- `open_github_issue.md` - Structured GitHub issue creation
- `scrape_source.md` - Web scraping for regulatory sources
- `write_user_story.md` - Generates user stories from requirements

---

## Directory Structure

```
.claude/
├── README.md                    # This file
├── agents/                      # Project-specific agents
│   ├── regulatory_agent.md
│   ├── analyzer_agent.md
│   ├── github_agent.md
│   └── scrum_master_casazen.md
├── context/                     # Regulatory and domain context
│   ├── domain.md
│   ├── codebase_map.md
│   ├── open_issues.md
│   ├── _index.md
│   ├── _last_updated.json
│   └── regulations/             # Italian short-term rental regulations
│       ├── cin.md
│       ├── alloggiati.md
│       ├── imposta_soggiorno.md
│       ├── fiscale.md
│       ├── gdpr.md
│       ├── ota_normativa.md
│       ├── sicurezza.md
│       └── regionale.md
├── skills/                      # Custom skills
└── settings.local.json          # Local settings
```

**What's NOT here anymore** (intentionally removed to reduce noise):
- ❌ `sprint/` - No local planning files (agents output directly to GitHub)
- ❌ `reports/` - No local reports (results in GitHub artifacts + email)
- ❌ `config/` - Moved to root CLAUDE.md and README.md
- ❌ `coordination/` - Not needed
- ❌ `learning/` - Not needed
- ❌ `workflows/` - Not needed
- ❌ `AGENT_SYSTEM.md` - Consolidated here
- ❌ `ORCHESTRATORS.md` - Consolidated here

---

## How It Works

### Daily Cycle

**Morning (08:00 UTC)**:
1. Check if backlog has open issues
2. If YES → Implement oldest issue
3. If NO → Analyze project needs and create new issues

**Evening (17:00 UTC)**:
1. Run full test suite
2. Review today's code changes
3. Review open PRs and merge if quality is good
4. Send email report

**Monthly (1st day, 08:00 UTC)**:
1. Scan for regulatory updates
2. Update context files
3. Create compliance issues if gaps found

### Data Flow

```
Regulatory Agent → .claude/context/regulations/ → Analyzer Agent
                                                         ↓
                                                  GitHub Agent
                                                         ↓
                                                  GitHub Issues
                                                         ↓
                                          Daily Dev Workflow picks issue
                                                         ↓
                                          Feature Developer implements
                                                         ↓
                                          Commits to feature branch
                                                         ↓
                                          Testing & Review workflow
                                                         ↓
                                          Merge to main (if quality OK)
```

---

## Manual Execution

### Run regulatory scan:
```bash
gh workflow run regulatory-agents.yml
```

### Run daily development:
```bash
gh workflow run daily-development.yml
```

### Force create new issues even with backlog:
```bash
gh workflow run daily-development.yml -f force_new_issues=true
```

### Run testing and review:
```bash
gh workflow run daily-testing-and-review.yml
```

---

## Configuration

### GitHub Secrets Required
- `ANTHROPIC_API_KEY` - For Claude agents
- `GITHUB_TOKEN` - Auto-provided by GitHub Actions
- `SENDGRID_API_KEY` - For email reports

### Email Reports
Daily reports sent to: `luca.lamal@hotmail.it`

---

## Notes

- All agents work with **English** for code/docs (Italian only for end-user UI)
- Agents **never include** "Co-Authored-By: Claude" in commits (project policy)
- Reports and plans are **ephemeral** (not committed, available as GitHub artifacts for 30 days)
- Regulatory context is **persistent** (committed to `.claude/context/`)
- Maximum 10 regulatory issues created per run (to avoid flooding)
- PRs auto-merged only if: tests pass + code review score B or better

---

**Last Updated**: 2026-05-02
**System Version**: 2.1 (Process-Centric)
**Core Process Guides**: [PLANNING.md](../PLANNING.md) | [DEVELOPMENT.md](../DEVELOPMENT.md)
