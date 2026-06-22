# Design — MVP Fase 1 Epic (#291)

Epic parent: **#291** · Child issues: **#292–#301** · Platform: **#271, #230, #273, #274**  
**Exit:** GJ 12-step + Maestro M1–M7 + supplier F1–F2 on prod; E2E green in CI  
**Planning:** `Sessions/PLANNING.md` § Fase 1 · **Child order:** `Sessions/gh-issues/comment-epic-f1.md`

---

## Scope summary

| Issue | US / ID | Layer | Deliverable |
|---|---|---|---|
| **#292** | US-022 | BE + FE | Supplier console web — identity, activation wizard, inbox shell, availability |
| **#293** | US-021 | BE + FE | `ServiceRequest` entity + host/supplier state machine (Richiesto→Pagato) |
| **#294** | US-018 | BE + FE + jobs | iCal import/export, `CalendarBlock`, Hangfire sync (15 min) |
| **#295** | US-019 | BE + FE | Property activation wizard, checkout wizard, compliance summary cockpit |
| **#296** | US-020 | BE + FE + jobs | Guest check-in portal (token link), Alloggiati auto-enqueue, reminders |
| **#297** | US-023 | FE (+ optional BE) | `PublicSiteShell`, premium template, marketing-grade `/book/{slug}` |
| **#298** | US-024 | BE + FE edge | Custom domain CNAME, DNS verify, Vercel middleware tenant injection |
| **#299** | US-025 | Mobile repo + BE | Expo host app M1–M7, push tokens, calendar/booking/service subset |
| **#300** | US-026 | BE + FE | SEO comune funnel aligned to public DS, CTA event tracking |
| **#301** | GJ-001 | FE + mobile E2E | Playwright 12-step + Maestro M1–M7 + supplier mobile F1–F2 in CI |
| **#271** | — | BE + FE | Onboarding PLG — host mode selection (path / subdomain / custom) |
| **#230** | — | BE + FE | SaaS billing / freemium entitlements (reopened) |
| **#273 / #274** | — | BE | Billing security hardening (webhook sig, plan gate enforcement) |

---

## API Contract

Legend: **Existing** = shipped or partial in codebase · **New** = Fase 1 deliverable · **Modify** = extend existing contract

