# GitHub Flow - Mandatory Rules for All Agents

> **CRITICAL**: These rules are NON-NEGOTIABLE and MUST be followed by ALL agents (project-level and user-level)

## GitHub Flow Process

CasaZen follows **GitHub Flow** strictly. The workflow is:

```
1. Create feature branch from main
2. Implement changes and commit
3. Push branch to remote
4. **OPEN PULL REQUEST** ← MANDATORY
5. **Wait for code review** (automated or manual)
6. **Address review feedback** if any
7. Merge ONLY after approval
8. Deploy from main
```

## Agent-Specific Rules

### For Development Agents (feature_developer, architect, test_engineer)

**NEVER directly merge to main**. Your workflow MUST be:

```bash
# ✅ CORRECT
git checkout -b feature/descriptive-name
# ... implement changes ...
git add .
git commit -m "feat: description"
git push origin feature/descriptive-name
gh pr create --base main --title "..." --body "..."
# STOP HERE - Wait for approval

# ❌ WRONG - NEVER DO THIS
git checkout main
git merge feature/descriptive-name  # FORBIDDEN!
git push origin main                 # FORBIDDEN!
```

**Pull Request Requirements:**
- Title: Follow Conventional Commits format (`feat:`, `fix:`, etc.)
- Body MUST include:
  - **Summary**: What was changed and why
  - **Test Plan**: How to verify the changes
  - **Closes #X**: Link to issue
  - **🤖 Generated with [Claude Code](https://claude.com/claude-code)**
- Base branch: `main`
- Head branch: `feature/*` or `fix/*`

### For Release Manager (release_manager)

**ONLY the release_manager agent can merge to main**, and ONLY:
- After PR is approved
- After all CI checks pass
- After code review is complete

Merge command:
```bash
gh pr merge <number> --squash --delete-branch
```

### For Issue Planner (issue_planner)

When creating implementation plans, ALWAYS include:
```markdown
## GitHub Flow Steps
1. Create feature branch: `git checkout -b feature/descriptive-name`
2. Implement changes
3. Commit with conventional commits
4. Push to remote: `git push origin feature/descriptive-name`
5. **Open PR**: `gh pr create --base main --title "..." --body "..."`
6. **STOP and wait for approval** - Do NOT merge directly
```

### For Scrum Master / Coordinators

When orchestrating work:
- Verify PRs are created, not direct merges
- Track PR status, not direct commits to main
- Escalate if agents bypass PR process

## Verification Checklist

Before ANY agent completes a development task, verify:

- [ ] Feature branch created from main
- [ ] Changes committed to feature branch
- [ ] Feature branch pushed to remote
- [ ] **Pull Request opened on GitHub**
- [ ] PR includes proper description and test plan
- [ ] PR links to issue with "Closes #X"
- [ ] Agent has **NOT** merged to main directly
- [ ] Agent has **NOT** pushed to main directly

## Consequences of Violations

If an agent violates these rules:
1. Immediately undo the direct merge: `git reset --hard HEAD~1 && git push --force`
2. Recreate feature branch from implementation commit
3. Open proper PR
4. Update agent instructions to prevent recurrence
5. Document violation for agent improvement

## Code Review Integration

All PRs trigger automated Claude Code review via `.github/workflows/claude-code-review.yml`:
- Critical issues MUST be fixed before merge
- High priority issues SHOULD be fixed before merge
- Review comments appear inline in PR

## Branch Protection (Future)

Consider enabling branch protection on `main`:
- Require pull request before merging
- Require status checks to pass
- Require code review approval
- Restrict who can push to main

---

**Last Updated**: 2026-03-31
**Applies To**: ALL agents (project and user-level)
**Priority**: CRITICAL - Non-negotiable
