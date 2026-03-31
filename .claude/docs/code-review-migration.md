# Code Review Migration Guide

> **Purpose**: Compare custom workflow vs standardized Claude Code approach

---

## 📊 Side-by-Side Comparison

### Architecture

| Aspect | Old (Custom) | New (Standardized) |
|--------|--------------|-------------------|
| **Action Used** | Manual `npm install -g @anthropic-ai/claude-code` | `anthropics/claude-code-action@v1` |
| **Lines of Code** | 270 lines | ~100 lines |
| **Configuration** | Custom prompts + parsing logic | Declarative with `claude_args` |
| **Maintenance** | Manual updates required | Auto-updated by Anthropic |
| **Documentation** | Custom implementation | [Official docs](https://code.claude.com/docs/en/github-actions) |

### Features

| Feature | Old (Custom) | New (Standardized) |
|---------|--------------|-------------------|
| **Code Review** | ✅ via `code_reviewer` agent | ✅ Built-in |
| **Release Management** | ✅ via `release_manager` agent | ⚠️ Separate workflow recommended |
| **Quality Scoring** | ✅ A-F grading | ✅ Severity tags (🔴🟡🟢⚪) |
| **PR Comments** | ✅ via `gh pr comment` | ✅ Native inline comments |
| **Draft PR Handling** | ✅ Skip with check | ✅ Built-in skip logic |
| **Manual Trigger** | ❌ Not supported | ✅ `@claude review` |
| **Local Review** | ❌ Not available | ✅ `/code-review-local` skill |

### Review Output

| Output Location | Old (Custom) | New (Standardized) |
|-----------------|--------------|-------------------|
| **Inline Comments** | ✅ Single PR comment | ✅ Per-line annotations |
| **Check Run Summary** | ⚠️ Via job outputs | ✅ Native check run |
| **Severity Tagging** | ⚠️ In text | ✅ Visual markers (🔴🟡🟢⚪) |
| **Extended Reasoning** | ❌ Not included | ✅ Collapsible sections |
| **False Positive Filtering** | ❌ Manual | ✅ Automatic verification |

---

## 🎯 What to Keep from Custom Workflow

### ✅ Keep (Still Valuable)

1. **Release Management Job** (optional):
   - Deployment decision logic
   - CI/CD status checks
   - PR approval/changes workflow
   - **Recommendation**: Extract to separate workflow (`.github/workflows/release-management.yml`)

2. **Pre-Review Checks Job**:
   - Build verification
   - Test execution
   - Formatting checks
   - **Status**: Already included in new workflow (`pre-review-checks` job)

3. **Summary Job**:
   - Consolidated results display
   - **Status**: Replaced by native GitHub check run output

### ❌ Replace (Superseded by Standard Approach)

1. **Manual Claude CLI Installation**:
   ```yaml
   # OLD ❌
   - name: Install Claude Code
     run: npm install -g @anthropic-ai/claude-code

   # NEW ✅
   - uses: anthropics/claude-code-action@v1
   ```

2. **Custom Review Prompt + Parsing**:
   ```yaml
   # OLD ❌ (95 lines of custom logic)
   REVIEW_OUTPUT=$(claude --model sonnet -p "$(cat <<'PROMPT_EOF'
     Custom prompt with output format parsing...
   )")

   # NEW ✅ (3 lines)
   - uses: anthropics/claude-code-action@v1
     with:
       prompt: "Review this PR..."
   ```

3. **Output Parsing with grep**:
   ```yaml
   # OLD ❌
   QUALITY_SCORE=$(echo "$REVIEW_OUTPUT" | grep -oP 'QUALITY_SCORE:\s*\K[A-F]')

   # NEW ✅ (handled automatically by action)
   # Severity levels automatically tagged and displayed
   ```

---

## 🔄 Migration Strategy

### Option A: Clean Migration (Recommended)

**Steps**:
1. ✅ Enable new standardized workflow (`.github/workflows/claude-code-review.yml`)
2. ✅ Test on a sample PR
3. ✅ Disable old workflow (rename `daily-testing-and-review.yml` to `daily-testing-and-review.yml.disabled`)
4. ⏳ Monitor for 1-2 weeks
5. 🗑️ Delete old workflow if satisfied

**Benefits**:
- Clean break, no confusion
- Follows official best practices
- Easier maintenance

**Timeline**: 1-2 weeks testing period

### Option B: Gradual Migration

**Steps**:
1. ✅ Run both workflows in parallel
2. ✅ Compare outputs on PRs
3. ✅ Tune REVIEW.md based on feedback
4. ⏳ Gradually trust new workflow
5. 🗑️ Deprecate old workflow

**Benefits**:
- Lower risk
- Can compare outputs side-by-side
- Identify gaps before full switch

**Timeline**: 2-4 weeks testing period

### Option C: Hybrid Approach

**Steps**:
1. ✅ Use **new workflow** for code review
2. ✅ Keep **release management job** from old workflow in separate file
3. ✅ Create `.github/workflows/release-management.yml` with just the deployment logic

**Benefits**:
- Best of both worlds
- Standardized review, custom release logic
- Modular workflows

**Structure**:
```
.github/workflows/
├── claude-code-review.yml        (NEW - standardized review)
├── release-management.yml        (EXTRACTED - custom logic)
├── ci-cd.yml                     (EXISTING - build/test/deploy)
└── daily-testing-and-review.yml  (OLD - deprecated)
```

---

## 📝 Recommended Approach: Option C (Hybrid)

### Phase 1: Enable New Review Workflow

**Status**: ✅ **COMPLETED**

- Created `REVIEW.md` with comprehensive guidelines
- Created `.github/workflows/claude-code-review.yml` (standardized)
- Created `.claude/skills/code-review-local.md` for local reviews
- Updated `CLAUDE.md` with code review section

### Phase 2: Extract Release Management (Optional)

**Status**: ⏳ **PENDING** (only if you want to keep release logic)

<Accordion title="Create release-management.yml">
```yaml
name: Release Management

on:
  pull_request:
    types: [opened, synchronize, ready_for_review]
    branches: [main]

jobs:
  release-decision:
    name: Release Manager Decision
    runs-on: ubuntu-latest
    if: github.event.pull_request.draft == false

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20'

      - name: Install Claude Code
        run: npm install -g @anthropic-ai/claude-code

      # Use release_manager agent for deployment recommendations
      - name: Release Manager Agent
        env:
          ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          PR_NUMBER=${{ github.event.pull_request.number }}

          claude --model sonnet -p "
          You are the release_manager agent for CasaZen.

          Review PR #${PR_NUMBER} and make deployment recommendation:
          1. Check CI/CD status: gh pr checks ${PR_NUMBER}
          2. Review code review results (check for Critical issues)
          3. Make recommendation (approve/changes_requested)
          4. Decide deployment environment (production/staging/development/none)

          Provide rationale and post as PR comment.
          " --allowedTools "Bash,Read,Glob,Grep"
```
</Accordion>

**Decision**: Do you need custom release management logic, or is standard review sufficient?

### Phase 3: Deprecate Old Workflow

**Status**: ⏳ **PENDING** (after testing new workflow)

**Action**: Rename `daily-testing-and-review.yml` → `daily-testing-and-review.yml.disabled`

---

## 🧪 Testing Checklist

Before fully migrating, test the new workflow:

- [ ] Open a test PR with intentional issues
- [ ] Verify inline comments appear on specific lines
- [ ] Check severity tags (🔴🟡🟢⚪) are correct
- [ ] Test manual trigger: `@claude review`
- [ ] Run local review: `/code-review-local`
- [ ] Verify REVIEW.md guidelines are followed
- [ ] Check extended reasoning sections are useful
- [ ] Confirm no false positives on known patterns
- [ ] Test draft PR skipping behavior
- [ ] Verify `pre-review-checks` job runs successfully

---

## 💰 Cost Comparison

### Old Workflow

**Cost Structure**:
- GitHub Actions minutes: ~5-10 minutes per run
- Claude API calls: 1 review call per PR
- **Estimated**: ~$15-20 per PR

**Trigger**: Automatic on PR events

### New Workflow

**Cost Structure**:
- GitHub Actions minutes: ~2-5 minutes per run (more efficient)
- Claude API calls: 1 review call per PR
- **Estimated**: ~$15-25 per PR (same range)

**Trigger Options**:
- Automatic (same as old)
- Manual only (cost on-demand)
- Hybrid (auto for main, manual for develop)

**Optimization**:
- Use `/code-review-local` for free pre-checks
- Configure manual trigger mode for high-traffic repos
- Set spending caps at [usage settings](https://claude.ai/admin-settings/usage)

---

## 🎓 Learning Resources

### Official Documentation
- [Code Review](https://code.claude.com/docs/en/code-review) - Full review system guide
- [GitHub Actions](https://code.claude.com/docs/en/github-actions) - Integration standards
- [CLAUDE.md Memory](https://code.claude.com/docs/en/memory) - Project context files
- [Cost Management](https://code.claude.com/docs/en/costs) - Token optimization

### Internal Documentation
- @.claude/docs/code-review-setup.md - Setup guide
- @REVIEW.md - Review guidelines
- @CLAUDE.md - Project overview

---

## 🤔 Decision Matrix

**Choose NEW (Standardized) if**:
- ✅ Want official support and updates
- ✅ Prefer simpler configuration
- ✅ Need inline PR comments on specific lines
- ✅ Want manual trigger capability (`@claude review`)
- ✅ Value maintainability over customization

**Keep OLD (Custom) if**:
- ⚠️ Need highly custom scoring logic (A-F grades)
- ⚠️ Require integrated release management in same workflow
- ⚠️ Have specific output parsing requirements
- ⚠️ Cannot adapt to new severity levels (🔴🟡🟢⚪)

**Hybrid Approach (Recommended) if**:
- ✅ Want standardized review (new)
- ✅ Need custom release logic (extracted from old)
- ✅ Best of both worlds

---

## 📞 Support

**Questions about migration?**
- GitHub Issues: https://github.com/anthropics/claude-code-action/issues
- Claude Code Docs: https://code.claude.com/docs
- Internal: @.claude/docs/code-review-setup.md

---

**Status**: ✅ New workflow ready to test
**Recommendation**: Start with Option C (Hybrid) - standardized review + optional custom release logic
**Next Step**: Test new workflow on a sample PR

**Last Updated**: 2026-03-31
**Maintained By**: CasaZen Development Team
