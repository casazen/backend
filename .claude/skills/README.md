# Skills

On-demand instructions invoked with `/skill-name`. Skills load only when called, keeping the base context small.

Each skill is **self-contained**: it does not redirect to another file (no thin-invoker pattern). The full execution instructions are embedded directly in the skill file.

---

## Available Skills

### Process Skills (orchestrate agents + workflows)

| Skill | Invocation | What it does |
|---|---|---|
| `compliance-feature` | `/compliance-feature` | Regulatory scan → gap analysis → GitHub issue backlog |
| `feature-implementation` | `/feature-implementation` | GitHub issue → branch → PR → review → merge |
| `contract-audit` | `/contract-audit` | FE/BE API alignment audit → GitHub issues |
| `create-cross-repo-issues` | `/create-cross-repo-issues` | Create paired FE+BE GitHub issues (delegates to `@scrum_master_casazen`) |

### Reference Skills (instant lookup, no agent needed)

| Skill | Invocation | What it does |
|---|---|---|
| `codebase-overview` | `/codebase-overview` | Architecture snapshot (replaces exploring 10-20 files) |
| `code-review-local` | `/code-review-local` | Manual PR code review (GitHub Actions disabled for cost) |
| `migration-workflow` | `/migration-workflow` | EF Core migration create → review → apply steps |
| `list-issues` | `/list-issues` | Live GitHub issue query (replaces deprecated static file) |

### Template Skills (generate structured output)

| Skill | Invocation | What it does |
|---|---|---|
| `write-user-story` | `/write-user-story` | Generate a compliance-ready GitHub issue from a regulatory gap |
| `open-github-issue` | `/open-github-issue` | Create a GitHub issue with the CasaZen compliance template |

---

## Design Principles

- **Self-contained**: each skill file contains full execution instructions
- **No thin-invoker**: skills do not just `Read <other-file>` — they embed the essential steps
- **On-demand**: skills are NOT loaded into base context; they load only when invoked
- **Token-efficient**: skills avoid duplicating content already in `CLAUDE.md` or `.claude/rules/`

Full workflow specs (for deeper reference): `.claude/workflows/`

---

## Token Savings

Skills reduce the base context loaded at session start by ~20-30%.

| Replaced by skill | Token savings |
|---|---|
| `codebase-overview` | Replaces reading 10-20 files (~5,000 tokens) |
| `migration-workflow` | Replaces reading migration docs (~1,000 tokens) |
| `list-issues` | Replaces static `open_issues.md` file (~3,000 tokens) |
| `code-review-local` | Replaces reading full review setup docs (~2,000 tokens) |
