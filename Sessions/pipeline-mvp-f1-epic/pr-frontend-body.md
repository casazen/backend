## Summary

- `/supplier/*` route group with `ProtectedRoute role="Supplier"` and `SupplierShell`
- Activation wizard (5 steps, Italian), inbox/profile/availability pages
- Admin invite page at `/app/admin/suppliers/invite`
- GJ E2E steps 1–2 + mobile F1 inbox smoke with demo mocks

## Backend PR

https://github.com/casazen/backend/pull/312

## Acceptance criteria coverage

| AC | Test |
|---|---|
| AC10 SupplierShell routes | `golden-journey-web.spec.ts` inbox smoke |
| AC11 activation wizard | `GJ steps 1–2: supplier activation` |
| AC12 inbox mobile | `GJ supplier inbox mobile viewport F1 smoke` |
| AC13 availability UI | manual / Wave 2 E2E extension |

## Gate status

| Gate | Status |
|---|---|
| G5 npm test | ✅ 176 passed |
| G6 tsc | ✅ |
| G7 lint | ⚠️ pre-existing repo errors; no new supplier lint errors |
| G8 build | ✅ |
| G9 E2E | ✅ 4 passed (3 fixme) |

## Test plan

- [ ] `npm run test:e2e -- golden-journey-web`
- [ ] Demo: `?demoProfile=supplier` → `/supplier/activation` → complete → inbox
- [ ] Admin: `?demoProfile=admin` → `/app/admin/suppliers/invite`

Closes casazen/backend#292
