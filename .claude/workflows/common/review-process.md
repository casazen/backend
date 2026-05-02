# Shared Process: Code Review (max 3 iterations)

> Reusable process. Used by `feature-implementation.md` and any workflow that opens PRs.

---

## When to Use

Invoke `@code_reviewer` after a PR is opened by `@feature_developer`.

---

## Iteration Protocol

### Review checks
- Logic correctness vs. issue requirements
- Code quality: SOLID, async patterns, testing (see `.claude/rules/code-style.md`)
- Security: SQL injection, XSS, secrets, auth (see `.claude/rules/security.md`)
- API contract coherence (if full-stack feature)
- Obvious regressions
- Compliance-specific checks (see `.claude/rules/compliance.md`)

### Severity
| Level | Action |
|---|---|
| 🔴 Critical | MUST fix before merge (security, compliance, deadlock) |
| 🟡 High | SHOULD fix before merge (missing tests, SOLID violations) |
| 🟢 Medium | Consider fixing (duplication, complexity) |
| ⚪ Low | Optional (style, naming) |

### If APPROVED
→ hand off to `@release_manager` for merge.

### If CHANGES REQUESTED
1. Forward findings to `@feature_developer`
2. Fix only the flagged items — no unrelated refactoring
3. Push fixes
4. Re-review only the **delta** (changed lines + previously flagged items)
5. Do not re-examine already-approved sections

### Anti-loop: iteration limit
**Maximum 3 iterations per PR.**

After 3 iterations with unresolved Critical/High findings:
1. Stop — do not iterate further
2. Produce an **escalation report**:
   - Remaining unresolved findings (list)
   - Summary of what each iteration changed
   - Recommendation for manual resolution
3. Mark PR status: `ESCALATION_REQUIRED`

---

## Output Format (per iteration)

```markdown
## Review Iteration N/3

**PR**: <link>
**Status**: APPROVED | CHANGES_REQUESTED | ESCALATION_REQUIRED

### Findings
- 🔴 [Critical] <description> — file:line
- 🟡 [High] <description> — file:line
- 🟢 [Medium] <description> — file:line

### Resolved Since Last Iteration
- [x] Finding #1 fixed in commit <hash>
- [ ] Finding #2 still open

**Next**: Iteration N+1 | APPROVED | ESCALATION
```

---

## Rules

- Review delta only between iterations — never re-examine the full codebase
- Do not introduce new requirements mid-review
- Do not reopen approved sections (unless the developer's fix touched them)
- Flag ambiguities explicitly rather than making assumptions
