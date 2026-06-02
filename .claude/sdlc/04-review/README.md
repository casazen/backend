# Stage 04 — Review

**Pattern**: builder-validator
**Input**: Open PR from Stage 03

## Purpose

Catch critical issues before merge: security vulnerabilities, OWASP Top 10, compliance gaps, and code quality problems. No critical findings may remain open when this stage exits.

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Orchestrates review, tracks findings, clears exit | `agents/coordinator.md` |
| code-reviewer | Logic correctness, test coverage, async patterns, SOLID violations | `agents/code-reviewer.md` |
| security-auditor | IDOR, SQL injection, PII exposure, Stripe signature, GDPR, frontend auth | `agents/security-auditor.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full loop specification.

**Key gates**:
- `gh pr view --json reviews` → at least 1 approval, no requested changes
- No critical review findings open (🔴 severity)
- IDOR check: property/booking/guest endpoints verify `OwnerId == auth-sub`
- No raw SQL concatenation in `Casazen.Infrastructure/`
- Guest PII fields absent from error responses and structured logs
- Stripe `StripeWebhookHandler` signature check not bypassed
- GDPR: `ErasureRequested` + `DataRetentionUntil` present if Guest flow changed
- All new authenticated frontend routes wrapped in `<ProtectedRoute>`

## Exit Artifact

PR with:
- At least 1 approval
- All critical and high findings addressed (or formally deferred with issue created)
- Review notes resolved

## Chain

→ **Stage 05: Release** — approved PR ready to merge
