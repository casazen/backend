# 🤖 CasaZen Agent Orchestrators - Setup Complete

## What Was Created

This setup provides **fully automated daily development cycles** for CasaZen using AI agent orchestration.

### Created Files

#### GitHub Workflows (3 files)
1. **`.github/workflows/daily-development.yml`**
   - Runs daily at 8:00 AM UTC
   - Orchestrates: product_owner → architect → scrum_master → issue_planner → feature_developer
   - **Implementation mode**: Implements 1 issue from backlog
   - **Planning mode**: Creates new issues when backlog is empty

2. **`.github/workflows/daily-testing-and-review.yml`**
   - Runs daily at 5:00 PM UTC
   - Orchestrates: test_engineer → code_reviewer → release_manager
   - Runs comprehensive tests with coverage
   - Reviews code quality and security
   - Auto-merges PRs if all checks pass
   - Sends email report to `luca.lamal@hotmail.it`

3. **`.github/workflows/regulatory-agents.yml`** *(already existed)*
   - Runs monthly on 1st at 8:00 AM UTC
   - Orchestrates: regulatory_agent → analyzer_agent → github_agent

#### Documentation (6 files)
4. **`.claude/ORCHESTRATORS.md`**
   - Complete orchestrator documentation
   - Workflow logic diagrams
   - Agent ecosystem overview
   - Monitoring and troubleshooting guide

5. **`.claude/reports/README.md`**
   - Daily reports directory documentation
   - Report types and formats
   - Usage examples

6. **`.claude/sprint/README.md`**
   - Sprint planning documents directory
   - Planning workflow documentation
   - Integration with GitHub issues

7. **`.claude/coordination/README.md`**
   - Cross-repository coordination documentation
   - Feature tracking for backend + frontend

8. **`.github/SECRETS_SETUP.md`**
   - Required secrets configuration guide
   - Step-by-step setup instructions
   - Cost estimation and monitoring

9. **`ORCHESTRATORS_SETUP.md`** *(this file)*
   - Setup summary and quick start guide

#### Directories (3 created)
- `.claude/reports/` - Daily test, code review, and PR management reports
- `.claude/sprint/` - Sprint planning and issue implementation plans
- `.claude/coordination/` - Cross-repo feature coordination

---

## Quick Start

### 1. Configure Secrets (Required)

The workflows will fail without these secrets. Follow `.github/SECRETS_SETUP.md` for detailed instructions.

**Go to**: Repository Settings → Secrets and variables → Actions

**Add these secrets**:

1. **`ANTHROPIC_API_KEY`**
   - Get from: https://console.anthropic.com/
   - Used for: Claude Code agent execution

2. **`SENDGRID_API_KEY`**
   - Get from: https://app.sendgrid.com/
   - Used for: Daily email reports
   - **Also verify sender**: `noreply@casazen.app` in SendGrid

Quick verification:
```bash
gh secret list
# Should show:
# ANTHROPIC_API_KEY
# SENDGRID_API_KEY
```

### 2. Test Workflows (Optional but Recommended)

Before waiting for automatic runs, test manually:

```bash
# Test development orchestrator
gh workflow run daily-development.yml

# Test testing & review orchestrator
gh workflow run daily-testing-and-review.yml

# Check run status
gh run list --limit 3

# View detailed logs
gh run view --log
```

Check your email (`luca.lamal@hotmail.it`) for the testing report.

### 3. Monitor First Automatic Runs

**Tomorrow morning (8:00 AM UTC)**:
- Daily Development will check for issues
- If no issues exist, it will create new ones
- If issues exist, it will implement the oldest one

**Tomorrow evening (5:00 PM UTC)**:
- Daily Testing & Review will test today's changes
- Review code quality
- Decide on PR merges
- Send email report

### 4. Review Generated Artifacts

After first runs, check:

```bash
# Sprint planning documents
ls -la .claude/sprint/

# Daily reports
ls -la .claude/reports/

# View latest reports
cat .claude/reports/test-report-$(date +%Y-%m-%d).md
cat .claude/reports/code-review-$(date +%Y-%m-%d).md
cat .claude/reports/pr-management-$(date +%Y-%m-%d).md
```

---

## Daily Cycle Overview

