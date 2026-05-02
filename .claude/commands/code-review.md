---
description: Review current PR changes against CasaZen quality standards — security, compliance, async patterns, testing. Primary review method (GitHub Actions disabled for cost).
disable-model-invocation: true
allowed-tools: Bash Read Grep Glob
---

Run a local code review on the current changes.

Full instructions: @.claude/skills/code-review-local/SKILL.md

Steps:
1. `git diff main...HEAD --stat` — overview
2. `git diff main...HEAD` — full diff
3. Review against REVIEW.md + .claude/rules/ by severity:
   - 🔴 Critical: security, GDPR violations, missing CIN validation, auth bypass
   - 🟡 High: missing await / .Result use, missing tests, SOLID violations
   - 🟢 Medium: N+1 queries, hardcoded config values, pattern bypassed
   - ⚪ Low: naming, formatting
4. Check: migration present for schema changes, tests for new logic, Conventional Commits format
5. Output findings with file:line references + quality score A–F

Re-review: delta only, max 3 iterations total per PR.
