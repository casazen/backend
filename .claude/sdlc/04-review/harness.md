# Stage 04: Review — Quality Harness

## Entry Criteria

- Feature PR(s) open targeting `develop` (`pr_backend`, `pr_frontend` — either may be N/A)
- All Stage 03 gates passed
- `Sessions/design-<issue-N>.md` available for spec comparison

## Council Run

Coordinator spawns: `code-reviewer`, `security-auditor`

Topic handed to council:
> "Review PR #P for Issue #N. Check for critical code quality issues, OWASP Top 10 violations, and CasaZen compliance requirements. Produce a findings list with severity ratings."

## Quality Gates

All gates must pass before exit.

### Review gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | PR(s) mergeable | `gh pr view --json mergeable` per repo | `MERGEABLE` (or N/A) |
| G2 | No critical findings | Read `Sessions/review-<N>.md` | 0 open 🔴 issues |
| G3 | High findings addressed | Read review output | All 🟡 resolved or deferred with issue |
| G4 | Cross-repo consistency | Read review output | FE API calls match BE contract in design spec |

### Security gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G5 | No IDOR vulnerabilities | Read modified controller code | Property/booking/guest endpoints verify `OwnerId == auth-sub` or `UserId == auth-sub` |
| G6 | No raw SQL | `grep -rn "FromSqlRaw\|ExecuteSqlRaw" Casazen.Infrastructure` | 0 string-concatenated SQL (parameterized SQL is OK) |
| G7 | PII not exposed | Read error handling + logging code | Guest `DocumentNumber`, `DateOfBirth`, `Nationality` absent from error responses and log messages |
| G8 | Stripe signature verified | Read `StripeWebhookHandler.cs` if modified | Signature check present and not bypassed |

### Compliance gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G9 | GDPR fields populated | Read Guest-touching code in PR diff | `ErasureRequested` flag + `DataRetentionUntil` set in new guest creation flows |
| G10 | Frontend auth routes | Read modified React routes in PR diff | All new authenticated pages wrapped in `<ProtectedRoute>` |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G10 fails) AND (iteration < max_iterations):
  1. Coordinator compiles findings by severity (🔴 critical → 🟡 high → 🟢 medium)
  2. Post findings as PR review comments (gh pr review --comment)
  3. Developer (Stage 03 team) addresses critical and high findings
  4. Re-request review: coordinator re-checks only previously failing gates
  5. iteration++

IF iteration == max_iterations AND critical findings remain:
  ESCALATE: close PR with escalation note, create separate issue for root cause
  Human decision required
```

## Exit Artifact

`Sessions/review-<issue-N>.md` covering backend + frontend PRs:
- 0 open 🔴 critical findings
- Cross-repo review summary posted on each PR

## Handoff to Stage 05

Pass `pr_backend`, `pr_frontend`, issue `#N`, and design spec. Stage 05 merges to `develop`, validates on staging, then promotes to `main`.
