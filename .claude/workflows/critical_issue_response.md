# Workflow: Critical Issue Response

## Overview
Emergency response workflow for critical issues (P0/P1) affecting production systems. Optimized for speed while maintaining safety.

**SLA**:
- **P0 (Critical)**: Response < 15 min, Resolution < 4 hours
- **P1 (High)**: Response < 1 hour, Resolution < 24 hours

---

## Critical Issue Definition

### P0 - CRITICAL
- Security vulnerabilities, data breaches
- Production system completely down
- Data loss or corruption risk
- Payment processing failures
- Regulatory compliance violations

### P1 - HIGH
- Major feature broken for many users
- Severe performance degradation (> 5x slower)
- External integration failures (OTA sync, Stripe)
- Data integrity issues

---

## Workflow Steps

### Step 1: Detection & Assessment (< 5 min)

**How detected**:
- User report
- Monitoring alert
- Error spike in logs
- Failed health check

**Immediate assessment**:
```
1. What is broken?
2. When did it start?
3. How many users affected?
4. Backend, frontend, or both?
5. Can we reproduce it?
```

**Severity classification**:
- If P0/P1 → Continue with this workflow
- If P2/P3 → Use normal issue_planner workflow

---

### Step 2: Incident Declaration (< 10 min)

#### Single Repository Issue
**Agent**: `issue_planner` (global) with P0 priority

```bash
# Create P0 issue
gh issue create \
  --title "🚨 [P0] [Brief description]" \
  --label "critical,P0" \
  --assignee "@me"
```

#### Cross-Repository Issue
**Agent**: `scrum_master` (project-specific)

```bash
# Invoke scrum_master for cross-repo critical
Task: Handle critical issue affecting both repos
Description: [issue details]
Severity: P0
```

**Create incident doc**: `.claude/incidents/INC-YYYY-MM-DD-HH-MM.md`
```markdown
# 🚨 INCIDENT: [Brief Description]

**Severity**: P0
**Detected**: YYYY-MM-DD HH:MM
**Affected**: Backend|Frontend|Both
**Status**: Investigating

## Problem
[Clear description]

## Impact
Users affected: [number/percentage]
Business impact: [revenue, reputation, legal]

## Timeline
- HH:MM: First occurrence
- HH:MM: Detected
- HH:MM: Investigation started

## Investigation
[Notes]
```

---

### Step 3: Immediate Containment (< 15 min)

**Priority**: Stop the bleeding first

#### Option A: Rollback (Fastest)
If caused by recent deployment:

```bash
# Backend rollback
git revert [bad-commit-hash]
git push origin main
# Trigger deployment

# Frontend rollback
gh workflow run rollback.yml --repo casazen/frontend -f version=previous

# Verify
curl https://api.casazen.app/health
curl https://casazen.app/health
```

#### Option B: Disable Feature
If feature flag exists:

```bash
# Disable problematic feature
# Update feature flag in admin panel or config
```

#### Option C: Workaround
If quick workaround available:

```bash
# Implement temporary fix
# (e.g., bypass broken OTA sync, use fallback payment method)
```

**Notify stakeholders**:
```
🚨 Critical incident declared: [description]
Severity: P0
Status: Contained via [rollback/disable/workaround]
Investigating root cause...

ETA for permanent fix: [estimate]
```

---

### Step 4: Root Cause Analysis (< 1 hour for P0)

**Agent**: `feature_developer` (global) or `scrum_master` (if both repos)

**Investigation checklist**:
- [ ] Check recent commits: `git log --oneline -20`
- [ ] Check logs: `kubectl logs` or application logs
- [ ] Check database: Look for corrupt data, locks
- [ ] Check external services: OTA APIs, Stripe, Auth0 status
- [ ] Check monitoring: Error rates, response times
- [ ] Reproduce locally if possible

**Common root causes**:
- Null reference exceptions
- Race conditions
- External API failures
- Database deadlocks
- Configuration errors
- Memory leaks
- Resource exhaustion

**Document findings** in incident doc.

---

### Step 5: Permanent Fix Implementation (< 2 hours for P0)

**Agent**: `feature_developer` (global)

#### Create Hotfix Branch
```bash
# Backend
git checkout main && git pull
git checkout -b hotfix/INC-YYYY-MM-DD-description

# Frontend (if needed)
gh repo clone casazen/frontend frontend-hotfix
cd frontend-hotfix
git checkout -b hotfix/INC-YYYY-MM-DD-description
```

#### Implement Fix
**Principles**:
- Minimal changes (fix ONLY the critical issue)
- No refactoring, no "while we're here" changes
- Add test that reproduces the bug
- Verify fix locally

#### Create Hotfix PR
```bash
git add .
git commit -m "fix: [brief description] (INC-YYYY-MM-DD-HH-MM)"
git push origin hotfix/INC-YYYY-MM-DD-description

gh pr create \
  --title "🚨 HOTFIX: [Brief Description]" \
  --label "critical,hotfix,P0" \
  --body "**Incident**: INC-YYYY-MM-DD-HH-MM

## Problem
[What was broken]

## Root Cause
[Why it happened]

## Fix
[What changed]

## Testing
[How verified]

## Rollback Plan
[If this fails, how to rollback]

**Requires immediate review and merge**"
```

---

### Step 6: Fast-Track Review (< 30 min for P0)

**Agent**: `code_reviewer` (global)

**Expedited review process**:
- 1 approver sufficient (normally 2)
- Focus on: Does it fix the bug? Is it safe?
- Skip: Nitpicks, style issues, refactoring suggestions

