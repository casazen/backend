## Summary

Admin **Gestione Utenti** table and role-change dialog show **empty user identity** (no email, no name).

## Reproduction

1. Login as admin → **Amministrazione** → **Utenti**
2. Table shows role "Admin" but email column is "—" for all users
3. Click **Ruolo** → dialog text: "Seleziona il nuovo ruolo per ." (missing name)

## API evidence

`GET /api/users?page=1&pageSize=20` returns:
```json
{"email":"","firstName":"","lastName":"","role":"Admin","orgId":null}
```

Auth0 profile has `luca.lamal@hotmail.it` but DB user record has empty email.

## Impact

- Admin cannot identify which user they are modifying
- Risk of changing wrong account in multi-user environments

## Suggested fix

1. **BE**: Sync email/name from JWT claims on `GetCurrentUserAsync` / login provisioning
2. **FE**: Fallback display: show `id` (truncated) or Auth0 sub when email empty
3. **Backfill**: Update existing users with email from Auth0 Management API

## Evidence

Spec: `Sessions/specs/spec-production-e2e-flow-verification.md`
Tested: 2026-06-09
