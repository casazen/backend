---
description: Full compliance workflow — scan Italian regulations, gap analysis vs codebase, competitor research, create prioritized GitHub issue backlog.
disable-model-invocation: true
allowed-tools: Bash Read Write Edit Grep Glob WebSearch WebFetch
---

Run the compliance-feature workflow.

Full instructions: @.claude/skills/compliance-feature/SKILL.md

Execute every step:
1. Check roadmap: `ls .claude/context/planning/product-roadmap.md 2>/dev/null || echo MISSING`
2. Check epics: `gh issue list --label epic --state open`
3. If roadmap or epics missing → Refinement Meeting in-memory (@product-owner + @architect + @scrum-master-casazen) → write product-roadmap.md + create epic issues
4. @regulatory-agent: scan 8 regulatory topics, update .claude/context/regulations/
5. @analyzer-agent: gap analysis (MISSING/PARTIAL/OUTDATED/COMPLIANT), produce gap report
6. Competitive research: WebSearch Lodgify, Guesty, Hostaway per gap found
7. @scrum-master-casazen: create GitHub issues (max 10, CRITICAL first, linked to epics)
