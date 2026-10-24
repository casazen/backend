# ADR-001: Custom domain and subdomain booking resolution

**Status:** Accepted (amended 2026-10-24)  
**Date:** 2026-06-19  
**Issue:** #288  
**Informs:** `spec-custom-domain-booking` (US-024, Fase 1)

## Context

CasaZen hosts need three publication modes: `casazen.it/book/{slug}`, `{slug}.casazen.it`, and `www.customdomain.it` (CNAME). Guests must perceive the site as the host's brand while the booking engine remains CasaZen API + Stripe Connect.

## Decision

### Edge resolution — Vercel middleware (primary)

Use **Vercel Edge Middleware** in the frontend repo to resolve tenant context before React render:

1. Read `Host` header from incoming request.
2. Call `GET /api/public/resolve-host?host={host}` (new endpoint, Fase 1).
3. Inject `orgId`, `slug`, `branding` into request context (cookie or header for SSR).
4. Route to `PublicSiteShell` with tenant theme.

**Rejected:** Cloudflare for SaaS as primary — adds cost and DNS complexity before first beta host. Revisit if Vercel custom domain limits block Pro plan scale.

### SSL

Automatic via **Vercel Custom Domains** for both `*.casazen.it` wildcard and per-org custom domains. Document verification flow in `docs/INFRA.md`.

### Backend contract (`resolve-host`)

```http
GET /api/public/resolve-host?host=www.tuovilla.it
→ 200 { orgId, slug, branding: { logoUrl, primaryColor, heroImageUrl, publicThemeId }, publicHostMode }
→ 404 unknown host
```

`[AllowAnonymous]` — returns only public branding, no PII.

### Org fields (Fase 1 migration)

- `PublicHostMode`: `CasazenSubdomain` | `CasazenPath` | `CustomDomain`
- `CustomDomain`, `DomainVerificationStatus`, `DomainVerificationToken`

### Security — host-header allowlist

- Middleware rejects hosts not matching `*.casazen.it`, `casazen.it`, or a verified `CustomDomain` in DB.
- No open redirect: resolve-host returns 404 for unregistered hosts; middleware serves generic 404 page.
- Rate-limit `resolve-host` (60 req/min per IP) to prevent host enumeration.

### Plan gating

- Starter: `CasazenSubdomain` or `CasazenPath` only.
- Pro+: `CustomDomain` with DNS TXT/CNAME verification.

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-001-R1 | P0 | Vercel Edge Middleware resolves tenant from `Host` before React render |
| ADR-001-R2 | P0 | `GET /api/public/resolve-host?host=` returns public branding only (`[AllowAnonymous]`), 404 for unknown hosts |
| ADR-001-R3 | P0 | Org supports `PublicHostMode` (`CasazenSubdomain` \| `CasazenPath` \| `CustomDomain`) plus custom-domain verification fields |
| ADR-001-R4 | P0 | Middleware allowlists `*.casazen.it`, `casazen.it`, and verified custom domains only; no open redirect |
| ADR-001-R5 | P1 | Rate-limit `resolve-host` (60 req/min per IP) |
| ADR-001-R6 | P1 | Custom domain gated to Pro+; Starter limited to subdomain/path modes |

## PoC scope (Fase 0)

Document only. Optional staging test: configure one `test-org.casazen.it` subdomain on Vercel preview — manual verification, not CI-gated in F0.

## Consequences

- Fase 1 requires EF migration on `Org`, new `resolve-host` endpoint, Vercel middleware, and onboarding PLG step for host mode selection.
- Custom domain for supplier orgs deferred to Fase 2.

## Amendment 2026-10-24 (run R20261024-1925)

**Why:** NF-02 adds Swiss property support with CHF currency. Properties need country and currency fields. Direct booking for CHF properties is deferred per D-02.

**Decision delta:**

### Property Country and Currency Fields

Add to Property entity:
- `Country` varchar(2) non-null default 'IT' (ISO 3166-1 alpha-2)
- `Currency` varchar(3) non-null default 'EUR' (ISO 4217)

### Swiss Municipality Table

For Swiss properties (Country = 'CH'):
- `SwissMunicipality` table (CantonCode, MunicipalityCode, OfficialName) for city lookup
- Similar to Comuni table for Italian properties (ADR-007)

### CHF Direct Booking Deferred

Per D-02, direct booking for CHF properties is not supported in phase 1:
- `POST /api/bookings` for CHF property returns 501 `currency_not_supported`
- Manual booking and iCal sync work normally
- Future phase: multi-currency Stripe Connect setup

### Resolve-Host Extension

`GET /api/public/resolve-host` response includes:
- `property.currency` in branding response for multi-currency display

| ID | Priority | Requirement |
|---|---|---|
| ADR-001-R7 | P0 | Property MUST add Country varchar(2) non-null default 'IT', Currency varchar(3) non-null default 'EUR' |
| ADR-001-R8 | P0 | Migration MUST backfill all existing Property rows to Country='IT', Currency='EUR' |
| ADR-001-R9 | P0 | SwissMunicipality table (CantonCode, MunicipalityCode, OfficialName) for CH property city lookup |
| ADR-001-R10 | P0 | Direct booking for CHF properties MUST return 501 `currency_not_supported` in phase 1 (deferred per D-02) |
| ADR-001-R11 | P1 | GET /api/public/resolve-host MUST include property.currency in branding response for multi-currency display |

**Consequences delta:**
- Backend: Property.Country + Property.Currency fields, SwissMunicipality migration, direct booking validation
- Frontend: Country selector in property form, currency display
- Multi-currency bookings without conversion deferred (totals per currency, no exchange rates)
