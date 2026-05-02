---
description: Implement the oldest open GitHub issue end-to-end — branch, code, PR, review (max 3 iterations), merge.
disable-model-invocation: true
allowed-tools: Bash Read Write Edit Grep Glob
---

Run the feature-implementation workflow.

Full instructions: @.claude/skills/feature-implementation/SKILL.md

Execute every step:
1. Check open issues (exclude epics): `gh issue list --state open --json number,title,labels --jq '[.[] | select(all(.labels[]; .name != "epic"))]'`
2. If no issues → run /compliance-feature first, then resume
3. Analyze + prioritize (compliance deadline > priority:critical > priority:high > effort)
4. Plan (BE-first, API contract, DB migrations, testing strategy)
5. @feature-developer implements on feature branch + opens PR
6. Run /code-review (max 3 iterations, delta only)
7. Fix Critical/High findings
8. @release-manager merges: `gh pr merge <number> --squash --delete-branch`
