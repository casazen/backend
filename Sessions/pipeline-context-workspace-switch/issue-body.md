## User Story

As a CasaZen user who may operate in one or more operational areas (short-stay rentals, long-term leases, platform administration), I want the application to treat each area as a distinct **workspace/context** with canonical URLs, a persistent workspace switcher, and navigation derived from my active context—so I never need to guess or manually type the correct route after login.

## Acceptance Criteria

- [ ] GIVEN an authenticated user, WHEN they navigate the app, THEN all protected app routes live under canonical context prefixes: `/app/short-rent/*`, `/app/long-rent/*`, and `/app/admin/*` (no new flat operational routes at root such as `/bookings` or `/leases` without context prefix)
- [ ] GIVEN a centralized **route manifest** (single source for `path`, `context`, `requiredPermissions`, `navLabel`, `isDefault`), WHEN the app builds the sidebar and default redirects, THEN menu items and landing routes for each context are derived from the manifest—not from duplicated per-shell nav config or global Auth0 role names alone
- [ ] GIVEN a user with membership in more than one context, WHEN they use the application shell, THEN a **workspace switcher** (e.g. Italian labels *Affitti brevi*, *Affitti lungo termine*, *Amministrazione*) is always visible in the header or sidebar and switching context updates `activeContext`, the nav tree, and the URL to the target context’s default landing
- [ ] GIVEN a user who has just completed login, WHEN the app loads available contexts from the backend (or bootstrap API), THEN: (a) a single available context triggers automatic redirect to that context’s default route; (b) multiple contexts show a context picker or restore last-used context from persisted preference; (c) post-login redirect never sends the user to a context they cannot access
- [ ] GIVEN a user without membership or required permissions for a context or route, WHEN they open a deep link (e.g. `/app/long-rent/contracts`) or switch workspace, THEN the app redirects to a valid home for an allowed context or shows a managed 403—not silent access or a broken shell
- [ ] GIVEN any API call for a context-scoped resource, WHEN the backend authorizes the request, THEN access is evaluated using **contextual** membership/permissions (not only global JWT roles such as `PropertyOwner` / `LongTermLandlord` / `Admin`); the frontend guards reflect but do not replace backend decisions
- [ ] GIVEN legacy URLs from prior navigation (e.g. `/`, `/leases`, `/admin/users` from #182), WHEN users or bookmarks hit old paths, THEN temporary redirects preserve usability until redirects are removed in a follow-up (design stage defines mapping table)

## Technical Notes

**Design reference**: [Sessions/specs/spec-split-layer.md](https://github.com/casazen/backend/blob/main/Sessions/specs/spec-split-layer.md) (structured analysis; pipeline copy in `Sessions/pipeline-context-workspace-switch/external-analysis.md`).

**Relation to prior work**: Closed issue #182 (`long-term-ui-layer`) introduced separate short-stay / long-term / admin shells with role-based `ProtectedRoute`, `AppLayerProvider`, and a dual-role **layer switcher**. This issue **generalizes** that pattern into a unified multi-context architecture (context-prefixed routing, manifest-driven nav, backend contextual auth). Do not re-scope closed #182 deliverables (lease pages, shell split); extend and migrate them.

### Current state (baseline)

| Area | Today |
|---|---|
| FE routing | Flat paths (`/`, `/leases`, `/admin/*`); `ProtectedRoute` + `AppLayerProvider` + per-role shells (#182) |
| FE switcher | `layer-switcher.tsx` — dual-role short-stay ↔ long-term; not a full three-context workspace model |
| BE auth | Auth0 JWT global roles in claim `https://casazen.app/roles`; no `UserContextMembership` entity yet |

### Frontend impact (`casazen/frontend`)

| Concern | Approach |
|---|---|
| Route tree | Nest under `/app/:context/...`; introduce route manifest module consumed by router, sidebar, and guards |
| State | `activeContext` (`short-rent` \| `long-rent` \| `admin`) in app state + persistence (last-used) |
| Shell | Unify/refactor `AppShell`, `LongTermAppShell`, `AdminAppShell` to share manifest-driven nav; workspace switcher always on for multi-context users |
| Guards | Context-aware route guard (membership + permissions from bootstrap API); deprecate role-only `ProtectedRoute` over time |
| Post-login | Replace fixed `/` redirect with context resolution flow |
| i18n | Nav/switcher labels remain Italian in UI; route keys English |

**Expected touch areas**: `src/routes/index.tsx`, `src/contexts/app-layer-*`, `src/components/layout/*`, `src/components/auth/protected-route.tsx`, new `src/config/route-manifest.ts` (or equivalent), API client for contexts/bootstrap.

### Backend impact (`casazen/backend`)

| Concern | Approach |
|---|---|
| Data model | Introduce contextual authorization primitives (minimum: `UserContextMembership` linking user ↔ `ContextKey` ↔ role; `Role` / `RolePermission` or equivalent grant table)—exact schema in Stage 02 |
| API | Endpoint(s) for current user’s available contexts, roles, and permissions; enforce on existing controllers via policy/handler evaluated per request context |
| Auth0 | Phase 1 may map existing JWT roles to synthetic context membership for migration; Phase 2 aligns claims or uses server-side membership as source of truth |
| Migrations | **EF Core migration expected** for membership/permission tables; no breaking change to lease/booking entities in this epic slice |

### Infrastructure / OTA / jobs

| Area | Impact |
|---|---|
| **EF Core migrations** | Yes — new authorization/membership tables (and seed data for dev) |
| **OTA platforms** | None — short-rent context continues to own OTA surfaces; ensure admin/long-rent manifests do not expose OTA nav |
| **Background jobs** | None expected; Hangfire admin UI remains under `admin` context routes |
| **Stripe / webhooks** | None |

### Phased delivery (recommended)

1. **Phase A — FE routing & manifest**: Context-prefixed routes, manifest, redirects from legacy paths, switcher UX wired to `activeContext` (membership stubbed from JWT roles if needed).
2. **Phase B — BE contextual auth**: Persistence, bootstrap API, policy enforcement on critical endpoints.
3. **Phase C — JWT/Auth0 alignment**: Reduce reliance on global operational roles where they duplicate context membership.

**Complexity**: effort **L (3–5 days)** cross-repo — routing migration + authorization model + API contract.

### Risks

| Risk | Mitigation |
|---|---|
| Bookmark/deep-link breakage | Redirect table; e2e tests for legacy paths |
| Dual source of truth (JWT roles vs DB membership) | Document transition; single bootstrap API for FE |
| Role explosion on backend | Capability-style permissions per context (#182 used coarse roles) |

## Compliance Assessment

**Regulations in scope**: None new. Navigation and authorization restructuring does not introduce new regulated data flows beyond existing short-stay (CIN/Alloggiati) and long-term lease flows.

**Label**: `none-required`

## Dependencies

- Builds on / generalizes: #182 (closed — long-term UI layer separation)
- Spec: `Sessions/specs/spec-split-layer.md`
- Stage 02 design spec: `Sessions/design-<N>.md` (to be created after planning gate)

## Planning artifact

Pipeline: `Sessions/pipeline-context-workspace-switch` — Stage 01 planning handoff → Stage 02 design.