```bash
gh pr review [PR-NUM] --approve --body "LGTM - critical fix verified"
```

---

### Step 7: Deployment (< 30 min)

**Agent**: `release_manager` (global) or `scrum_master` (if both repos)

#### Deploy Backend
```bash
# Merge PR
gh pr merge [PR-NUM] --squash --delete-branch

# Deploy to production
gh workflow run deploy-production.yml --ref main

# Verify
curl https://api.casazen.app/health
# Test the specific fix
```

#### Deploy Frontend (if needed)
```bash
# Same process for frontend
gh workflow run deploy-production.yml --repo casazen/frontend --ref main

# Verify
curl https://casazen.app/health
```

#### Monitor Closely
Watch for 30-60 minutes:
- Error rates
- Response times
- User reports
- Logs

---

### Step 8: Verification & Resolution (< 1 hour)

**Verification checklist**:
- [ ] Issue no longer reproduces
- [ ] All tests pass
- [ ] No new errors in logs
- [ ] Performance metrics normal
- [ ] Users confirm fix (if reported by users)

**Update incident doc**:
```markdown
## Status: ✅ RESOLVED

**Resolved**: YYYY-MM-DD HH:MM
**Resolution Time**: X hours Y minutes

## Root Cause
[Detailed explanation]

## Fix Deployed
- Backend: PR #XXX
- Frontend: PR #YYY (if applicable)

## Verification
- [How verified]
```

**Notify stakeholders**:
```
✅ Incident RESOLVED: [description]

Root cause: [explanation]
Fix deployed: [time]
Verification: Complete

Resolution time: X hours

Post-mortem: [link]
```

---

### Step 9: Post-Incident Review (< 1 week)

**Agent**: `doc_writer` (global) or manual

**Create post-mortem**: `.claude/post-mortems/INC-YYYY-MM-DD-HH-MM.md`

```markdown
# Post-Mortem: [Incident Description]

## Summary
- Date: YYYY-MM-DD
- Duration: X hours
- Severity: P0
- Impact: [users affected, business impact]

## Timeline
| Time | Event |
|------|-------|
| HH:MM | First occurrence |
| HH:MM | Detected |
| HH:MM | Contained (rollback/workaround) |
| HH:MM | Root cause identified |
| HH:MM | Fix deployed |
| HH:MM | Verified resolved |

## Root Cause
[Detailed technical explanation]

## What Went Well
- Fast detection (monitoring alert)
- Quick containment (rollback)
- Effective communication

## What Went Poorly
- Missing test coverage for this scenario
- No monitoring for this specific error
- Deployment went out without sufficient testing

## Action Items
- [ ] Add test coverage for [scenario] - @developer - Due: [date]
- [ ] Add monitoring/alert for [metric] - @devops - Due: [date]
- [ ] Update deployment checklist - @release-manager - Due: [date]
- [ ] Update runbook with this scenario - @doc-writer - Due: [date]

## Prevention
To prevent similar incidents:
1. [Preventive measure 1]
2. [Preventive measure 2]
```

**Create prevention tickets**:
```bash
# Create GitHub issues for action items
gh issue create \
  --title "[Prevention] Add monitoring for [scenario]" \
  --label "prevention,post-incident" \
  --body "Following INC-YYYY-MM-DD-HH-MM..."
```

---

## Critical Issue Checklist

### Detection Phase
- [ ] Issue detected and severity assessed
- [ ] Incident document created
- [ ] Stakeholders notified

### Containment Phase
- [ ] Immediate containment (rollback/disable/workaround)
- [ ] Users notified of workaround
- [ ] System stable

### Investigation Phase
- [ ] Root cause identified
- [ ] Documented in incident doc

### Fix Phase
- [ ] Hotfix branch created
- [ ] Fix implemented and tested
- [ ] Hotfix PR created
- [ ] Fast-track review completed
- [ ] PR merged

### Deployment Phase
- [ ] Deployed to production (backend)
- [ ] Deployed to production (frontend, if needed)
- [ ] Smoke tests passed
- [ ] Monitoring for 30-60 minutes

### Resolution Phase
- [ ] Issue verified resolved
- [ ] No new issues introduced
- [ ] Stakeholders notified
- [ ] Incident doc updated

### Post-Incident Phase
- [ ] Post-mortem created
- [ ] Action items created
- [ ] Prevention measures planned

---

## Example: Payment Processing Failure (P0)

**Timeline**:
```
14:30 - Monitoring alert: Stripe payments failing
14:32 - Incident declared (P0)
14:35 - Rollback deployed (to pre-Stripe-update version)
14:40 - Payments working again (workaround active)
14:45 - Root cause: Stripe API version mismatch
15:15 - Hotfix PR ready (updated Stripe SDK)
15:30 - PR reviewed and merged
15:45 - Hotfix deployed to production
16:00 - Verified: Payments working with correct Stripe version
16:15 - Incident resolved

Resolution time: 1 hour 45 minutes (within P0 SLA)

Post-incident:
- Added Stripe API version monitoring
- Updated Stripe integration tests
- Documented Stripe upgrade procedure
```

---

## Agent Coordination

### Single-Repo Critical
```
issue_planner (P0) → feature_developer (hotfix) →
code_reviewer (fast-track) → release_manager (deploy)
```

### Cross-Repo Critical
```
scrum_master (coordinate) → feature_developer (both repos) →
code_reviewer (both repos) → scrum_master (coordinated deploy)
```

---

## Related Documents
- `.claude/agents/scrum_master.md` - Cross-repo coordination
- `feature_implementation.md` - Normal feature workflow
- `CLAUDE.md` - Project guidelines
