# AC Matrix — MVP Phase 1

**Updated:** 2026-08-13 (tick 10: Direct checkout L3 booking create → blocked; no FE write / no L3 Playwright)  
**Baseline:** Dev Flow Verification 2026-07-14 + quality-gates overhaul  
**Statuses:** `pass` | `fail` | `stub` | `missing-test` | `in-progress` | `blocked`

P0 rows with `fail` trigger [freeze-policy.md](./freeze-policy.md).

## Stub inventory (excluded from production claims)

| Component | Path | Action |
|---|---|---|
| Booking.com OTA adapter | `Casazen.Infrastructure/OTA/BookingComAdapter.cs` | `status:stub` — real API TODO |
| OTA ChannelFactory gaps | `Casazen.Infrastructure/OTA/ChannelFactory.cs` | `status:stub` — NotImplemented paths |
| Lease e-sign adapter | `Casazen.Infrastructure/External/LeaseESignHttpAdapter.cs` | `status:stub` — provider TBD |
| Null rent billing | `Casazen.Infrastructure/Services/NullRentBillingService.cs` | `status:stub` — LTR rent deferred |
| PaymentService TODO | `Casazen.Infrastructure/Services/PaymentService.cs` | `status:stub` — partial TODO |
| Mobile Expo push | `mobile/app.json` placeholder `eas.projectId` | `status:stub` until `eas init` |

## Golden Journey (GJ-001) — P0

| AC | Description | L2 | L3 | Status | Notes |
|---|---|---|---|---|---|
| AC1–AC5 | Web steps harness | `e2e/golden-journey-web.spec.ts` | `e2e/l3/*` | `blocked` | Full 1–12 L2/L3 cannot land from this Automation — no write access to `casazen/frontend` (push 403); L3 also needs `E2E_AUTH0_*`. FE `golden-journey-web.spec.ts` only covers demo steps 3–4 today; unblock when FE push + (optional) Auth0 secrets available |
| AC6–AC12 | Host app M1–M7 | `mobile/e2e/m*.yaml` | same + staging seed | `blocked` | `casazen/mobile` repo missing; Maestro CLI/device unavailable in Automation — unblock when mobile repo + device exist |
| AC13 | Supplier mobile F1–F2 | — | — | `missing-test` | Spec pending suite |
| AC14–AC15 | Parity + CI | CI e2e.yml L2 | staging-gj project | `in-progress` | L2 CI restored; staging needs secrets |

## Native host app (US-025 / #299) — P0

| AC | Status | Notes |
|---|---|---|
| AC1 Scaffold | `pass` | Expo project exists |
| AC2 Auth0 PKCE | `pass` | expo-auth-session |
| AC3 API client | `pass` | Axios + JWT |
| AC4 Calendar month/week | `blocked` | Needs Expo calendar month/week UI in `casazen/mobile` — repo missing (404) and no `mobile/` tree in this Automation; matrix note historically FlatList-only. Unblock when mobile repo is available and calendar grid ships |
| AC5 Booking detail | `pass` | Screen present |
| AC6 Service request | `pass` | Screen + API |
| AC7 Mark paid | `pass` | Mutation present |
| AC8 Checkout | `pass` | Wizard start screen |
| AC9 Properties | `pass` | Read-only list |
| AC10–AC12 Push | `stub` | Placeholder EAS projectId |
| AC13–AC14 Parity/offline | `pass` | React Query + OfflineBanner |
| AC15 Maestro 0 crash | `blocked` | `casazen/mobile` repo missing (404); Maestro CLI/device unavailable in Automation — cannot prove 0-crash on device; unblock when mobile repo + device/Maestro exist |
| AC19 typecheck | `fail` → fixed 2026-07-26 | Was missing `shouldShowBanner`/`shouldShowList` (SDK 54); re-run `npm run typecheck` |
| AC20 Maestro M1–M7 | `blocked` | `casazen/mobile` repo missing; Maestro CLI/device unavailable in Automation — structural smoke alone cannot satisfy device M1–M7; unblock when mobile repo + device exist |
| AC21 BE push tests | `pass` | BE suite: `PushNotificationServiceTests` (guest check-in, service-request, checkout reminder + routing) + `DeviceRegistrationIntegrationTests` (`POST/DELETE /api/devices`); closed delivery tick 8 |