### Morning (8:00 AM UTC / 9:00 CET)
```
Daily Development Orchestrator
    ↓
┌─ Has open issues? ─────────────────┐
│   YES                      NO       │
│   ↓                        ↓        │
│  Implementation        Planning     │
│   ↓                        ↓        │
│  • issue_planner      • product_owner
│  • feature_developer  • architect   │
│  • Implement 1 issue  • scrum_master
│  • Commit to branch   • Create 2 issues
│                                     │
└─────────────────────────────────────┘
            ↓
    Changes committed
```

### Evening (5:00 PM UTC / 6:00 CET)
```
Daily Testing & Review Orchestrator
    ↓
┌─ Test & Review ────────────────────┐
│                                    │
│  1. test_engineer                  │
│     • Run all tests                │
│     • Analyze coverage             │
│     • Generate report              │
│                                    │
│  2. code_reviewer                  │
│     • Review today's commits       │
│     • Check quality & security     │
│     • Assign quality score (A-F)   │
│                                    │
│  3. release_manager                │
│     • Review open PRs              │
│     • Check test + review reports  │
│     • Auto-merge if approved       │
│                                    │
│  4. Email Report                   │
│     • Send to luca.lamal@hotmail.it
│     • Include all summaries        │
│                                    │
└────────────────────────────────────┘
```

---

## What to Expect

### Day 1 (Today - After Setup)

**Manual testing**:
- Run workflows manually to verify secrets are configured
- Check that email report arrives
- Review workflow logs for any errors

### Day 2 (First Automatic Run)

**8:00 AM UTC**:
- ✅ Daily Development runs automatically
- If backlog is empty → Creates 2 new issues
- If issues exist → Starts implementing oldest issue
- Creates sprint planning documents

**5:00 PM UTC**:
- ✅ Daily Testing & Review runs automatically
- Tests any changes from morning
- Reviews code quality
- Checks PRs for auto-merge
- Sends email report to `luca.lamal@hotmail.it`

### Week 1

**Development velocity**: 1 issue/day
- 5 working days × 1 issue = 5 issues implemented
- All tested and reviewed automatically
- PRs auto-merged if quality meets standards

**Reports generated**:
- 5 test reports
- 5 code review reports
- 5 PR management reports
- 5 email reports

### Month 1

**1st of month (8:00 AM UTC)**:
- Regulatory Agents run (existing workflow)
- Updates compliance knowledge
- Creates compliance-related issues

**Daily cycles**: ~20 working days
- ~20 issues implemented
- ~20 reports of each type
- Full audit trail in `.claude/` directories

---

## Development Workflow Integration

### For You (Manual Work)

1. **Morning**: Check email for yesterday's report
2. **During day**: Review PRs that need manual intervention
3. **Anytime**: Create issues for bugs or features (bot will implement)
4. **Weekly**: Review `.claude/reports/` for trends
5. **Monthly**: Review velocity and adjust if needed

### For the Bot (Automated)

1. **8 AM**: Plan or implement issues
2. **5 PM**: Test, review, merge
3. **Continuous**: Track everything in reports

### Collaboration

```
You                          Bot
 │                            │
 ├─ Create issue ────────────→│
 │                            ├─ Plan implementation (8 AM)
 │                            ├─ Write code
 │                            ├─ Create PR
 │                            │
 │                            ├─ Run tests (5 PM)
 │                            ├─ Review code
 │                            ├─ Auto-merge if approved
 │                            │
 │←─── Email report ──────────┤
 │                            │
 ├─ Review merged PR          │
 ├─ Test in production        │
 │                            │
 └─ Repeat ──────────────────→│
```

---

## Monitoring & Maintenance

### Daily

**Check email reports** (`luca.lamal@hotmail.it`):
- Review test results
- Check code quality scores
- Monitor PR merge decisions

**Optional deep dive**:
```bash
# View today's reports
cat .claude/reports/*-$(date +%Y-%m-%d).md

# Check workflow status
gh run list --limit 5
```

### Weekly

**Review trends**:
```bash
# Test pass rate trend
grep "tests passed\|tests failed" .claude/reports/test-report-*.md

# Code quality trend
grep "Quality Score" .claude/reports/code-review-*.md

# Issues implemented
ls .claude/sprint/issue-*-plan.md | wc -l
```

### Monthly

**Velocity analysis**:
- Issues implemented: `ls .claude/sprint/issue-*.md | wc -l`
- Issues created: `cat .claude/sprint/daily-*-issues-created.md`
- Coverage trend: Review test reports

**Cost monitoring**:
- Anthropic console: Check token usage
- SendGrid dashboard: Verify emails sent
- Adjust if costs exceed budget

---

