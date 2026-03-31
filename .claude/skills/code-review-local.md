---
name: code-review-local
description: Run code review locally after opening a PR (current active approach)
invocable: true
tags: [review, quality, testing]
---

# Local Code Review ⭐ **PRIMARY REVIEW METHOD**

> **Purpose**: Review PR changes locally to catch issues before merging (cost-free alternative to GitHub Actions)
>
> **Current Strategy**: Automated GitHub Actions review is disabled for cost savings. This local skill is the **active approach**.

## When to Use

- **After opening a PR** (primary workflow) ✅
- After addressing review comments
- Before requesting final approval
- When you want immediate feedback without triggering paid workflow

## Review Process

I'll perform a comprehensive code review following the same standards as the automated GitHub Actions review:

1. **Analyze Changes**:
   - Get current git diff (staged + unstaged changes)
   - Review modified files in context of surrounding code
   - Check against REVIEW.md and CLAUDE.md guidelines

2. **Review Criteria** (same as automated review):
   - 🔴 **Security**: SQL injection, XSS, auth bypass, secrets exposure
   - 🔴 **Compliance**: CIN codes, GDPR, tourist tax regulations
   - 🟡 **Async Patterns**: Proper async/await, no .Result/.Wait()
   - 🟡 **Testing**: Test coverage for new features
   - 🟢 **Architecture**: Repository pattern, DI, layer separation
   - 🟢 **Code Quality**: SOLID principles, clean code, naming
   - ⚪ **Style**: Conventions, formatting (if not caught by dotnet format)

3. **Report Findings**:
   - List issues by severity (Critical → High → Medium → Low)
   - Provide file:line references for each issue
   - Include actionable fix suggestions
   - Highlight regulatory compliance issues
   - Summarize overall code quality score

4. **Additional Checks**:
   - Verify EF Core migrations for schema changes
   - Check if tests exist for new features
   - Validate commit message format (Conventional Commits)
   - Ensure no secrets in code

## Output Format

```
📋 Local Code Review Results
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📊 Summary:
- Files changed: X
- Lines added: +Y / removed: -Z
- Quality Score: [A|B|C|D|F]
- Critical Issues: [YES|NO]

🔴 Critical Issues (must fix):
1. [File:Line] Issue description
   → Suggested fix

🟡 High Severity (should fix):
...

🟢 Medium Severity (consider fixing):
...

⚪ Low Severity (optional):
...

✅ Passed Checks:
- [✓] No secrets detected
- [✓] All async methods have Async suffix
- [✓] Tests included for new features
- ...

❌ Failed Checks:
- [✗] Missing EF Core migration for schema change
- ...

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Next Steps:
- Fix critical issues before committing
- Address high severity issues
- Run `dotnet test` to verify tests pass
- Run `dotnet format` to fix formatting
```

## Usage Examples

```bash
# Review current changes
/code-review-local

# Review specific branch
/code-review-local feature/user-authentication

# Review specific files
/code-review-local src/Casazen.Core/Entities/Property.cs
```

## Notes

- **Current approach**: This is the **primary review method** (GitHub Actions review is disabled)
- **Zero cost**: Runs locally, no API consumption via GitHub Actions
- **Same standards**: Follows REVIEW.md and CLAUDE.md guidelines (identical to automated review)
- **Workflow**: Developer opens PR → Run this skill → Address findings → Merge
- Review is based on current git diff (committed + uncommitted changes)
- For full context, ensure you're on the correct branch

## Future Migration

When automated GitHub Actions review is re-enabled:
- This skill will remain useful for **pre-PR review** (catch issues before opening PR)
- GitHub Actions will handle **post-PR review** (automatic inline comments)
- Both approaches use the same standards (REVIEW.md)

---

**Related**: @REVIEW.md | @.claude/rules/ | @.claude/docs/code-review-setup.md | [Claude Code Review](https://code.claude.com/docs/en/code-review)
