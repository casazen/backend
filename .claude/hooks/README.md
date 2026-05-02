# Hooks

Real executable hooks that fire automatically during Claude Code sessions.

Hooks are configured in `.claude/settings.json` and call scripts in `hooks/scripts/`.

---

## Active Hooks

### 1. `filter-test-output.sh` — PreToolUse on `dotnet test`

**Trigger**: any `Bash` tool call containing `dotnet test`
**Script**: `.claude/hooks/scripts/filter-test-output.sh`
**Savings**: ~80-90% token reduction on test runs

Strips verbose passing-test output, keeps only:
- Test failure blocks + stack traces
- Summary lines (Passed/Failed/Skipped counts)

### 2. `filter-build-output.sh` — PreToolUse on `dotnet build`

**Trigger**: any `Bash` tool call containing `dotnet build`
**Script**: `.claude/hooks/scripts/filter-build-output.sh`
**Savings**: ~70-80% token reduction on build runs

Strips informational lines, keeps only:
- Compiler errors (`error CS...`)
- Compiler warnings (`warning CS...`)
- Build success/failure status line

### 3. `session-context.sh` — UserPromptSubmit

**Trigger**: first user message in each session
**Script**: `.claude/hooks/scripts/session-context.sh`
**Cost**: ~100 tokens per session (minimal)

Injects a compact project context snapshot:
- Tech stack + layer summary
- Regulatory context last updated date
- Roadmap status (EXISTS / MISSING)
- Open issue count
- Quick-start skill reminders

---

## Scripts

```
.claude/hooks/scripts/
  filter-test-output.sh   PreToolUse — dotnet test output filter
  filter-build-output.sh  PreToolUse — dotnet build output filter
  session-context.sh      UserPromptSubmit — compact session bootstrap
```

---

## Configuration

### Claude Code
Hooks are wired in `.claude/settings.json` (PreToolUse, correct schema):
- `Bash(dotnet test*)` → `filter-test-output.sh`
- `Bash(dotnet build*)` → `filter-build-output.sh`

`session-context.sh` is invoked by the `/context` command in `.claude/commands/context.md`.

### OpenCode
The filter scripts can be called via `!` shell interpolation in commands (see `opencode.json`).
`session-context.sh` is invoked by the `/context` command in `opencode.json`.

To disable a filter: remove its entry from `settings.json` (Claude Code) or the `!` call in `opencode.json` (OpenCode).

---

## What is NOT in hooks/

Workflow process specs are in `.claude/workflows/` — not here.
The old `hooks/common/review-process.md` has been removed; canonical version is `.claude/workflows/common/review-process.md`.

Workflow specs:
- `feature-implementation` → `.claude/workflows/feature-implementation.md`
- `compliance-feature-creation` → `.claude/workflows/compliance-feature-creation.md`
- `contract-audit` → `.claude/workflows/contract-audit.md`
- `review-process` → `.claude/workflows/common/review-process.md`

Skills (invocable via `/command-name`):
- `.claude/skills/<name>/SKILL.md` — each skill is a directory containing `SKILL.md`
