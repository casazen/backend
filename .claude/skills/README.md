# Skills

On-demand instructions invoked via OpenCode commands or the `skill` tool.
Each skill is a **directory** containing a `SKILL.md` file (OpenCode format).

```
.claude/skills/
  <name>/
    SKILL.md   ← required filename, required frontmatter (name + description)
```

Skills are **self-contained**: full execution instructions embedded directly, no thin-invoker pattern.

---

## Available Skills

### Process Skills (orchestrate agents + multi-step workflows)

| Directory | Command | What it does |
|---|---|---|
| `compliance-feature/` | `/compliance-feature` | Regulatory scan → gap analysis → GitHub issue backlog |
| `feature-implementation/` | `/feature-implementation` | GitHub issue → branch → PR → review → merge |
| `contract-audit/` | `/contract-audit` | FE/BE API alignment audit → GitHub issues |
| `create-cross-repo-issues/` | `/create-cross-repo-issues` | Create paired FE+BE GitHub issues |

### Reference Skills (instant lookup)

| Directory | Command | What it does |
|---|---|---|
| `codebase-overview/` | `/codebase-overview` | Architecture snapshot (replaces exploring 10-20 files) |
| `code-review-local/` | `/code-review` | PR code review (primary method — GitHub Actions disabled) |
| `migration-workflow/` | `/migration` | EF Core migration create → review → apply → verify |
| `list-issues/` | (use `gh` CLI directly) | Live GitHub issue queries |

### Template Skills (generate structured output)

| Directory | Command | What it does |
|---|---|---|
| `write-user-story/` | (invoked by agents) | Compliance-ready user story from regulatory gap |
| `open-github-issue/` | (invoked by agents) | GitHub issue creation procedure |

---

## OpenCode Command Integration

The main workflow skills are also wired as OpenCode commands in `opencode.json`:

| Type in TUI | Runs |
|---|---|
| `/feature-implementation` | feature-implementation skill + workflow |
| `/compliance-feature` | compliance-feature skill + workflow |
| `/contract-audit` | contract-audit skill + workflow |
| `/code-review` | code-review-local skill |
| `/context` | session context summary |
| `/codebase-overview` | architecture reference |
| `/migration` | EF Core migration workflow |

---

## Design Principles

- **Directory per skill**: `skills/<name>/SKILL.md` — required by OpenCode discovery
- **Self-contained**: no `Read <other-file>` indirection — full steps embedded
- **On-demand**: loaded only when invoked, not in base context
- **Agent chains documented**: each process skill shows who calls who + handoff artifacts

Full workflow specs (deeper reference): `.claude/workflows/`
