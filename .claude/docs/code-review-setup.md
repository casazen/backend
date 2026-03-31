# Code Review Setup Guide

> **Standard**: Follows [Claude Code official documentation](https://code.claude.com/docs/en/code-review) and [GitHub Actions standards](https://code.claude.com/docs/en/github-actions)

---

## 📋 Overview

CasaZen uses **standardized** Claude Code automated reviews with two modes:

1. **Automated GitHub Actions Review**: Runs automatically on PR events
2. **Local Review**: Run manually before opening a PR (via skill)

**Key Files**:
- `REVIEW.md` - Review-specific guidelines (auto-discovered by Claude)
- `.github/workflows/claude-code-review.yml` - Automated PR review workflow
- `.claude/skills/code-review-local.md` - Local review skill

---

## 🚀 Quick Start

### Prerequisites

1. **GitHub Secrets** (repository admin required):
   - `ANTHROPIC_API_KEY` - Your Claude API key from [console.anthropic.com](https://console.anthropic.com)

2. **GitHub App** (recommended for best control):
   - Option A: Use official [Claude GitHub App](https://github.com/apps/claude)
   - Option B: Create custom GitHub App (see [3P Providers setup](https://code.claude.com/docs/en/github-actions#create-a-custom-github-app-recommended-for-3p-providers))

### Setup Steps

<Steps>
  <Step title="Install GitHub App">
    **Option A - Official Claude App (Recommended for Direct API)**:
    1. Go to [https://github.com/apps/claude](https://github.com/apps/claude)
    2. Click "Install" and select your repository
    3. Grant permissions: Contents (read/write), Issues (read/write), Pull requests (read/write)

    **Option B - Custom App (Required for AWS Bedrock / Google Vertex AI)**:
    1. Follow instructions in [GitHub Actions docs](https://code.claude.com/docs/en/github-actions#create-a-custom-github-app-recommended-for-3p-providers)
    2. Add `APP_ID` and `APP_PRIVATE_KEY` secrets to repository
  </Step>

  <Step title="Add API Key Secret">
    1. Go to repository Settings → Secrets and variables → Actions
    2. Click "New repository secret"
    3. Name: `ANTHROPIC_API_KEY`
    4. Value: Your API key from [console.anthropic.com](https://console.anthropic.com/settings/keys)
    5. Click "Add secret"
  </Step>

  <Step title="Verify Setup">
    1. Open a test PR or push to an existing PR
    2. Check workflow runs: Actions tab → "Claude Code Review"
    3. Review comments should appear in "Files changed" tab
    4. Test manual trigger: Comment `@claude review` on a PR
  </Step>
</Steps>

---

## 🔄 How It Works

### Automated Review (GitHub Actions)

**Trigger Conditions**:
- ✅ PR opened/updated (if not draft)
- ✅ PR marked ready for review
- ✅ `@claude review` or `@claude` mentioned in PR/issue comment
- ✅ Manual workflow dispatch

**What Claude Checks** (from REVIEW.md):
1. 🔴 **Critical**: Security, secrets, regulatory compliance, deadlock risks
2. 🟡 **High**: Missing tests, async patterns, SOLID violations
3. 🟢 **Medium**: Code duplication, complexity, hardcoded values
4. ⚪ **Low**: Style improvements, naming suggestions

**Review Process**:
1. Analyzes PR diff + surrounding code context
2. Verifies against REVIEW.md and CLAUDE.md guidelines
3. Posts inline comments on specific lines
4. Tags by severity (🔴 🟡 🟢 ⚪)
5. Provides extended reasoning for each finding

**Output Locations**:
- **Inline comments**: Files changed tab (specific lines)
- **Summary comment**: PR conversation
- **Check run**: Actions tab → Claude Code Review

### Local Review (Before PR)

**Usage**:
```bash
# In Claude Code CLI
/code-review-local

# Or invoke manually
claude -p "Run code-review-local skill to review my current changes"
```

**Benefits**:
- Catch issues before opening PR
- Faster feedback loop
- No GitHub Actions minutes consumed
- Same standards as automated review

---

## 📚 Customization

### Update Review Guidelines

Edit `REVIEW.md` to customize what Claude checks:

```markdown
## ✅ Always Check
- Add your specific requirements here

## 🚫 Skip / Ignore
- Add patterns Claude should ignore
```

Changes take effect immediately on next review.

### Adjust Workflow Settings

Edit `.github/workflows/claude-code-review.yml`:

```yaml
claude_args: |
  --max-turns 10              # Max conversation turns (default: 10)
  --model claude-sonnet-4-6   # Model to use (sonnet recommended)
  --append-system-prompt "Additional context for Claude"
```

**Available models**:
- `claude-opus-4-6` - Most capable (higher cost)
- `claude-sonnet-4-6` - Balanced (recommended)
- `claude-haiku-4-5` - Fast and cheap (not recommended for review)

### Change Trigger Behavior

**Current**: Automatic on PR open/update + manual `@claude` trigger

**Alternative A - Manual Only**:
```yaml
on:
  issue_comment:
    types: [created]
  pull_request_review_comment:
    types: [created]
```

**Alternative B - Every Push**:
```yaml
on:
  pull_request:
    types: [opened, synchronize, ready_for_review, reopened]
  push:
    branches: [main, develop]
```

---

## 💡 Best Practices

### For Developers

1. **Run Local Review First**:
   ```bash
   /code-review-local
   ```
   Fix issues before opening PR → faster review cycle

2. **Address Critical Issues**:
   - 🔴 Critical: Must fix before merge
   - 🟡 High: Should fix before merge
   - 🟢 Medium: Fix if time permits
   - ⚪ Low: Optional improvements

3. **Use @claude for Questions**:
   ```
   @claude how should I implement authentication for this endpoint?
   @claude is this the right approach for handling CIN validation?
   ```

4. **Request Re-Review**:
   ```
   @claude review
   ```
   After addressing feedback, trigger a fresh review

### For Maintainers

1. **Monitor Review Quality**:
   - Check [analytics dashboard](https://claude.ai/analytics/code-review)
   - Review per-repo costs and feedback resolution rates

2. **Tune REVIEW.md**:
   - Add project-specific patterns Claude should check
   - Document known false positives to skip

3. **Set Spending Limits**:
   - Configure at [usage settings](https://claude.ai/admin-settings/usage)
   - Typical cost: $15-25 per review

4. **Integrate with Branch Protection**:
   ```yaml
   # .github/workflows/claude-code-review.yml
   # Parse check run output for critical issues
   # Fail workflow if critical issues found
   ```

---

## 🔧 Troubleshooting

### Claude Not Responding

**Symptoms**: No review comments after PR opened

**Solutions**:
1. Check workflow ran: Actions tab → "Claude Code Review"
2. Verify `ANTHROPIC_API_KEY` secret is set
3. Confirm GitHub App has permissions (Contents, Issues, PRs)
4. Check PR is not draft (or use `ready_for_review` event)
5. Try manual trigger: `@claude review`

### Review Failed / Timed Out

**Symptoms**: Workflow fails or exceeds 30min timeout

**Solutions**:
1. Trigger fresh review: `@claude review`
2. Check API key is valid
3. Review workflow logs for errors
4. Reduce `--max-turns` in `claude_args`

### Comments Not Showing Inline

**Symptoms**: Check run shows findings but no inline comments

**Solutions**:
1. Check **Files changed** tab for annotations
2. Look in check run **Details** for severity table
3. Check review body for "Additional findings" section
4. Findings on moved lines appear in review body, not inline

### Too Many False Positives

**Solutions**:
1. Update `REVIEW.md` → "🚫 Skip / Ignore" section
2. Document known patterns Claude should ignore
3. Use `--append-system-prompt` in workflow for additional context

---

## 📊 Cost Management

### Typical Costs

- **Per Review**: $15-25 (varies by PR size and complexity)
- **Trigger Impact**:
  - Once per PR: ~$20/PR
  - Every push: $20 × number of pushes
  - Manual only: Only when requested

### Optimization Tips

1. **Use Manual Trigger Mode** for high-traffic repos
2. **Run Local Review First** (free, catches issues early)
3. **Set Monthly Spending Cap** at [usage settings](https://claude.ai/admin-settings/usage)
4. **Monitor via Analytics** at [code-review dashboard](https://claude.ai/analytics/code-review)

---

## 🔗 Migration from Custom Workflow

### What Changed

| Old (Custom)                           | New (Standardized)                    |
|----------------------------------------|---------------------------------------|
| Manual Claude CLI installation         | `anthropics/claude-code-action@v1`    |
| Custom agent invocation                | Built-in review logic                 |
| Output parsing with grep               | Automatic inline comments             |
| Multiple jobs (review + release)       | Single focused job                    |
| Custom scoring logic                   | Severity tags (🔴 🟡 🟢 ⚪)            |

### Benefits of Standard Approach

✅ **Simpler Configuration**: 30 lines vs 270 lines
✅ **Official Support**: Maintained by Anthropic
✅ **Better Integration**: Native GitHub PR comments
✅ **Automatic Updates**: Action updates without workflow changes
✅ **Standard Patterns**: Follows documented best practices

### Backward Compatibility

Old workflow (`daily-testing-and-review.yml`) can coexist with new workflow:
- Old: Custom review with release management logic
- New: Standard code review following official docs

**Recommendation**: Gradually migrate to new workflow, then deprecate old one.

---

## 📚 Additional Resources

- [Claude Code Review Docs](https://code.claude.com/docs/en/code-review)
- [GitHub Actions Standards](https://code.claude.com/docs/en/github-actions)
- [CLAUDE.md Memory Guide](https://code.claude.com/docs/en/memory)
- [Cost Management](https://code.claude.com/docs/en/costs)

---

**Last Updated**: 2026-03-31
**Maintained By**: CasaZen Development Team