## Customization

### Change Development Velocity

Edit `.github/workflows/daily-development.yml`:

```yaml
# Current: 1 issue/day
# To change: Modify the Implement Feature step to loop
# Or remove the "1 issue limit" logic
```

### Change Schedule

Edit cron expressions:

```yaml
# 8 AM UTC → 7 AM UTC
- cron: '0 7 * * *'

# 5 PM UTC → 6 PM UTC
- cron: '0 18 * * *'
```

Use https://crontab.guru/ to test cron expressions.

### Add Email Recipients

Edit `.github/workflows/daily-testing-and-review.yml`:

```yaml
"to": [
  {"email": "luca.lamal@hotmail.it", "name": "Luca La Malfa"},
  {"email": "team@casazen.app", "name": "CasaZen Team"}
]
```

### Adjust Quality Thresholds

Modify agent prompts in workflows:

- Current auto-merge criteria: Quality B+ and all tests pass
- To require higher quality: Change prompt to "quality A"
- To be more lenient: Change to "quality C or better"

---

## Troubleshooting

### Workflows Not Running

**Check**:
1. Repository is active (push within 60 days)
2. GitHub Actions enabled (Settings → Actions)
3. Cron syntax is correct (use https://crontab.guru/)
4. Check Actions tab for errors

**Fix**:
```bash
# Trigger manually to test
gh workflow run daily-development.yml
gh workflow run daily-testing-and-review.yml
```

### No Email Received

**Check**:
1. SENDGRID_API_KEY secret is set: `gh secret list`
2. Sender verified in SendGrid dashboard
3. Spam folder in luca.lamal@hotmail.it
4. Workflow logs for API errors

**Fix**:
- Re-verify sender in SendGrid
- Regenerate API key if needed
- Check SendGrid Activity Feed

### Tests Failing

**Expected behavior**: Bot does NOT auto-merge if tests fail

**Action**:
1. Review test report: `.claude/reports/test-report-{date}.md`
2. Fix failing tests manually
3. Next day's cycle will re-test

### PRs Not Auto-Merging

**Reasons** (by design):
- Code quality below B
- Tests failing
- CI/CD checks not passing

**Action**:
1. Review code review report
2. Address quality issues
3. Manually merge if override needed

---

## Next Steps

### Immediate (Today)

- [ ] Configure secrets (ANTHROPIC_API_KEY, SENDGRID_API_KEY)
- [ ] Test workflows manually
- [ ] Verify email report arrives
- [ ] Review workflow logs

### Short-term (This Week)

- [ ] Monitor first automatic runs
- [ ] Review generated reports
- [ ] Adjust configuration if needed
- [ ] Create some issues for bot to implement

### Long-term (This Month)

- [ ] Analyze development velocity
- [ ] Review cost vs. budget
- [ ] Optimize agent prompts if needed
- [ ] Consider increasing velocity (2+ issues/day)

---

## Documentation

**Main documentation**:
- `.claude/ORCHESTRATORS.md` - Complete orchestrator guide
- `.github/SECRETS_SETUP.md` - Secrets configuration
- `.claude/reports/README.md` - Reports directory
- `.claude/sprint/README.md` - Sprint planning directory

**Project documentation**:
- `CLAUDE.md` - Project guidelines and instructions
- `README.md` - Main project README
- `.claude/config/project.json` - Technical configuration

**Workflow files**:
- `.github/workflows/daily-development.yml` - Morning orchestrator
- `.github/workflows/daily-testing-and-review.yml` - Evening orchestrator
- `.github/workflows/regulatory-agents.yml` - Monthly compliance (existing)

---

## Support

**Questions or issues?**

1. Check documentation (above)
2. Review workflow logs: `gh run view --log`
3. Check email reports for details
4. Create GitHub issue with label `orchestrator`
5. Email: luca.lamal@hotmail.it

---

## Success Metrics

After 1 month, you should see:

- ✅ ~20 issues implemented automatically
- ✅ ~20 test reports with coverage metrics
- ✅ ~20 code reviews with quality scores
- ✅ ~20 PRs managed (merged or flagged)
- ✅ ~20 email reports received
- ✅ Full audit trail in `.claude/` directories
- ✅ Continuous integration without manual intervention
- ✅ Improved code quality (tracked over time)

---

**Setup Date**: 2026-03-30
**Status**: ✅ Complete - Ready to activate
**Next Action**: Configure secrets in GitHub

🤖 **Happy Automating!**
