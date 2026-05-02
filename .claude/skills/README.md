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

## Command Integration (dual-mode)

Workflow skills are wired as invocable commands in both tools:

| Command | Claude Code | OpenCode |
|---|---|---|
| `/feature-implementation` | `.claude/commands/feature-implementation.md` | `opencode.json` command |
| `/compliance-feature` | `.claude/commands/compliance-feature.md` | `opencode.json` command |
| `/contract-audit` | `.claude/commands/contract-audit.md` | `opencode.json` command |
| `/code-review` | `.claude/commands/code-review.md` | `opencode.json` command |
| `/context` | `.claude/commands/context.md` | `opencode.json` command |
| `/codebase-overview` | `.claude/commands/codebase-overview.md` | `opencode.json` command |
| `/migration` | `.claude/commands/migration.md` | `opencode.json` command |

---

## Design Principles

- **Directory per skill**: `skills/<name>/SKILL.md` — required by OpenCode discovery
- **Self-contained**: no `Read <other-file>` indirection — full steps embedded
- **On-demand**: loaded only when invoked, not in base context
- **Agent chains documented**: each process skill shows who calls who + handoff artifacts

Full workflow specs (deeper reference): `.claude/workflows/`
