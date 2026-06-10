## Summary

- Auto-provision a **Starter** org when authenticated users have roles but `OrgId` is null (#217)
- Backfill empty email/name on `GET /api/users/me` from JWT claims (#219)
- Add E2E flow verification spec for production Chrome testing

## Test plan

- [x] `dotnet test` — UserServiceTests + PropertiesControllerTests (59 passed)
- [ ] Deploy to Railway test → create property as `luca.lamal@hotmail.it` returns 201
- [ ] `PUT /api/orgs/me/plan` succeeds after auto-provision
- [ ] Admin users table shows email after next login

Fixes #217
