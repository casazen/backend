# Runbook — Golden Journey Fase 0 (steps 1–4)

**Epic:** #286  
**Environment:** Railway staging (`develop` deploy) + Vercel staging FE  
**Prerequisite:** Pre-F0 fixes #282–#285 merged (#152 FE)

## Purpose

Manual acceptance run for Fase 0 exit criterion: GJ steps 1–4 pass once without HTTP 500 or UI blockers. Steps 5–12 deferred to Fase 1.

## Preconditions

- [ ] Staging API health: `GET /api/health` → 200
- [ ] Auth0 staging tenant configured
- [ ] Test admin account with `admin` role
- [ ] Clean test emails: `gj-f0-{date}@mailinator.com` pattern

## Step 1 — Supplier creation

**Actor:** Admin or supplier self-signup

1. Admin: `/admin/users` → invite supplier OR supplier signs up via Auth0.
2. Verify supplier user exists with `supplier` role intent.
3. **Pass:** No 500; user visible in admin list.

| Check | Expected |
|---|---|
| API | No 500 on user create/invite |
| UI | Supplier can log in |

## Step 2 — Supplier wizard → Active

**Actor:** Supplier

1. Log in as supplier → complete activation wizard (profile, services, zone).
2. Admin approves if `Pending` workflow applies.
3. **Pass:** Supplier status `Active`.

| Check | Expected |
|---|---|
| API | `GET /api/me/contexts` includes supplier org |
| UI | Inbox accessible without error |

## Step 3 — Host property + site

**Actor:** Host

1. Complete host onboarding (short-rent) → org created (#285 fix verified).
2. Create property via wizard → `Active`.
3. Open public site preview `/book/{slug}`.
4. **Pass:** Property visible; site loads (no Vercel placeholder).

| Check | Expected |
|---|---|
| API | `GET /api/properties` 200 with org context |
| API | `GET /api/public/orgs/{slug}` 200 |
| UI | Calendar page loads without infinite loop (#282) |
| Billing | Plan page no 404 before org (#283) |

## Step 4 — Guest direct booking

**Actor:** Guest (incognito)

1. Open `/book/{slug}` on staging/prod URL.
2. Select dates → checkout → complete Stripe test payment (or demo mode).
3. **Pass:** Booking `Confirmed`; confirmation visible.

| Check | Expected |
|---|---|
| API | Checkout session creates booking |
| UI | No 500 storm on page load (#284) |
| FE smoke | `#root` present; booking widget interactive |

## Automated baseline

Run before/after manual run:

```bash
# Frontend repo — demo mode
npm run test:e2e -- golden-journey-web
npm run test:e2e -- calendar-property-guard
npm run test:e2e -- api-regression-smoke   # E2E_STAGING=1 on staging
```

## Failure logging

For each failure, file issue with:

- Step number
- URL + actor role
- Network tab: first failing API call (status + response)
- Screenshot

Tag `mvp-f0` and link to #286.

## Sign-off

| Role | Date | Steps 1–4 |
|---|---|---|
| Dev | | ☐ |
| PO | | ☐ |
