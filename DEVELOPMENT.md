# CasaZen - Development Process

This document is the **authoritative guide** for how development work is started, executed, and completed in the CasaZen backend. It applies to all contributors: humans and AI agents.

> **Related**: [PLANNING.md](./PLANNING.md) — how features are planned before implementation begins.

---

## Overview

Development in CasaZen follows a structured, agent-assisted workflow that ensures quality, compliance, and cross-repo coherence. All development work originates from a GitHub issue and ends with a merged pull request.

```
GitHub Issue
    └── Feature branch created
        └── Code implemented (with tests)
            └── PR opened
                └── Code review (local or automated)
                    └── Critical/High issues fixed
                        └── PR approved
                            └── Merged to main (release_manager only)
                                └── Issue closed
```

---

## How to Start Development

### Step 1: Find an issue to implement

```bash
# List open backend issues (excluding epics)
gh issue list --state open --label "feature,bug,enhancement,compliance" \
  --json number,title,labels,milestone | jq .

# If the backlog is empty, run the planning workflow first:
# → See PLANNING.md
```

Pick the issue with the highest priority:
- `priority:critical` → compliance deadline, blocking other work
- `priority:high` → high-value feature or serious bug
- `priority:medium` → standard feature
- `priority:low` → nice-to-have

### Step 2: Create a feature branch

```bash
git checkout main && git pull
git checkout -b feature/<descriptive-name>   # e.g. feature/cin-code-validation
# For bug fixes:
git checkout -b fix/<descriptive-name>
# For hotfixes:
git checkout -b hotfix/<descriptive-name>
```

Branch naming conventions:
| Type | Pattern | Example |
|---|---|---|
| Feature | `feature/<name>` | `feature/tourist-tax-calculation` |
| Bug fix | `fix/<name>` | `fix/booking-overlap-check` |
| Hotfix | `hotfix/<name>` | `hotfix/alloggiati-web-timeout` |
| Refactor | `refactor/<name>` | `refactor/ota-adapter-interface` |
| Docs | `docs/<name>` | `docs/development-process` |

### Step 3: Implement

