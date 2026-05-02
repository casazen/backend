---
description: Audit FE/BE API contract alignment — parallel read of backend + frontend, gap analysis across 4 categories, create GitHub issue per misalignment. No code changes.
disable-model-invocation: true
allowed-tools: Bash Read Grep Glob
---

Run the contract-audit workflow. Audit only — NO code changes, NO branches, NO PRs.

Full instructions: @.claude/skills/contract-audit/SKILL.md

Execute every step:
1. Read backend (API_DOCUMENTATION.md, Controllers/*.cs, Core/*.cs) in parallel with frontend (src/types/*.ts, src/api/*.ts, src/queries/*.ts)
2. Gap analysis: TypeScript Types / API Client / Query Hooks / Documentation
3. @github-agent: create 1 issue per gap + 1 summary issue on backend
4. Report totals by category and severity
