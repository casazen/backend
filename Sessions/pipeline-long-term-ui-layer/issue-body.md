## User Story

As a CasaZen user, I want the app to present **separate navigation layers** for short-stay property management and long-term lease management, so that I only see the tools relevant to my role and can switch contexts clearly when I hold both roles.

## Acceptance Criteria

- [ ] GIVEN a user with **PropertyOwner** role only (no `LongTermLandlord`), WHEN they log in and browse the app, THEN the short-stay shell is shown with Dashboard, Properties, Bookings, OTA, etc., and **no** long-term nav items (Leases, long-term layer switcher, or `/leases` links) are visible
- [ ] GIVEN a user with **LongTermLandlord** role only (no `PropertyOwner`), WHEN they log in, THEN they enter the long-term UI layer with a dedicated sidebar (e.g. Leases, long-term Dashboard placeholder or lease home) and **no** short-stay items (Bookings, OTA Sync, Payments tied to short-stay) are visible
- [ ] GIVEN a user with **both** `PropertyOwner` and `LongTermLandlord` roles, WHEN they use the app, THEN a persistent **layer switcher** (header or sidebar control) toggles between short-stay and long-term shells without losing authentication
- [ ] GIVEN an authenticated LongTermLandlord in the long-term layer, WHEN they navigate to lease routes, THEN existing pages at `/leases`, `/leases/new`, and `/leases/:id` render inside the long-term shell (reuse current feature components; no CRUD reimplementation)
- [ ] GIVEN a PropertyOwner-only user, WHEN they manually navigate to `/leases`, THEN they are redirected to an authorized destination (short-stay dashboard or 403/unauthorized page) and cannot access lease content
- [ ] GIVEN a dual-role user who switches to the long-term layer, WHEN they click a lease nav item, THEN navigation stays within the long-term shell and active route highlighting reflects the long-term sidebar config

## Technical Notes

**Scope**: Frontend only (`casazen/frontend`). Reuses lease pages from #177; this issue is **shell/navigation architecture**, not lease CRUD.

### FE shell architecture

| Concern | Approach |
|---|---|
| Short-stay layer | Existing `AppShell` + `Sidebar` with Properties, Bookings, OTA, Payments, Search, Profile |
| Long-term layer | New `LongTermAppShell` + `LongTermSidebar` (Leases + future long-term items); distinct branding subtitle (e.g. "Long-Term Rental") |
| Route grouping | Option A: prefix long-term routes under `/long-term/*` with redirects from `/leases/*`; Option B: keep `/leases/*` but wrap in layout route with `LongTermAppShell` — design stage to pick one |
| Role-based default | On post-login redirect, route users to the layer matching their primary available mode (LongTermLandlord-only → long-term home; PropertyOwner-only → `/`; dual-role → last-used layer persisted in `localStorage`) |
| Layer switcher | Dual-role only; visible in header; switches shell context and nav tree |
| Protected routes | Extend layout-level guards: long-term layout requires `LongTermLandlord`; short-stay layout hides long-term entry points |

### Affected files (expected)

| File / area | Change |
|---|---|
| `src/components/layout/app-shell.tsx` | Split or parameterize for short-stay vs long-term |
| `src/components/layout/sidebar.tsx` | Short-stay nav only; remove role-gated Leases item |
| `src/components/layout/long-term-sidebar.tsx` | New — long-term nav config |
| `src/components/layout/layer-switcher.tsx` | New — dual-role toggle |
| `src/routes/index.tsx` | Nested layout routes for each layer |
| `src/features/leases/*` | Swap `AppShell` → `LongTermAppShell` (or inherit from layout route) |
| `src/lib/auth-roles.ts` | Helpers: `isLongTermOnly`, `isShortStayOnly`, `isDualRole` |
| `src/hooks/use-app-layer.ts` | New — layer state + persistence |

### Backend / infrastructure impact

| Area | Impact |
|---|---|
| **EF Core migrations** | None |
| **OTA platforms** | None — long-term layer must not surface OTA nav |
| **Background jobs** | None |
| **Auth0 / backend** | No API changes expected; roles already in JWT claim `https://casazen.app/roles`. Verify no new role metadata needed in Stage 02 |

### Dependencies

- Parent epic: #165 (closed) — long-term lease vertical
- Prior FE deliverable: #177 (closed) — lease pages at `/leases/*`
- Auth0 role: `LongTermLandlord` (see #167)

### Complexity

**effort:M (1–2 days)** — routing refactor + two shell variants + layer switcher; lease feature code unchanged.

### Risks

| Risk | Mitigation |
|---|---|
| Deep links to `/leases/*` break after route restructure | Add redirects; preserve `/leases/*` paths or 301 to `/long-term/leases/*` |
| Dual-role users confused by default layer | Persist last layer; show switcher prominently |
| Per-page `AppShell` duplication | Prefer React Router layout routes so pages don't import shell directly |

## Compliance Assessment

**Regulations in scope**: None new. This feature reorganizes navigation and does not collect, process, or display additional regulated data beyond existing lease flows (#177).

**Label**: `none-required`

CIN, Alloggiati Web, tourist tax, and short-stay guest reporting remain in the short-stay layer only. Long-term GDPR/Questura/APE guardrails from #177 remain unchanged in reused lease components.

## Dependencies

- Builds on: #177 (lease UI section — closed)
- Part of: #165 epic (Long-Term Lease — closed)
- No backend blocker expected

## Planning artifact

Stage 01 — see `Sessions/planning-<N>.md`. Handoff → Stage 02: `Sessions/design-<N>.md`.
