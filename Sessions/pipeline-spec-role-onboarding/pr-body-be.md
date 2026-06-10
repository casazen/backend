## Summary
- Add `POST/PUT /api/users/onboarding` to assign Auth0 roles from rental type choice
- Add nullable `User.RentalType` enum + EF migration
- Extend `Auth0ManagementService` with multi-role onboarding sync
- Integration + unit tests (AC7–AC10)

## Test plan
- [x] `dotnet test` — 414 passed
- [x] Onboarding integration tests (401, 400, ShortTerm/LongTerm/Both, PUT idempotent)

Closes #198
