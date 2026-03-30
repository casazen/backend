# GitHub Actions Secrets Setup

This document explains how to configure the required secrets for CasaZen's automated orchestrator workflows.

## Required Secrets

The orchestrator workflows require two secrets to be configured in the repository:

### 1. ANTHROPIC_API_KEY

**Purpose**: Enables Claude Code to execute AI agents for development, testing, and review tasks.

**Used by**:
- Daily Development Orchestrator
- Daily Testing & Review Orchestrator
- Regulatory Agents Workflow

**How to get it**:

1. Go to https://console.anthropic.com/
2. Sign in or create an account
3. Navigate to **API Keys** section
4. Click **Create Key**
5. Name it: `casazen-github-orchestrators`
6. Copy the API key (starts with `sk-ant-...`)

**How to add it to GitHub**:

1. Go to repository: https://github.com/{owner}/casazen/settings/secrets/actions
2. Click **New repository secret**
3. Name: `ANTHROPIC_API_KEY`
4. Value: Paste the API key
5. Click **Add secret**

### 2. SENDGRID_API_KEY

**Purpose**: Sends daily email reports to `luca.lamal@hotmail.it` with testing and review summaries.

**Used by**:
- Daily Testing & Review Orchestrator (send-email-report job)

**How to get it**:

1. Go to https://app.sendgrid.com/
2. Sign in or create account
3. Navigate to **Settings** → **API Keys**
4. Click **Create API Key**
5. Name it: `casazen-github-reports`
6. Permissions: **Full Access** (or at least **Mail Send** permission)
7. Click **Create & View**
8. Copy the API key (starts with `SG.`)
9. ⚠️ **Important**: Save this key securely - SendGrid only shows it once!

**How to add it to GitHub**:

1. Go to repository: https://github.com/{owner}/casazen/settings/secrets/actions
2. Click **New repository secret**
3. Name: `SENDGRID_API_KEY`
4. Value: Paste the API key
5. Click **Add secret**

**SendGrid Configuration**:

You also need to configure SendGrid sender:

1. In SendGrid, go to **Settings** → **Sender Authentication**
2. Verify domain: `casazen.app` (recommended) OR
3. Verify single email: `noreply@casazen.app`
4. Follow SendGrid's verification instructions

## Automatic Secrets

These secrets are automatically provided by GitHub - no setup needed:

### GITHUB_TOKEN

**Purpose**: Allows workflows to interact with GitHub API for:
- Creating/updating issues
- Managing pull requests
- Committing files
- Running `gh` CLI commands

**How it works**: GitHub automatically creates and injects this token into every workflow run.

## Verification

After adding secrets, verify they're configured correctly:

```bash
# List configured secrets (values are hidden)
gh secret list

# Expected output:
# ANTHROPIC_API_KEY    Updated YYYY-MM-DD
# SENDGRID_API_KEY     Updated YYYY-MM-DD
```

## Test Workflows

Test that secrets work correctly:

### Test Daily Development

```bash
# Trigger workflow manually
gh workflow run daily-development.yml

# Wait a few seconds, then check status
gh run list --workflow=daily-development.yml --limit 1

# View logs
gh run view --log
```

### Test Daily Testing & Review

```bash
# Trigger workflow manually
gh workflow run daily-testing-and-review.yml

# Wait for completion
gh run watch

# Check if email was received at luca.lamal@hotmail.it
```

## Troubleshooting

### ANTHROPIC_API_KEY Issues

**Error**: `401 Unauthorized` or `Invalid API key`

Solutions:
1. Verify key is correct (starts with `sk-ant-`)
2. Check key hasn't expired
3. Verify account has sufficient credits
4. Regenerate key if needed

**Error**: `429 Too Many Requests`

Solutions:
1. Check API usage limits in Anthropic console
2. Reduce workflow frequency if needed
3. Upgrade to higher tier if necessary

### SENDGRID_API_KEY Issues

**Error**: `403 Forbidden`

Solutions:
1. Verify API key has Mail Send permission
2. Check sender email is verified in SendGrid
3. Verify domain authentication (if using domain)

**Error**: Email not received

Check:
1. SendGrid Activity Feed: https://app.sendgrid.com/email_activity
2. Spam folder in luca.lamal@hotmail.it
3. Workflow logs for API error messages
4. SendGrid account status (not suspended)

## Security Best Practices

1. **Never commit secrets** to repository
   - Secrets are in `.gitignore` by default
   - Never log secret values in workflows

2. **Rotate keys regularly**
   - Rotate API keys every 90 days
   - Update secret in GitHub when rotated

3. **Minimum permissions**
   - Grant least privilege needed
   - Review API key permissions regularly

4. **Monitor usage**
   - Check Anthropic console for unusual activity
   - Monitor SendGrid activity feed

5. **Secure access**
   - Limit who has access to repository secrets
   - Use GitHub's environment protection rules if needed

## Cost Estimation

### Anthropic API

**Daily Development** (runs at 8 AM):
- Planning Mode (empty backlog): ~50K tokens
- Implementation Mode (1 issue): ~100K tokens
- **Monthly**: ~2-3M tokens ≈ $6-9/month (Claude Sonnet)

**Daily Testing & Review** (runs at 5 PM):
- Testing + Review + PR: ~30K tokens/day
- **Monthly**: ~900K tokens ≈ $2.70/month (Claude Sonnet)

**Regulatory Agents** (monthly):
- Runs 1st of month: ~100K tokens
- **Monthly**: ~100K tokens ≈ $0.30/month (Claude Sonnet)

**Total Estimated Cost**: ~$9-12/month

💡 **Tip**: Use Claude Haiku for simple tasks to reduce costs (3x cheaper)

### SendGrid

**Free Tier**: 100 emails/day free forever
- CasaZen usage: 1 email/day
- **Cost**: $0/month (well within free tier)

If you need more emails, SendGrid Essentials starts at $19.95/month.

## Support

**Questions about secrets setup?**
- GitHub Secrets Docs: https://docs.github.com/en/actions/security-guides/encrypted-secrets
- Anthropic API Docs: https://docs.anthropic.com/
- SendGrid API Docs: https://docs.sendgrid.com/

**Issues?**
- Create GitHub issue with label `orchestrator`
- Email: luca.lamal@hotmail.it
