## Summary
- `GET /api/public/resolve-host` — subdomain → org branding (#288)
- `ICalImportSpike` — Ical.Net parse/export/overlap PoC (#289)
- Unit tests + Airbnb `.ics` fixture
- Design: `Sessions/design-batch-f0.md`
- Mobile init script (#287): `scripts/init-mobile-repo.ps1`

## Frontend PR
(pending)

## Acceptance criteria coverage
| AC | Test / artifact |
|---|---|
| #288 resolve-host | `PublicHostControllerTests`, `PublicHostResolverTests` |
| #289 iCal PoC | `ICalParserSpikeTests` + `Fixtures/sample-airbnb.ics` |
| #287 Expo ADR + script | ADR-003 + `init-mobile-repo.ps1` |

## Test plan
- [x] dotnet test (570 passed)
- [x] dotnet format --verify-no-changes
- [ ] CI Build & Test

Part of epic #286
