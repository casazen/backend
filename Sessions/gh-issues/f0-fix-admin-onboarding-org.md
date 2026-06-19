## Summary

Pre-Fase 0 blocking fix. Admin users completing onboarding **skip org creation**, leaving them without host `OrgId` → 403 `no_org_context` on property create.

**Planning:** `Sessions/PLANNING.md` § Debito noto  
**Blocks:** GJ step 3, property wizard

## Acceptance criteria

- [ ] Admin choosing to operate as host triggers org creation (same as property owner path)
- [ ] Retroactive path: admin without org can create org from settings/onboarding without DB manual fix
- [ ] `POST /api/properties` succeeds after fix for admin-as-host test user
- [ ] Unit/integration test for admin host bootstrap

## Spec / deps

- Coordinates with #271 (PLG onboarding)
- Prerequisite for `spec-compliance-wizards` property activation