Follow the layered architecture — see the [Architecture Overview](#architecture-quick-reference) below.

Key rules (full details in `.claude/rules/`):
- **Async**: always use `async/await`, never `.Result` or `.Wait()`
- **Repository pattern**: all DB access via `IRepository<T>`
- **Tests**: write unit + integration tests alongside implementation
- **Migrations**: run `dotnet ef migrations add <Name>` for every schema change
- **Security**: validate all inputs, use parameterized queries, check auth
- **Compliance**: tag code touching Italian regulations with XML comments referencing the specific normativa

```bash
# Run tests frequently during implementation
dotnet test

# Format code before committing
dotnet format
```

### Step 4: Commit with Conventional Commits

```bash
git add .
git commit -m "feat: add CIN code validation on property creation"
# Other prefixes:
# fix:      bug fix
# refactor: no behavior change
# test:     add/update tests
# docs:     documentation only
# chore:    tooling, config
```

### Step 5: Open a Pull Request (MANDATORY)

```bash
git push origin feature/<branch-name>

gh pr create --base main --head feature/<branch-name> \
  --title "feat: descriptive title matching commit" \
  --body "$(cat <<'EOF'
## Summary
[What was changed and why]

## Test Plan
- [x] Build succeeds (`dotnet build`)
- [x] All tests pass (`dotnet test`)
- [x] [Other verification steps — e.g., Swagger endpoint tested manually]

## Compliance Notes
[If applicable: which Italian regulation this implements and how]

Closes #<issue-number>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

**NEVER push to `main` directly. NEVER merge locally. Always open a PR.**

### Step 6: Run code review

```bash
# Invoke local code review skill
/code-review-local
```

Fix all **Critical** (🔴) and **High** (🟡) severity findings before requesting merge.

Review severity guide:
| Level | Action |
|---|---|
| 🔴 Critical | MUST fix before merge (security, compliance, deadlock) |
| 🟡 High | SHOULD fix before merge (missing tests, SOLID violations) |
| 🟢 Medium | Consider fixing (duplication, complexity) |
| ⚪ Low | Optional (style, naming) |

### Step 7: Request merge

After PR is approved and CI passes, request the `@release_manager` to merge:

```bash
# Only release_manager executes this:
gh pr merge <number> --squash --delete-branch
```

---

## AI Agent Development Workflow

When using agents to implement features:

### Manual invocation

```bash
# Invoke the full feature implementation workflow
/feature-implementation
```

This orchestrates:
1. `@scrum_master_casazen` — reads open issues, groups related work, creates implementation plan
2. `@feature_developer` — implements on feature branch, writes tests, opens PR
3. `/code-review-local` — reviews the PR (max 3 iterations, anti-loop protection)
4. `@release_manager` — merges when approved

### Automated invocation (GitHub Actions)

The `daily-development.yml` workflow runs every morning at **08:00 UTC**:
- If open issues exist → picks oldest by priority and implements
- If backlog is empty → triggers planning workflow (see [PLANNING.md](./PLANNING.md))

Manual trigger:
```bash
gh workflow run daily-development.yml

# Force planning even if issues exist:
gh workflow run daily-development.yml -f force_new_issues=true
```

### Agent priority order

Always use the most specific agent for the task:

```
1. Project agents (.claude/agents/)    ← Domain-specific, CasaZen context
2. User agents (~/.claude/agents/)    ← Generic, reusable
3. Built-in agents                    ← Fallback only
```

Project agents:
| Agent | When to use |
|---|---|
| `scrum_master_casazen` | Cross-repo coordination (BE ↔ FE), full-stack feature planning |
| `feature_developer` | Code implementation (CasaZen override with mandatory review step) |
| `regulatory_agent` | Italian regulation monitoring |
| `analyzer_agent` | Compliance gap analysis |
| `github_agent` | Creating compliance issues on GitHub |

---

## Cross-Repo Development

For features that span both backend and frontend:

```
@scrum_master_casazen coordinates:
  1. Creates backend issue on casazen/backend
  2. Creates frontend issue on casazen/frontend
  3. Cross-links both issues
  4. Coordinates BE-first implementation
  5. Notifies frontend team when BE API is ready
  6. Synchronizes production deployment
```

Backend is always implemented first (API before UI).

Cross-repo tracking: `.claude/coordination/<feature-id>-status.md`

---

## Architecture Quick Reference

```
Casazen.Web (Presentation)
  Controllers/       API endpoints — thin layer, delegate to services
  Middleware/        Auth, logging, error handling
  Program.cs         DI registration

Casazen.Core (Domain)
  Entities/          Domain models (Property, Booking, Guest, Payment, OtaIntegration)
  Repositories/      IRepository<T> interfaces — DO NOT implement here
  Services/          Business logic interfaces

Casazen.Infrastructure (Data + External)
  Data/              AppDbContext, EF Core configuration
  Migrations/        EF Core migration files
  Repositories/      IRepository<T> implementations
  Services/          Business logic implementations
  External/          Auth0, Stripe, SendGrid
  OTA/               Adapter per OTA platform (Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda)

Casazen.Tests
  Unit/              Service unit tests (Moq)
  Integration/       API integration tests
```

Key files:
| What | Where |
|---|---|
| DI registration | `Casazen.Web/Program.cs` |
| DB Context | `Casazen.Infrastructure/Data/AppDbContext.cs` |
| Migrations | `Casazen.Infrastructure/Data/Migrations/` |
| OTA adapters | `Casazen.Infrastructure/OTA/` |
| Configuration | `Casazen.Web/appsettings.json` |

---

## Essential Commands

```bash
# Local development
dotnet run --project Casazen.Web
dotnet test
dotnet format

# Database migrations
dotnet ef migrations add <MigrationName> --project Casazen.Infrastructure
dotnet ef database update --project Casazen.Infrastructure

# GitHub Actions (manual triggers)
gh workflow run daily-development.yml
gh workflow run daily-testing-and-review.yml
gh workflow run regulatory-agents.yml

# GitHub issue / PR management
gh issue list --state open
gh pr list --state open
gh pr view <number>
```

---

## GitHub Flow Rules (Non-Negotiable)

Full rules: [`.claude/rules/github-flow-mandatory.md`](.claude/rules/github-flow-mandatory.md)

Summary:
- All work on feature branches — never commit to `main` directly
- Every change requires a Pull Request
- PR must have: Conventional Commit title, Summary, Test Plan, `Closes #X`
- Only `@release_manager` merges PRs to `main`
- CI checks must pass before merge
- Code review must be completed (🔴 Critical findings resolved)

---

## Testing Standards

Full standards: [`.claude/rules/code-style.md`](.claude/rules/code-style.md)

```bash
# All tests
dotnet test

# Specific test class
dotnet test --filter "PropertyServiceTests"

# With coverage
dotnet test /p:CollectCoverage=true
```

Patterns:
- Unit tests: AAA (Arrange-Act-Assert), Moq for dependencies
- Integration tests: `WebApplicationFactory<Program>` for API tests
- Test every business logic path, error case, and edge case

---

## Daily Development Cycle

| Time (UTC) | Activity |
|---|---|
| 08:00 | `daily-development.yml` — implement oldest open issue (or plan if backlog empty) |
| 17:00 | `daily-testing-and-review.yml` — run tests, review PRs, merge if approved |
| 1st of month, 08:00 | `regulatory-agents.yml` — scan Italian regulations, create compliance issues |

---

**Last Updated**: 2026-05-02
**Maintained By**: CasaZen Development Team
**Related Docs**: [PLANNING.md](./PLANNING.md) | [CLAUDE.md](./CLAUDE.md) | [REVIEW.md](./REVIEW.md)
