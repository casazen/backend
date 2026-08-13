# Stage 04: Review — Quality Harness

## Entry Criteria

- Feature PR(s) open targeting `develop` (`pr_backend`, `pr_frontend`, optional `pr_mobile`)
- All Stage 03 gates passed (including G9a L2, G9b L3, G9c anti-stub)
- `Sessions/design-<issue-N>.md` available with `## AC Test Map`

## Council Run

Coordinator spawns: `code-reviewer`, `security-auditor`

Topic handed to council:
> "Review PR(s) for Issue #N. Verify AC matrix completeness (L1/L2/L3), no shipped stubs, OWASP/CasaZen compliance. Produce Sessions/review-<N>.md."

## Quality Gates

### Review gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | PR(s) mergeable | `gh pr view --json mergeable` per repo | `MERGEABLE` (or N/A) |
| G2 | No critical findings | `Sessions/review-<N>.md` **plus** gate-runner evidence | 0 open 🔴 issues; markdown alone is insufficient |
| G3 | High findings addressed | Review output + linked fix commits | All 🟡 resolved or deferred with issue |
| G4 | Cross-repo consistency | Skill `sdlc-contract-check` evidence (`contract-check.md`) | FE/mobile API calls match BE contract; overall PASS |

### Security gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G5 | No IDOR vulnerabilities | Read modified controller code | Org/user boundary enforced |
| G6 | No raw SQL | `grep -rn "FromSqlRaw\|ExecuteSqlRaw" Casazen.Infrastructure` | 0 string-concatenated SQL |
| G7 | PII not exposed | Read error handling + logging | Guest sensitive fields absent from errors/logs |
| G8 | Stripe signature verified | Read `StripeWebhookHandler.cs` if modified | Signature check present |

### Compliance + completeness gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G9 | GDPR fields populated | Read Guest-touching code in PR diff | Flags set when Guest created |
| G10 | Frontend auth routes | Read modified React/Expo routes | ProtectedRoute / session gate present |
| G11 | AC matrix complete | PR body + design AC Test Map + **Stage 03 evidence** (L2+L3 exit codes) | 0 AC marked PASS without evidence paths; UI ACs require L3/Maestro evidence; stubs only if `status:stub` |
| G12 | Anti-stub on diff | `.\scripts\quality\check-no-shipped-stubs.ps1` | Exit 0 |
| G13 | Evidence-only PASS | `Sessions/loop/evidence/...` or `Sessions/pipeline-<slug>/evidence/` for Stage 03/04 | Stage 04 cannot approve on narrative tables alone |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G13 fails) AND (iteration < max_iterations):
  1. Coordinator compiles findings by severity
  2. Post findings as PR review comments
  3. Stage 03 team addresses critical/high + AC gaps
  4. Re-check failing gates via **sdlc-gate-runner**
  5. iteration++

IF iteration == max_iterations AND critical or G11 failures remain:
  ESCALATE via sdlc-escalate — do not approve merge
```

## Exit Artifact

`Sessions/review-<issue-N>.md` with:
- 0 open 🔴 critical findings
- **AC matrix table** (AC → evidence → PASS/FAIL)
- Cross-repo summary on each PR

**Issue `#N` remains open.**

## Handoff to Stage 05

Pass PR numbers, issue `#N`, design spec, and AC matrix status.
