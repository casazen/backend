## Summary
Promote develop → main for v1.1.10.

### Features
- Stripe Connect Express onboarding (Issue #224 / #222)
- Connect API: account create, onboarding link, status
- Connect webhook route (RF2) + Org capability fields migration
- Booking DTO hardening (CreateBookingRequest)

### Staging validation (Phase B)
- Railway test health: 200
- dotnet test: 476 passed
- FE E2E: 60 passed (develop CI green)
- Migration applied to test + prod (`AddConnectStatusFields`)

Closes #224