## Micro-marketplace (#293) — P0

| AC | Status | Notes |
|---|---|---|
| Host create ServiceRequest | `pass` | L2 marketplace-suppliers |
| Supplier take/complete | `blocked` | FE inbox L2 take/complete Playwright cannot land — Automation has no write access to `casazen/frontend` (403); L3 also needs `E2E_AUTH0_*`. BE `ServiceRequestServiceTests` + `CompleteFlow_TakeCompleteMarkPaid_Succeeds` already cover API state machine; unblock when FE push + (optional) Auth0 secrets available |
| Mark paid | `pass` | Host path |
| L3 real API loop | `blocked` | Needs FE Playwright L3 + `E2E_AUTH0_*` in Automation; BE `CompleteFlow_TakeCompleteMarkPaid_Succeeds` already covers API loop |

## Compliance wizards (#295) — P0

| AC | Status | Notes |
|---|---|---|
| Summary widget on dashboard | `pass` | Wired 2026-07-26 |
| Activation route + CTA | `pass` | Route + edit CTA added |
| Checkout wizard route | `pass` | `/bookings/:id/checkout` added |
| L2 compliance-wizards | `in-progress` | Should pass after route wiring |

## Direct checkout / branded booking — P0

| AC | Status | Notes |
|---|---|---|
| Public property amenities guard | `pass` | `?? []` |
| Guest tax line + pay CTA | `pass` | Estimate + payNow i18n |
| Cookie consent | `in-progress` | Verify L2 branded-booking-site |
| L3 booking create | `blocked` | FE L3 Playwright (`e2e/l3/*direct-checkout*`) missing; Automation cannot push to `casazen/frontend` (403). BE `DirectCheckoutIntegrationTests` already seeds Connect-ready property + creates booking, but matrix L3 is FE/staging Playwright. Unblock when FE write + L3 spec + seeded public property available |

## Long-rent — P1

| AC | Status | Notes |
|---|---|---|
| Post-create navigate canonical | `pass` | `/app/long-rent/leases/:id` |
| Lease create E2E | `missing-test` | |

## Admin / supplier — P0/P1

| AC | Status | Notes |
|---|---|---|
| Admin invite E2E | `pass` | #357 |
| Supplier legacy redirect | `pass` | Preserves sub-path |
| Supplier inbox L3 | `missing-test` | |

## Platform / other Phase-1

| Spec | Priority | Status | Notes |
|---|---|---|---|
| tenant-boundary | P0 | `pass` | Shipped + ops |
| role-onboarding | P0 | `pass` | |
| connect-onboarding | P0 | `pass` | |
| saas-billing | P0 | `in-progress` | Partial; FE ACs deferred historically |
| custom-domain | P0 | `pass` | Wave5 |
| ical-calendar-sync | P0 | `pass` | |
| guest-check-in-portal | P0 | `pass` | L2 guest-checkin-portal |
| public-site-design-system | P1 | `pass` | |
| seo-funnel | P2 | `pass` | |
| Booking.com OTA | P2 | `stub` | |
| e-sign leases | P2 | `stub` | |
| native-supplier-app | P2 | `stub` | Fase 2 |

## Remediation backlog (ordered)

1. Green Maestro M1–M7 + document push as stub until EAS
2. Full GJ web 1–12 L2 + L3 staging
3. Supplier inbox L3 state machine
4. Calendar month/week UI (mobile AC4)
5. Clear remaining `fail` / `missing-test` P0 rows