### #292 — Supplier console web (US-022)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| New | POST | `/api/admin/suppliers/invite` | `[Authorize]` — role `Admin` | `{ email, comuneCode, categories[]?, message? }` | 201 `{ inviteId, expiresAt }` / 409 duplicate |
| New | POST | `/api/suppliers/register` | Public — `[AllowAnonymous]` | `{ email, password?, legalName, phone, comuneCode, inviteToken? }` | 201 `{ orgId, authRedirectUrl }` / 400 validation |
| New | GET | `/api/supplier/profile/activation` | `[Authorize]` — role `Supplier` | — | 200 `{ status, steps: [{ id, label, status, blocker? }] }` |
| New | POST | `/api/supplier/profile/activation/complete` | `[Authorize]` — role `Supplier` | `{ tosAccepted: true }` | 200 `{ status: "Active" }` / 409 blockers remain |
| New | GET | `/api/supplier/profile` | `[Authorize]` — role `Supplier` | — | 200 `SupplierProfileDto` |
| New | PUT | `/api/supplier/profile` | `[Authorize]` — role `Supplier` | `{ legalName?, vatNumber?, phone?, categories[]?, comuni[]?, bio?, photoUrls[]? }` | 200 `SupplierProfileDto` |
| New | GET | `/api/supplier/inbox` | `[Authorize]` — role `Supplier` | `?status=open&page=&pageSize=` | 200 `{ items: ServiceRequestSummaryDto[], total }` (empty until #293) |
| New | PUT | `/api/supplier/availability` | `[Authorize]` — role `Supplier` | `{ dates: [{ date, available }] }` | 200 `{ updated: number }` |
| New | GET | `/api/suppliers` | `[Authorize]` — role `PropertyOwner` | `?comune={code}&category=` | 200 `{ items: SupplierPickerDto[] }` — only `Status=Active` |

**SupplierProfileDto (summary):** `{ orgId, status, legalName, vatNumber?, phone, email, categories[], comuni[], bio?, photoUrls[], tosAcceptedAt? }`

**IDOR:** all `/api/supplier/*` scoped to JWT `orgId` where `OrgType=Supplier`; admin invite requires platform admin claim.

---

### #293 — Micro-marketplace v0 (US-021)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| New | POST | `/api/service-requests` | `[Authorize]` — host org | `{ propertyId, bookingId?, supplierOrgId, category, urgency, notes?, chargeToGuest? }` | 201 `ServiceRequestDto` / 403 supplier not Active in comune |
| New | GET | `/api/service-requests` | `[Authorize]` | `?status=&propertyId=&page=` | 200 `{ items[], total }` — host: org scope; supplier: assigned inbox |
| New | GET | `/api/service-requests/{id}` | `[Authorize]` | — | 200 `ServiceRequestDto` / 404 |
| New | POST | `/api/service-requests/{id}/take` | `[Authorize]` — supplier | — | 200 `{ status: "PresoInCarico", takenAt }` / 409 invalid transition |
| New | POST | `/api/service-requests/{id}/complete` | `[Authorize]` — supplier | `{ notes? }` | 200 `{ status: "Completato", completedAt }` / 409 |
| New | POST | `/api/service-requests/{id}/reject` | `[Authorize]` — supplier | `{ reason }` | 200 `{ status: "Rifiutato" }` / 409 |
| New | POST | `/api/service-requests/{id}/mark-paid` | `[Authorize]` — host | — | 200 `{ status: "Pagato", paidAt }` / 409 |

**ServiceRequestDto:** `{ id, orgId, bookingId?, propertyId, supplierOrgId, category, urgency, notes?, status, takenAt?, takenByUserId?, completedAt?, paidAt?, chargeToGuest }`

**State machine:** `Richiesto → PresoInCarico|Rifiutato → InCorso? → Completato → Pagato`; invalid → 409 Italian problem detail.

---

### #294 — iCal calendar sync (US-018)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| New | PUT | `/api/properties/{id}/ical` | `[Authorize]` — property owner | `{ importUrl }` | 200 `{ lastImportStatus, lastImportAt?, lastError? }` / 400 bad URL scheme |
| New | GET | `/api/properties/{id}/ical/export` | `[Authorize]` — property owner | — | 200 `{ exportUrl }` |
| New | GET | `/api/public/ical/{exportToken}` | Public — `[AllowAnonymous]` | — | 200 `text/calendar` VEVENT (no PII) / 404 |
| Modify | GET | `/api/bookings/calendar` | `[Authorize]` | existing query params | 200 — add entries `{ type: "ical-block", startUtc, endUtc, summary? }` |

**Background:** `ICalSyncJob` (Hangfire, every 15 min) — no HTTP surface.

**Secrets:** `PropertyICalFeed.ImportUrl` encrypted at rest (data protection / column encryption); export token unguessable (128-bit).

---

### #295 — Compliance wizards (US-019)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| New | GET | `/api/properties/{id}/compliance/activation` | `[Authorize]` — owner | — | 200 `{ complianceStatus, steps: [{ id, label, status, blocker? }] }` |
| New | POST | `/api/properties/{id}/compliance/activation/complete` | `[Authorize]` — owner | — | 200 `{ complianceStatus: "Active"|"Pending", blockers[]? }` |
| New | GET | `/api/compliance/summary` | `[Authorize]` — org member | — | 200 `{ propertiesPending, guestCheckInsIncomplete, checkoutsDue, alloggiatiFailures }` |
| New | POST | `/api/bookings/{id}/checkout-wizard/start` | `[Authorize]` — owner | — | 200 `{ steps: [{ id, label, status }] }` / 409 wrong booking state |
| New | POST | `/api/bookings/{id}/checkout-wizard/complete` | `[Authorize]` — owner | `{ confirmDeparture: true, ... }` | 200 `{ propertyReady, bookingStatus: "CheckedOut" }` |
| Modify | GET | `/api/public/orgs/{slug}/properties` | Public | — | 200 — exclude `ComplianceStatus != Active` |
| Existing | GET | `/api/properties/{id}/cin` | `[Authorize]` | — | reused in CIN wizard step |
| Existing | POST | `/api/properties/{id}/documents` | `[Authorize]` | multipart | reused in documents step |

---

### #296 — Guest check-in portal (US-020)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| Existing | GET | `/api/checkin/{token}` | Public — `[AllowAnonymous]` | — | 200 `CheckInContextDto` — extend with session status |
| Existing | POST | `/api/checkin/{token}/guest-data` | Public — `[AllowAnonymous]` | `SubmitGuestCheckInRequest` | 200 `{ dataComplete }` — wire to session + Alloggiati job |
| Existing | POST | `/api/checkin/{token}/document` | Public — `[AllowAnonymous]` | multipart | 200 |
| New | POST | `/api/bookings/{id}/check-in/resend-link` | `[Authorize]` — owner | — | 200 `{ sentAt }` / 404 |
| New | GET | `/api/bookings/{id}/check-in/status` | `[Authorize]` — owner | — | 200 `{ sessionStatus, sentAt?, completedAt? }` |

**Background jobs (no HTTP):** `GuestCheckInSendJob`, `GuestCheckInReminderJob`, `AlloggiatiWebReportJob` (existing, enqueue on complete).

**Rate limit:** existing `GuestCheckIn` fixed-window limiter on public check-in routes.

---

### #297 — Public site design system (US-023)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| Existing | GET | `/api/public/orgs/{slug}` | Public | — | 200 — consume `logoUrl`, `primaryColor`, `heroImageUrl`, `tagline` |
| Modify | GET | `/api/public/orgs/{slug}` | Public | — | add optional `publicThemeId` when BE column added |
| Existing | GET | `/api/public/orgs/{slug}/properties/{propertyId}` | Public | — | unchanged — UI rewrite only |
| Existing | GET | `/api/public/bookings/property/{propertyId}/availability` | Public | — | unchanged — booking widget |

No new HTTP endpoints required for MVP; FE-only unless `Org.PublicThemeId` migration added (optional).

---

### #298 — Custom domain booking (US-024)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| Existing | GET | `/api/public/resolve-host` | Public — `[AllowAnonymous]` | `?host={hostname}` | 200 `ResolveHostResponseDto` / 404 reserved or unknown |
| New | POST | `/api/orgs/{id}/domain` | `[Authorize]` — org owner | `{ customDomain, publicHostMode? }` | 200 `{ dnsInstructions, verificationToken }` / 403 Starter plan |
| New | POST | `/api/orgs/{id}/domain/verify` | `[Authorize]` — org owner | — | 200 `{ domainVerificationStatus: "Verified"|"Failed", message? }` |
| Existing | GET | `/api/orgs/me/entitlement` | `[Authorize]` | — | gates custom domain (`Plan.Pro`) |

**ResolveHostResponseDto (existing):** `{ orgId, slug, publicHostMode, branding: { slug, displayName, logoUrl, themeColor, contactEmail } }`

---

### #299 — Native host app (US-025)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| New | POST | `/api/devices` | `[Authorize]` | `{ platform, pushToken, deviceName? }` | 201 `{ deviceId }` |
| New | DELETE | `/api/devices/{id}` | `[Authorize]` | — | 204 |
| Existing | GET | `/api/bookings/calendar` | `[Authorize]` | — | M1 calendar |
| Existing | GET | `/api/bookings/{id}` | `[Authorize]` | — | M2 detail |
| New | POST | `/api/service-requests` | `[Authorize]` | — | M4 (from #293) |
| New | POST | `/api/service-requests/{id}/mark-paid` | `[Authorize]` | — | M6 |
| New | POST | `/api/bookings/{id}/checkout-wizard/start` | `[Authorize]` | — | M7 |
| Existing | GET | `/api/compliance/summary` | `[Authorize]` | — | M7 summary badges |

Mobile uses same JWT (Auth0 PKCE); no separate API surface beyond device registration.

---

### #300 — SEO funnel (US-026)

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| Existing | GET | `/api/public/content/affitti-brevi/{regionSlug}/{comuneSlug}` | Public | — | SEO content ( #258 ) |
| New | GET | `/api/public/seo/{comuneSlug}` | Public | — | 200 `{ title, content, meta, featuredProperties[], cta }` |
| New | POST | `/api/public/seo/events` | Public — `[AllowAnonymous]` | `{ event, comuneSlug, utm?, referrer? }` | 204 — no PII |
| New | GET | `/api/admin/seo/analytics` | `[Authorize]` — Admin | `?days=30` | 200 `{ topComuni: [{ slug, ctaClicks }] }` |

---

### #301 — Golden Journey E2E (GJ-001)

No new API endpoints. Harness asserts existing + new endpoints above return ≠ 500 during UI flows.

---

### Platform — #271, #230, #273, #274

| Status | Method | Path | Auth | Notes |
|---|---|---|---|---|
| Existing | GET | `/api/onboarding/status` | `[Authorize]` | #271 — extend with host mode step |
| Existing | POST/PUT | `/api/users/onboarding` | `[Authorize]` | #271 PLG completion |
| Existing | GET | `/api/billing/plans` | Public / Auth | #230 freemium |
| Existing | POST | `/api/billing/checkout-session` | `[Authorize]` | #230 |
| Existing | POST | `/api/webhooks/stripe` | Public — signature | #273 webhook hardening |
| Existing | PATCH | `/api/admin/orgs/{orgId}/plan` | `[Authorize]` Admin | #274 plan enforcement |

---

## Frontend Flow

### Route map (all Fase 1 features)

| Path | Component | Auth | Issue | Status |
|---|---|---|---|---|
| `/supplier/register` | `SupplierRegisterPage` | public | #292 | New |
| `/supplier/activation` | `SupplierActivationWizard` | `<ProtectedRoute role="Supplier">` | #292 | New |
| `/supplier/inbox` | `SupplierInboxPage` | `<ProtectedRoute role="Supplier">` | #292 | New |
| `/supplier/inbox/:id` | `SupplierRequestDetailPage` | `<ProtectedRoute role="Supplier">` | #292/#293 | New |
| `/supplier/profile` | `SupplierProfilePage` | `<ProtectedRoute role="Supplier">` | #292 | New |
| `/supplier/availability` | `SupplierAvailabilityPage` | `<ProtectedRoute role="Supplier">` | #292 | New |
| `/admin/suppliers` | `AdminSupplierInvitePage` | `<ProtectedRoute role="Admin">` | #292 | New |
| `/bookings/:id/service-request` | `CreateServiceRequestPage` | `<ProtectedRoute>` | #293 | New |
| `/properties/:id/settings/ical` | `PropertyIcalSettingsCard` | `<ProtectedRoute>` | #294 | New |
| `/properties/:id/activation` | `PropertyActivationWizard` | `<ProtectedRoute>` | #295 | New |
| `/compliance` | `ComplianceSummaryPage` | `<ProtectedRoute>` | #295 | New |
| `/bookings/:id/checkout` | `CheckoutWizardPage` | `<ProtectedRoute>` | #295 | New |
| `/check-in/:token` | `GuestCheckInPortalPage` | public | #296 | Modify existing |
| `/book/:slug` | `PublicBookingSite` | public | #297 | Modify — `PublicSiteShell` |
| `/book/:slug/:propertyId` | `PublicPropertyDetail` | public | #297 | Modify |
| `/settings/domain` | `CustomDomainSettingsPage` | `<ProtectedRoute>` | #298 | New |
| `/destinazioni/:comune` | `ComuneLandingPage` | public | #300 | Modify |
| `/signup` | `SignupPage` | public | #271/#300 | Modify — comune UTM |

**Supplier shell (#292 Wave 1):** separate `SupplierShell` layout — no host `AppShell` nav. Lazy route group `src/routes/supplier/*`.

### Component breakdown by feature

#### #292 — Supplier console (Wave 1 — this pipeline run)

| Component | Status | Location | Responsibility |
|---|---|---|---|
| `SupplierShell` | new | `src/layouts/SupplierShell.tsx` | Mobile-responsive nav, logout, role guard |
| `SupplierActivationWizard` | new | `src/features/supplier-console/ActivationWizard/` | 5 steps IT copy; progress persisted via activation API |
| `SupplierInboxPage` | new | `src/features/supplier-console/Inbox/` | List open requests; empty state until #293 |
| `SupplierProfileForm` | new | `src/features/supplier-console/Profile/` | Legal name, VAT, categories, comuni, bio, photos |
| `SupplierAvailabilityCalendar` | new | `src/features/supplier-console/Availability/` | Month view; tap toggle dates |
| `AdminSupplierInviteForm` | new | `src/features/admin/SupplierInvite/` | Admin invite email + comune picker |

**State & API (Wave 1):**

| Data | Hook | API module |
|---|---|---|
| activation steps | `useSupplierActivation` | `supplier.api.ts` |
| profile | `useSupplierProfile` | `supplier.api.ts` |
| inbox | `useSupplierInbox` | `supplier.api.ts` |
| availability | `useSupplierAvailability` | `supplier.api.ts` |
| admin invite | `useAdminSupplierInvite` | `admin-suppliers.api.ts` |

**GJ coverage:** Playwright steps 1–2 (supplier create + activation); mobile viewport F1–F2 scaffolded in Wave 1 layout (full inbox CTAs wired in Wave 2).

#### #293 — Micro-marketplace

| Component | Status | Location |
|---|---|---|
| `ServiceRequestTimeline` | new | `src/features/service-requests/` |
| `SupplierPicker` | new | embedded in booking detail + checkout wizard |
| `CreateServiceRequestForm` | new | host booking detail + native app parity |

#### #294 — iCal

| Component | Status | Location |
|---|---|---|
| `PropertyIcalSettingsCard` | new | `src/features/properties/IcalSettings/` |
| Calendar legend + block styling | modify | `src/features/calendar/` |

#### #295 — Compliance wizards

| Component | Status | Location |
|---|---|---|
| `PropertyActivationWizard` | new | `src/features/compliance/activation/` |
| `ComplianceSummaryWidget` | new | `src/features/compliance/summary/` |
| `CheckoutWizardPage` | new | `src/features/compliance/checkout/` |

#### #296 — Guest check-in

| Component | Status | Location |
|---|---|---|
| `GuestCheckInPortalPage` | modify | `src/features/public-check-in/` — mobile-first IT |
| `CheckInStatusBadge` | new | booking detail (host) |

#### #297 — Public site DS

| Component | Status | Location |
|---|---|---|
| `PublicSiteShell` | new | `src/layouts/PublicSiteShell.tsx` |
| `Hero`, `PropertyGallery`, `AmenityGrid`, `BookingWidget`, `Footer` | new | `src/features/public-site/` |
| `public-tokens.css` | new | design tokens shared with SEO pages |

#### #298 — Custom domain

| Component | Status | Location |
|---|---|---|
| `middleware.ts` | new | Vercel edge — resolve-host → tenant context |
| `CustomDomainSettingsPage` | new | DNS instructions + verify CTA |

#### #299 — Native host app (sibling repo)

Screens: Calendar, BookingDetail, CreateServiceRequest, MarkPaid, QuickCheckout, PropertyList — Auth0 PKCE; deep links to web for heavy wizards.

#### #300 — SEO funnel

| Component | Status | Location |
|---|---|---|
| `ComuneLandingPage` | rewrite | `src/features/seo/` — uses `PublicSiteShell` |
| `useSeoEvent` | new | fires `POST /api/public/seo/events` |

#### #301 — E2E harness

| File | Change |
|---|---|
| `e2e/golden-journey-web.spec.ts` | Extend steps 1–12 (F0 has 1–4) |
| `e2e/golden-journey-supplier-mobile.spec.ts` | F1–F2 mobile viewport |
| `mobile/e2e/golden-journey-host-app.e2e.ts` | Maestro M1–M7 |

---

## Security Notes

### Auth gates (by surface)

| Surface | Requirement |
|---|---|
| All `/api/supplier/*`, `/api/service-requests/*` (supplier actions) | `[Authorize]` + role `Supplier` + org tenant boundary |
| Host service-request create/mark-paid | `[Authorize]` + `PropertyOwner` + `OrgId` match |
| `/api/admin/suppliers/*`, `/api/admin/seo/analytics` | `[Authorize]` + platform `Admin` |
| `/api/suppliers/register` | Public — rate-limited; optional `inviteToken` validation |
| `/api/public/ical/{token}`, `/api/public/resolve-host`, SEO events | Public — no secrets in response; rate-limited |
| Guest check-in `/api/checkin/*` | Public — token GUID + expiry; existing rate limiter |
| Custom domain verify | Owner-only; DNS TXT prevents domain hijack |

### IDOR

- Property-scoped: verify `property.OrgId == user.OrgId` on iCal, compliance, checkout wizard.
- ServiceRequest: host reads own org; supplier reads where `SupplierOrgId == user.OrgId`.
- Supplier profile: single-org — JWT org claim must equal profile `OrgId`.

### Secrets & OTA keys

- **iCal import URLs:** stored encrypted (`PropertyICalFeed.ImportUrl`); never logged; not returned on GET except masked last-4.
- **iCal export token:** opaque UUID in URL path only; rotate on property owner request.
- **No OTA partner API keys in Fase 1** — iCal URL only; config path reserved: `OTA:Platforms:{Name}:ApiKey` (unused until Fase 3+).
- **Guest check-in token:** hash at rest if session entity added; single-use submit → 409 replay.
- **Auth0:** Supplier role + RBAC documented in `docs/AUTH0_SETUP.md` (#292).

### Stripe

- Existing webhook signature verification (#273) — no change to supplier console.
- `mark-paid` is manual confirmation MVP — no Stripe Connect charge in v0.

### PII data flow

| Feature | PII | Mitigation |
|---|---|---|
| #292 Supplier profile | email, phone, VAT | org-scoped; not public until supplier vetrina (Fase 2) |
| #296 Guest check-in | name, DOB, document, nationality | public token route; minimal fields; consent checkboxes; no PII in logs |
| #294 iCal export | none in SUMMARY | strip guest names from VEVENT |
| #300 SEO events | none | anonymous event payload only |

### STRIDE threat summary

| Threat | Surface | Mitigation |
|---|---|---|
| Spoofing | Supplier/host actions | Auth0 JWT + role claims; separate Supplier org type |
| Tampering | ServiceRequest state | Server-side state machine; 409 on invalid transitions |
| Information disclosure | resolve-host, public iCal | Branding only; no plan/Stripe; iCal without guest PII |
| Denial of service | Public check-in, resolve-host | Rate limits; Hangfire job isolation |
| Elevation | Admin invite | Admin role required; audit log on invite |

---

## Migration Plan

Per-feature EF changes (apply in wave order):

| Issue | Migration | Entities / columns |
|---|---|---|
| **#292** | `AddSupplierOrgAndProfile` | `OrgType.Supplier` enum value; `SupplierProfile` `{ OrgId PK/FK, Status, LegalName, VatNumber?, Phone, Email, Categories jsonb, Comuni jsonb, Bio, PhotoUrls jsonb, TosAcceptedAt, CreatedAt, UpdatedAt }`; `SupplierAvailability` `{ Id, OrgId, Date, Available }` |
| **#293** | `AddServiceRequests` | `ServiceRequest` per US-021 AC1; indexes on `(OrgId)`, `(SupplierOrgId, Status)` |
| **#294** | `AddCalendarBlocksAndICalFeeds` | `CalendarBlock`, `PropertyICalFeed` per spec; unique `(PropertyId, ExternalUid)` |
| **#295** | `AddComplianceWizardFields` | `Property.ComplianceStatus`; `PropertySafetyChecklist` jsonb column |
| **#296** | `AddGuestCheckInSessions` | `GuestCheckInSession` per spec; optional extend `Booking.CheckInToken*` |
| **#297** | N/A or optional | `Org.PublicThemeId` nullable string — defer if FE-only theming |
| **#298** | `AddOrgCustomDomainFields` | `Org.CustomDomain`, `DomainVerificationStatus`, `DomainVerificationToken`, `PublicHostMode` (enum exists) |
| **#299** | `AddDeviceRegistrations` | `DeviceRegistration` `{ Id, UserId, OrgId, Platform, PushToken, CreatedAt }` |
| **#300** | optional `AddSeoEvents` | `SeoEvent` `{ Id, Event, ComuneSlug, Utm, Referrer, CreatedAt }` |
| **#301** | N/A | test harness only |
| **Platform #298 partial** | may reuse F0 `PublicHostMode` on Org | verify column presence before migration |

**Wave 1 (#292) applies only:** `AddSupplierOrgAndProfile`.

---

## GDPR Scope

| Issue | Guest data | Scope |
|---|---|---|
| **#292** | N/A | Supplier business contact only — not Guest PII |
| **#293** | N/A | Service notes must not require guest document numbers |
| **#294** | N/A | iCal blocks must exclude guest identity in export |
| **#295** | Indirect | Checkout wizard triggers retention scheduling on `CheckedOut` — reuse existing booking GDPR hooks |
| **#296** | **Yes** | Fields: name, DOB, nationality, document type/number, address; GDPR consents on submit; `ErasureRequested` check before display; `DataRetentionUntil` set on checkout; Alloggiati job async — no PII in job logs |
| **#297–#301** | N/A (except #296 dependency) | Public site shows no guest data |
| **#300** | N/A | Anonymous analytics events |

**Wave 1 (#292):** N/A — no Guest personal data in scope.

---

## Open Questions

None — resolved for Wave 1:

| Question | Resolution |
|---|---|
| Reuse `/api/checkin/{token}` vs new `/api/public/check-in/{token}`? | **Extend existing** `/api/checkin/*` — avoid duplicate public surface (#296) |
| Supplier Auth0 role name | **`Supplier`** claim + `OrgType.Supplier` (#292) |
| `resolve-host` shipped in F0? | **Existing** — extend for custom domain verification in Wave 5 (#298) |
| Alloggiati assert in CI GJ step 6? | **Mock/skip** unless Questura test credentials configured (GJ-001 AC regulatory gate) |

---

## Pipeline Wave Plan

Epic **#291** is delivered in **8 pipeline waves** (one child issue or parallel group per wave). Each wave merges to `develop`, then epic exit wave promotes via standard Stage 05 flow.

| Wave | Issues | Branch | Pipeline scope | Depends on |
|---|---|---|---|---|
| **1 (this run)** | **#292** | `feature/291-mvp-f1-wave1-supplier-console` | Supplier identity, Auth0 role, activation wizard, inbox shell, availability, admin invite; GJ steps 1–2 E2E scaffold | Fase 0 complete (#286) |
| 2 | #293 | `feature/291-mvp-f1-wave2-micro-marketplace` | `ServiceRequest` CRUD + state machine; host picker; supplier inbox CTAs | Wave 1 |
| 3 | #294, #295, #296 (parallel) | `feature/291-mvp-f1-wave3a-ical`, `…-wave3b-compliance`, `…-wave3c-guest-checkin` | iCal sync; compliance wizards; guest portal + jobs | Wave 2 (checkout step 3 → #293) |
| 4 | #297 | `feature/291-mvp-f1-wave4-public-site-ds` | `PublicSiteShell` + template; `/book/{slug}` redesign | Wave 3 (compliance gating on public list) |
| 5 | #298 | `feature/291-mvp-f1-wave5-custom-domain` | Org domain fields, DNS verify, Vercel middleware | Wave 4, #271 onboarding step |
| 6 | #299 | `feature/291-mvp-f1-wave6-native-host-app` | Expo app + Maestro M1–M7 + device push | Waves 3–5 |
| 7 | #300 | `feature/291-mvp-f1-wave7-seo-funnel` | SEO pages + CTA analytics | Wave 4 |
| 8 | #301 | `feature/291-mvp-f1-wave8-golden-journey-e2e` | Full 12-step Playwright + F1–F2 + CI workflow | All feature waves |

**Platform (#271, #230, #273, #274):** integrate across Waves 4–5 (onboarding host mode, billing gates); security hardening continuous.

### Wave 1 deliverables checklist (#292)

**Backend**
- [ ] `OrgType.Supplier`, `SupplierProfile`, `SupplierAvailability` migration
- [ ] `SupplierProfileController`, `AdminSuppliersController`
- [ ] Auth0 Supplier role + policy registration
- [ ] Integration tests: register, activation complete, admin invite, availability PUT

**Frontend**
- [ ] `SupplierShell` + route group `/supplier/*` with `<ProtectedRoute role="Supplier">`
- [ ] Activation wizard (5 steps, Italian)
- [ ] Inbox + profile + availability pages (mobile-responsive 375px)
- [ ] Admin invite page `<ProtectedRoute role="Admin">`

**E2E**
- [ ] Extend `golden-journey-web.spec.ts` steps 1–2 (supplier signup + activation → `Active`)
- [ ] Mobile viewport smoke on `/supplier/inbox` layout (F1–F2 placeholder until Wave 2)

**Stage 03 branch:** `feature/291-mvp-f1-wave1-supplier-console`

---

## Handoff

| Field | Value |
|---|---|
| Issue | #291 (Wave 1 implements #292) |
| Spec | `Sessions/design-291.md` |
| Branch | `feature/291-mvp-f1-wave1-supplier-console` |
| Next stage | 03 Development — backend + frontend per Wave 1 checklist |
