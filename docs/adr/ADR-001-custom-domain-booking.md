# ADR-001: Custom domain and subdomain booking resolution

**Status:** Accepted (Fase 0 spike)  
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
