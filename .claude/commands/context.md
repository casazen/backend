---
description: Show current project status — regulatory update date, roadmap status, open issue count, available skills and agents.
---

Show the current CasaZen session context:

!`bash .claude/hooks/scripts/session-context.sh 2>/dev/null || echo "Tech: .NET 10 / EF Core / SQL Server"`

Available commands (type /command-name):
- /feature-implementation — implement open issues end-to-end
- /compliance-feature    — regulatory scan + gap analysis + issue backlog
- /contract-audit        — FE/BE API alignment audit
- /code-review           — local PR code review
- /codebase-overview     — architecture reference
- /migration             — EF Core migration workflow

Available agents (@mention to invoke):
- @regulatory-agent      — scan Italian regulations
- @analyzer-agent        — gap analysis vs codebase
- @github-agent          — create GitHub issues from gap report
- @scrum-master-casazen  — cross-repo coordination
- @feature-developer     — branch + implement + PR
