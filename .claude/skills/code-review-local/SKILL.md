---
name: code-review-local
description: Run a code review on current PR changes following the same standards as the automated GitHub Actions review. Primary review method while GitHub Actions review is disabled for cost savings. Use after opening a PR or before requesting merge.
---

# Local Code Review

**Current strategy**: GitHub Actions automated review is disabled (cost). This skill is the active review method.

## When to use

- After opening a PR (primary workflow)
- After addressing review feedback (re-review delta)
- Before requesting merge

## Review process

**1. Analyze changes**

```bash
git diff main...HEAD --stat        # overview
git diff main...HEAD               # full diff
```

Read modified files in surrounding context to understand intent.

**2. Check against standards**

Review against `REVIEW.md` + `.claude/rules/`:

| Level | What to check |
|---|---|
| 🔴 Critical | SQL injection, XSS, auth bypass, secrets in code, GDPR violations, missing CIN validation |
| 🟡 High | Missing `await` / `.Result` / `.Wait()` use, missing tests for new features, SOLID violations |
| 🟢 Medium | Repository pattern bypassed, N+1 queries, hardcoded values that should be config |
| ⚪ Low | Naming conventions, formatting, missing XML docs on public API |

**Italian regulatory checks** (always verify):
- CIN field present and validated (`IT-XXXXX-XXXXXXXXXX` format) when touching Property
- GDPR guest data handling (retention policy, consent flag) when touching Guest
- Tourist tax rate read from `TaxRate` entity — never hardcoded
- Alloggiati Web integration not bypassed when touching booking check-in flow

**3. Additional checks**

- EF Core migration present for any schema change (`Casazen.Infrastructure/Data/Migrations/`)
- Tests exist for new business logic (unit) and new endpoints (integration)
- Commit messages follow Conventional Commits (`feat:`, `fix:`, `refactor:`, etc.)
- No secrets or credentials in code

**4. Output findings**

```
📋 Code Review Results
────────────────────────────────────────

📊 Summary:
  Files changed: X | +Y / -Z lines
  Quality score: A/B/C/D/F
  Critical issues: YES / NO

🔴 Critical (must fix before merge):
  1. src/File.cs:42 — Issue description
     → Suggested fix

🟡 High (should fix before merge):
  ...

🟢 Medium (consider fixing):
  ...

⚪ Low (optional):
  ...

✅ Passed checks:
  ✓ No secrets detected
  ✓ Async patterns correct
  ✓ Tests included
  ✓ Migration present (if schema changed)

❌ Failed checks:
  ✗ Missing migration for schema change
  ...

────────────────────────────────────────
Next steps:
  Fix Critical → push → re-run review (delta only)
```

## Re-review protocol

When re-reviewing after fixes:
- Review **only the delta** (lines changed since last review)
- Do not re-examine already-approved sections
- Reference the previous review findings explicitly

Max 3 iterations total per PR. After 3 iterations with unresolved Critical/High → escalation report.

## References

- `REVIEW.md` — review-specific rules
- `.claude/rules/security.md` — security guardrails
- `.claude/rules/code-style.md` — async + testing standards
- `.claude/rules/compliance.md` — Italian regulatory compliance checks
- `.claude/workflows/common/review-process.md` — full review protocol
