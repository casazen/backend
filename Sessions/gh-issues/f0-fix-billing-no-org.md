## Summary

Pre-Fase 0 blocking fix. Billing/plan pages return **404** when user has no `Org` context (skipped onboarding). Pages should gate behind onboarding completion instead of hard 404.

**Planning:** `Sessions/PLANNING.md` § Debito noto  
**Blocks:** Golden Journey step 3, MVP platform hardening

## Acceptance criteria

- [ ] `/billing` and plan upgrade routes redirect to onboarding when `no_org_context`
- [ ] No infinite spinner or raw 404 for authenticated users without org
- [ ] After #271 onboarding complete, billing pages load normally
- [ ] E2E: new user without org sees guided redirect (Italian)

## Spec / deps

- Depends on / coordinates with #271
- Related: #274, #273 (billing security)
