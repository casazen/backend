## Summary

- Add `OrgType.Supplier`, `SupplierProfile`, `SupplierAvailability`, `SupplierInviteRecord` + EF migration `AddSupplierOrgAndProfile`
- New API: register, activation wizard, profile CRUD, inbox shell, availability, admin invite, host supplier picker
- Auth policy `RequireSupplier`; 14 integration tests
- Epic design spec `Sessions/design-291.md` (8-wave plan; this PR is Wave 1)

## Frontend PR

https://github.com/casazen/backend/pull/312

## Acceptance criteria coverage

| AC | Test |
|---|---|
| AC1–AC6 identity/activation | `SupplierConsoleIntegrationTests` |
| AC7 inbox shell | `GetInbox_ReturnsEmptyList` |
| AC8 availability | `UpdateAvailability_Returns200` |
| AC3 admin invite | `AdminInvite_ValidRequest_Returns201` |

## Gate status

| Gate | Status |
|---|---|
| G1 dotnet test | ✅ 585 passed |
| G2 format | ✅ |
| G3 build | ✅ |
| G4 migration | ✅ `AddSupplierOrgAndProfile` |
| G10–G13 | N/A |

## Test plan

- [ ] `dotnet test --filter SupplierConsole`
- [ ] `.\scripts\migrate.ps1 -Target test` on staging before merge
- [ ] Register supplier via `POST /api/suppliers/register`
- [ ] Complete activation → `Status=Active`
- [ ] Admin invite via `POST /api/admin/suppliers/invite`

Closes #292
