# Skills - On-Demand Instructions

Skills provide detailed instructions that load only when invoked, keeping base context small.

## Available Skills

### `codebase-overview`
**Usage**: `/codebase-overview`
**Purpose**: Get instant project architecture overview without file exploration
**When**: Starting work, onboarding, navigation questions
**Replaces**: 10-20 file reads for basic codebase understanding

### `migration-workflow`
**Usage**: `/migration-workflow`
**Purpose**: Complete EF Core migration workflow (create → test → apply)
**When**: Modifying database schema (entities, relationships, indexes)
**Replaces**: Repeated reading of migration docs

## Creating New Skills

1. Create `.claude/skills/{skill-name}.md`
2. Add frontmatter:
   ```yaml
   ---
   name: skill-name
   description: Brief description (shown in skill list)
   invocable: true
   ---
   ```
3. Write detailed instructions in markdown
4. Invoke with `/{skill-name}` or via Skill tool

## Skill vs. CLAUDE.md

**CLAUDE.md**: Always loaded, should be concise (< 500 lines)
- Project overview
- Links to detailed rules
- Essential conventions

**Skills**: Loaded on-demand, can be verbose
- Detailed workflows
- Step-by-step procedures
- Domain knowledge

## Token Savings

Moving workflows from CLAUDE.md to skills:
- **Base context**: 20-30% smaller
- **Per-session**: Load only needed skills (not all workflows)
- **Cumulative**: Significant savings on long conversations

## Reference

https://code.claude.com/docs/en/skills
