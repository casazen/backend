## Summary
- Epic #286 Fase 0 orchestration design spec and pipeline state
- ADRs: custom domain (#288), iCal sync (#289), Expo scaffold (#287)
- Public site design brief (#290) and GJ steps 1-4 manual runbook

## Frontend PR
(pending — will link after FE PR created)

## Test Plan
- [x] dotnet test (559 passed, Docker integration excluded locally)
- [x] dotnet format --verify-no-changes
- [ ] CI Build & Test

## Acceptance criteria coverage
| AC | Artifact |
|---|---|
| ADRs domain + iCal | docs/adr/ADR-001, ADR-002 |
| Expo ADR | docs/adr/ADR-003 + ../mobile scaffold (sibling, pending repo) |
| Design brief #290 | Sessions/design-public-site-brief.md |
| GJ runbook | Sessions/runbooks/golden-journey-f0-steps-1-4.md |

Closes #286
