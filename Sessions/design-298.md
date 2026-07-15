# Design Spec — Issue #298: Custom domain + subdomain booking (US-024)

**Issue**: [#298](https://github.com/casazen/backend/issues/298)  
**Feature**: feat(MVP F1): custom domain + subdomain booking (US-024)  
**Spec**: `Sessions/specs/spec-custom-domain-booking.md`  
**ADR**: `docs/adr/ADR-001-custom-domain-booking.md`  
**Status**: complete  
**Date**: 2026-07-14  
**Branch**: `feature/298-custom-domain-booking`  
**Depends on**: #297 (PublicSiteShell), #271 (onboarding PLG — non-blocking)  
**Blocks**: #301 (golden-journey-e2e)

---

## Summary

Extend F0 `GET /api/public/resolve-host` so Host maps to Org for three publication modes: path (`casazen.it/book/{slug}`), subdomain (`{slug}.casazen.it`), and Pro custom domain (`www.customdomain.it` via CNAME + TXT verify). Add Org domain fields + owner settings UI; Vercel middleware injects tenant context into the existing `PublicSiteShell`. Do not block on #271 — ship a settings-page domain panel now; onboarding wizard step is a thin stub/link for later absorption.

**F0 already shipped (reuse, do not duplicate):**
- `PublicHostController` → `GET /api/public/resolve-host`
- `PublicHostResolver` (subdomain-only today)
- `PublicHostMode` enum
- `ResolveHostResponseDto`

**New in #298:** Org schema columns, custom-domain resolve path, domain CRUD/verify APIs, entitlement gate, DNS verification service, FE middleware + settings panel, INFRA SSL runbook.

---

## Data model

### New columns on `Org` (migration `AddOrgCustomDomain`)

| Column | Type | Nullable | Default | Notes |
|---|---|---|---|---|
| `PublicHostMode` | `int` (`PublicHostMode`) | no | `CasazenPath` (1) | Enum already exists in Core |
| `CustomDomain` | `string` MaxLength **253** | yes | null | Normalized FQDN lowercase, no scheme/port |
| `DomainVerificationStatus` | `int` (`DomainVerificationStatus`) | no | `Pending` (0) | New enum |
| `DomainVerificationToken` | `string` MaxLength **128** | yes | null | Cryptographically random; used in TXT challenge |
| `Subdomain` | `string` MaxLength **63** | yes | null | Label for `*.casazen.it`; pattern `^[a-z0-9]([a-z0-9-]*[a-z0-9])?$` |

### New enum — `DomainVerificationStatus`

```csharp
public enum DomainVerificationStatus
{
    Pending = 0,
    Verified = 1,
    Failed = 2,
}
```

### Indexes (filtered unique)

| Index | Columns | Filter |
|---|---|---|
| `UX_Orgs_CustomDomain` | `CustomDomain` | `WHERE "CustomDomain" IS NOT NULL` |
| `UX_Orgs_Subdomain` | `Subdomain` | `WHERE "Subdomain" IS NOT NULL` |

### Resolution precedence (`PublicHostResolver`)

1. Normalize host (trim, lower, strip port).
2. If host matches a row where `CustomDomain == host` **and** `DomainVerificationStatus == Verified` **and** `PublicHostMode == CustomDomain` **and** `IsActive` → return that org.
3. Else if host is `{label}.{BaseDomain}` (not apex/www, not reserved, single label) → lookup by `Subdomain == label` (fallback: `Slug == label` when `Subdomain` is null for back-compat) and `PublicHostMode` in `{CasazenSubdomain, CasazenPath}` allowed for resolve when subdomain DNS hits (path mode still works via `/book/{slug}` without resolve-host).
4. Else → null → HTTP 404.

**Path mode** (`casazen.it/book/{slug}`) does not require resolve-host; existing public org routes remain source of truth.

### Backfill

- Existing orgs: `PublicHostMode = CasazenPath`, `DomainVerificationStatus = Pending`, other domain fields null.
- Optional: set `Subdomain = Slug` when enabling CasazenSubdomain mode via API (not bulk-backfill).

---

## API Contract

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| **Modify** | GET | `/api/public/resolve-host` | `[AllowAnonymous]` — public branding only; rate-limited | Query `host` (required, max 253) | 200 `ResolveHostResponseDto` / 400 / 404 / 429 |
| **New** | GET | `/api/orgs/{orgId}/domain` | `[Authorize]` — caller org must equal `{orgId}` | — | 200 `OrgDomainConfigDto` / 401 / 403 / 404 |
| **New** | POST | `/api/orgs/{orgId}/domain` | `[Authorize]` — caller org must equal `{orgId}` | `SetOrgDomainRequest` | 200 `OrgDomainConfigDto` (+ DNS instructions) / 400 / 401 / 403 / 404 / 409 |
| **New** | POST | `/api/orgs/{orgId}/domain/verify` | `[Authorize]` — caller org must equal `{orgId}` | — | 200 `OrgDomainVerifyResultDto` / 401 / 403 / 404 |

> **Do not create** a second resolve endpoint (e.g. `PublicResolveController`). Extend `PublicHostController` + `PublicHostResolver` only.

---

### GET `/api/public/resolve-host` (modify — extend F0)

**Auth**: `[AllowAnonymous]` — required for Vercel edge middleware before guest auth. Justified: returns public branding + tenant ids only; no Guest/Owner PII, no Stripe secrets.

**Query params**:

| Param | Type | Required | Notes |
|---|---|---|---|
| `host` | `string` | yes | Hostname only; max 253; controller strips port if present |

**Response `200` — `ResolveHostResponseDto` (extended):**

| Field | Type | Notes |
|---|---|---|
| `orgId` | `Guid` | Tenant id for middleware rewrite |
| `slug` | `string` | Org slug (path fallback / PublicSiteShell) |
| `publicHostMode` | `PublicHostMode` | `CasazenSubdomain` \| `CasazenPath` \| `CustomDomain` |
| `planTier` | `string` | Effective tier name (`Starter`/`Pro`/`Scale`) — AC2; not PII |
| `branding` | object | See below |

**Branding object (no PII beyond public business surface):**

| Field | Type | Source |
|---|---|---|
| `logoUrl` | `string?` | `Org.LogoUrl` |
| `primaryColor` | `string?` | `Org.ThemeColor` (JSON name `primaryColor`; map from ThemeColor) |
| `publicThemeId` | `string?` | `Org.PublicThemeId` |
| `heroImageUrl` | `string?` | `Org.HeroImageUrl` |
| `tagline` | `string?` | `Org.Tagline` |
| `displayName` | `string` | `Org.DisplayName` |
| `slug` | `string` | `Org.Slug` |
| `showPoweredBy` | `bool` | `PlanTier == Starter` |

**Explicitly excluded from response:** `ContactEmail`, Stripe ids, `DomainVerificationToken`, owner Auth0 subject, Guest fields, billing VAT.

> Implementation note: prefer a slim `ResolveHostBrandingDto` (or project PublicOrgDto without ContactEmail) so F0 `ContactEmail` does not leak via resolve-host. Public org endpoints may still expose ContactEmail separately.

**Errors:**
- `400` — missing/empty `host`
- `404` — unknown / reserved / unverified custom domain
- `429` — rate limit exceeded (`PublicResolveHost` policy)

**Caching:** in-process / response cache **60s** keyed by normalized host (`PublicHost:ResolveCacheSeconds`, default 60). Invalidate on successful domain set/verify for that org (best-effort).

**Rate limit:** fixed-window **60 req/min per IP** — policy name `PublicResolveHost`; config `PublicHost:RateLimitPermitLimit` (default 60).

---

### GET `/api/orgs/{orgId}/domain` (new)

**Auth**: `[Authorize]` — valid Auth0 JWT. **IDOR**: `orgId` must equal `IOrgContextResolver.GetOrProvisionOrgIdAsync()`; else **403**.

**Request body:** none.

**Response `200` — `OrgDomainConfigDto`:**

| Field | Type | Notes |
|---|---|---|
| `orgId` | `Guid` | |
| `publicHostMode` | `PublicHostMode` | |
| `subdomain` | `string?` | |
| `customDomain` | `string?` | |
| `domainVerificationStatus` | `DomainVerificationStatus` | |
| `canUseCustomDomain` | `bool` | from entitlement |
| `dnsInstructions` | `DnsInstructionsDto?` | present when mode is CustomDomain and status ≠ Verified (or always when custom domain set) |
| `publicUrls` | object | `{ pathUrl, subdomainUrl?, customDomainUrl? }` preview helpers |

**`DnsInstructionsDto`:**

| Field | Type | Notes |
|---|---|---|
| `cnameHost` | `string` | e.g. `www` or apex instruction |
| `cnameTarget` | `string` | `cname.vercel-dns.com` (config `PublicHost:VercelCnameTarget`) |
| `txtHost` | `string` | `_casazen-challenge` (or `_casazen-challenge.{customDomain}`) |
| `txtValue` | `string` | `DomainVerificationToken` |
| `sslNote` | `string` | short pointer to Vercel Custom Domains SSL |

**Errors:** `401`, `403` (wrong org), `404` (org missing).

---

### POST `/api/orgs/{orgId}/domain` (new)

**Auth**: `[Authorize]` — JWT + **IDOR** org match (same as GET).

**Request body — `SetOrgDomainRequest`:**

| Field | Type | Required | Notes |
|---|---|---|---|
| `hostMode` | `PublicHostMode` | yes | `CasazenPath` \| `CasazenSubdomain` \| `CustomDomain` |
| `customDomain` | `string?` | when CustomDomain | FQDN; max 253; lowercase; reject IP literals, `*.casazen.it`, reserved apex |
| `subdomain` | `string?` | when CasazenSubdomain | max 63; `^[a-z0-9]([a-z0-9-]*[a-z0-9])?$`; not in `PublicHost:ReservedSubdomains` |

**Business rules:**
1. `hostMode == CustomDomain` → require `IEntitlementService.CanUseCustomDomainAsync(orgId)` → else **403** `{ code: "plan_required", requiredPlan: "Pro" }`.
2. On CustomDomain set: generate new `DomainVerificationToken` (128-bit URL-safe), set `DomainVerificationStatus = Pending`, store normalized `CustomDomain`.
3. On CasazenSubdomain: set `Subdomain` (default `Slug` if omitted); clear custom domain fields optional (null CustomDomain + token) or retain for later re-verify — **decision: clear custom domain fields when leaving CustomDomain** to avoid stale verified hosts.
4. Unique conflicts on `CustomDomain` / `Subdomain` → **409**.
5. Starter may set Path or Subdomain only.

**Response `200`:** `OrgDomainConfigDto` including DNS instructions when CustomDomain.

**Errors:** `400` validation, `401`, `403` plan/IDOR, `404`, `409` conflict.

---

### POST `/api/orgs/{orgId}/domain/verify` (new)

**Auth**: `[Authorize]` — JWT + **IDOR** org match.

**Request body:** none (uses stored `CustomDomain` + `DomainVerificationToken`).

**Behavior (`IDomainVerificationService`):**
1. Require `PublicHostMode == CustomDomain` and non-null `CustomDomain` + token; else **400**.
2. DNS TXT lookup for `_casazen-challenge.{CustomDomain}` (and/or host `_casazen-challenge` at apex — document primary as `_casazen-challenge.{domain}`).
3. Timeout **5s** (`PublicHost:DnsLookupTimeoutSeconds`).
4. If any TXT record equals token → `Verified`; else `Failed`.
5. Persist `DomainVerificationStatus`; return result.

**Response `200` — `OrgDomainVerifyResultDto`:**

| Field | Type |
|---|---|
| `domainVerificationStatus` | `Verified` \| `Failed` |
| `customDomain` | `string` |
| `checkedAt` | `DateTime` (UTC) |
| `message` | `string?` | Italian user-facing hint on failure |

**Errors:** `400`, `401`, `403`, `404`.

---

### Entitlement extension

Add to `IEntitlementService`:

```csharp
Task<bool> CanUseCustomDomainAsync(Guid orgId, CancellationToken cancellationToken = default);
```

Implementation: effective `PlanTier` is `Pro` or `Scale` (reuse `ResolveEffectiveTier` past-due → Starter logic). Gate **AC3/AC6**.

Optionally extend `EntitlementDto` / `GET /api/orgs/me/entitlement` with `canUseCustomDomain` for FE disable/upgrade CTA (recommended, non-breaking additive field).

---

## Frontend Flow

### New / Modified Routes

| Path | Component | Auth | ProtectedRoute | Notes |
|---|---|---|---|---|
| `/settings/domain` | `CustomDomainSettingsPage` | Host org member | **Yes** — `<ProtectedRoute>` inside AppShell | Primary domain config UI for #298 |
| `/book/:slug/*` | `PublicSiteShell` | public | No | Unchanged path mode; middleware may rewrite custom/subdomain Host → this path |
| `/onboarding` (or existing PLG route) | `DomainChoiceStep` stub | Host | **Yes** — `<ProtectedRoute>` | Thin stub: link/CTA to `/settings/domain`; full wizard absorbed by #271 later |

**Public (no ProtectedRoute):** guest traffic on custom domain / subdomain is rewritten to public booking routes — no auth.

### Edge middleware (AC7)

**File:** `frontend/middleware.ts` (Vercel Edge)

1. Read `Host` from request.
2. Skip middleware for apex marketing hosts (`casazen.it`, `www.casazen.it`, `*.vercel.app` app host) and static/API proxy paths.
3. Call `GET {API}/api/public/resolve-host?host={host}` (server-side fetch, short timeout).
4. On 200: rewrite internally to `/book/{slug}` (preserve path/query) and set request headers `X-Tenant-OrgId`, `X-Tenant-Slug`, `X-Public-Host-Mode` for SSR/shell.
5. On 404: serve branded/generic 404 page (no open redirect).
6. **Fallback (Hobby / no Edge):** client-side in `PublicSiteShell` / booking shell — `window.location.hostname` → resolve-host → load tenant; document as degraded path.

### Component Plan

| Component | Status | Location | Responsibility |
|---|---|---|---|
| `middleware.ts` | new | `frontend/middleware.ts` | Host → resolve-host → rewrite to PublicSiteShell |
| `CustomDomainSettingsPage` | new | `src/features/settings/domain/` | Mode picker, subdomain field, custom domain + DNS panel + Verify CTA |
| `DnsInstructionsPanel` | new | `src/features/settings/domain/` | CNAME + TXT copy blocks; SSL note |
| `DomainStatusBadge` | new | `src/features/settings/domain/` | Pending / Verified / Failed |
| `DomainChoiceStep` | stub | `src/features/onboarding/` | Link to settings; #271 owns full step later |
| `PublicSiteShell` | modify | `src/layouts/PublicSiteShell.tsx` | Read tenant headers / client fallback; hide CasaZen chrome when CustomDomain + Pro (`showPoweredBy`) |
| `domain.api.ts` | new | `src/api/domain.api.ts` | get/set/verify domain |
| `use-org-domain.ts` | new | `src/hooks/use-org-domain.ts` | TanStack Query hooks |

### State & API

| Data | Hook | API module | Notes |
|---|---|---|---|
| domain config | `useOrgDomain(orgId)` | `domain.api.ts` | GET domain |
| set domain | `useSetOrgDomain` | `domain.api.ts` | POST domain; invalidate cache |
| verify domain | `useVerifyOrgDomain` | `domain.api.ts` | POST verify |
| entitlement | `useEntitlement` (existing) | orgs API | gate CustomDomain UI + upgrade CTA |

### UX notes (Italian UI copy)

- Starter selecting CustomDomain → disable + “Passa a Pro” CTA.
- After set: show CNAME target + TXT value with copy buttons.
- Verify button polls once on click; show Failed with retry guidance.
- Preview links for path / subdomain / custom when Verified.

---

## Security Notes

**Auth gates:**
| Endpoint | Auth | Justification |
|---|---|---|
| `GET /api/public/resolve-host` | `[AllowAnonymous]` | Edge tenant resolution; branding-only payload |
| `GET/POST /api/orgs/{orgId}/domain` | `[Authorize]` + org IDOR | Owner/member of same org only |
| `POST /api/orgs/{orgId}/domain/verify` | `[Authorize]` + org IDOR | Same |

**IDOR risk:** Present on all `/api/orgs/{orgId}/domain*` — **mitigation:** reject when `{orgId} !=` resolved caller org id (403). Do not trust client-supplied org id alone.

**Host-header allowlist (AC12/AC13):**
- Resolve only registered verified custom domains or valid non-reserved `*.{BaseDomain}` labels matching DB.
- Unknown hosts → 404 (no default tenant hijack).
- Subdomain labels restricted to `[a-z0-9-]`; reserved list from `PublicHostOptions.ReservedSubdomains`.
- Middleware must not redirect to attacker-controlled URLs; rewrite is internal only.

**Rate limit:** `PublicResolveHost` 60/min/IP — mitigates host enumeration / DoS.

**Secrets:**
- `DomainVerificationToken` generated server-side; never logged in full; returned only to authenticated owner in DNS instructions.
- No OTA keys in this feature. Config keys: `PublicHost:BaseDomain`, `PublicHost:ReservedSubdomains`, `PublicHost:VercelCnameTarget`, `PublicHost:RateLimitPermitLimit`, `PublicHost:DnsLookupTimeoutSeconds`, `PublicHost:ResolveCacheSeconds`.

**Stripe:** N/A — no webhook changes. Plan gate reads existing `Org.PlanTier` / subscription effective tier via `IEntitlementService`.

**PII exposure risk:**
- resolve-host must **not** return Guest data, owner Auth0 ids, Stripe customer/connect ids, VAT, or verification token.
- Branding limited to public marketing fields; exclude `ContactEmail` from resolve-host DTO.
- DNS verify errors must not echo raw DNS server dumps with unrelated records containing emails.

**STRIDE (brief):**
| Threat | Surface | Mitigation |
|---|---|---|
| Spoofing | Host header | DB allowlist + Verified-only custom domains |
| Tampering | Domain claim | Auth + IDOR + unique indexes; TXT ownership proof |
| Information disclosure | resolve-host | Branding-only; rate limit |
| Denial of service | resolve-host | 60/min IP + 60s cache |
| Elevation | CustomDomain on Starter | `CanUseCustomDomain` → 403 |

---

## Migration Plan

**Migration name:** `AddOrgCustomDomain`

| Change | Detail |
|---|---|
| New columns | `Orgs.PublicHostMode` (int, default 1 = CasazenPath), `Orgs.CustomDomain` (varchar 253 null), `Orgs.DomainVerificationStatus` (int, default 0), `Orgs.DomainVerificationToken` (varchar 128 null), `Orgs.Subdomain` (varchar 63 null) |
| New enum type | `DomainVerificationStatus` in Core (EF stores as int) |
| Existing enum | `PublicHostMode` already in Core — map column only |
| Indexes | Filtered unique on `CustomDomain`, `Subdomain` |
| Data | No destructive change; defaults safe for all existing rows |

**EF:** update `Org` entity, `AppDbContext` fluent config for indexes/max lengths, register `IDomainVerificationService` + extend `IPublicHostResolver` / DI in `ServiceCollectionExtensions`.

**Ops:** after deploy to test/prod, run EF migrate (`scripts/migrate.ps1 -Target test` then prod when promoting). Document Vercel wildcard `*.casazen.it` + per-org Custom Domain add in `docs/INFRA.md` (AC10/AC11).

**Beta host (AC12):** configure ≥1 staging + prod org with verified custom domain as release validation (manual ops checklist; not unit-gated).

---

## GDPR Scope

**N/A — no Guest personal data in scope.**

Domain configuration stores org-level DNS metadata (`CustomDomain`, verification token/status). No Guest name/DOB/document/nationality fields are read or written. No new `ErasureRequested` / `DataRetentionUntil` hooks required. resolve-host responses exclude personal data beyond public marketing branding.

---

## Services

| Service | Status | Responsibility |
|---|---|---|
| `IPublicHostResolver` / `PublicHostResolver` | modify | Custom domain (Verified) + subdomain resolve; cache |
| `IDomainVerificationService` / `DomainVerificationService` | new | DNS TXT lookup (5s timeout), status update |
| `IOrgDomainService` / `OrgDomainService` | new (optional façade) | Set/get domain, token generation, conflict checks, entitlement gate |
| `IEntitlementService` | modify | `CanUseCustomDomainAsync` |
| `OrgDomainController` | new | Owner domain endpoints under `api/orgs/{orgId}/domain` |
| `PublicHostController` | modify | Rate-limit attribute; extended DTO; no route change |

**DNS library:** use `DnsClient` NuGet or `System.Net.Dns` with care; prefer injectable `IDnsTxtLookup` for unit tests (mock records).

---

## Config

| Key | Default | Purpose |
|---|---|---|
| `PublicHost:BaseDomain` | `casazen.it` | Subdomain suffix |
| `PublicHost:ReservedSubdomains` | www, api, app, admin, staging, test, mail | Blocklist |
| `PublicHost:VercelCnameTarget` | `cname.vercel-dns.com` | DNS CNAME instruction |
| `PublicHost:RateLimitPermitLimit` | `60` | resolve-host / IP / min |
| `PublicHost:DnsLookupTimeoutSeconds` | `5` | verify timeout |
| `PublicHost:ResolveCacheSeconds` | `60` | resolve-host cache TTL |
| `PublicHost:TxtRecordPrefix` | `_casazen-challenge` | TXT host label |

Wire rate limiter in `Program.cs` alongside existing public limiters; apply `[EnableRateLimiting("PublicResolveHost")]` on resolve-host action.

---

## Tests plan

### Unit (AC14)

| Test | Scenarios |
|---|---|
| `PublicHostResolverTests` | (1) verified custom domain → org; (2) subdomain → org; (3) unverified custom domain → null; (4) unknown host → null; plus reserved subdomain |
| `DomainVerificationServiceTests` | TXT match → Verified; mismatch → Failed; timeout → Failed |
| `OrgDomainServiceTests` / controller unit | Starter CustomDomain → 403; Pro success; unique conflict 409 |
| `EntitlementServiceTests` | `CanUseCustomDomain` true for Pro/Scale, false for Starter / past-due effective Starter |

### Integration (AC15)

| Scenario | Expect |
|---|---|
| resolve-host returns branding for verified custom + subdomain | 200 + slim branding, no token |
| set domain requires Pro | Starter → 403 |
| verify domain success | TXT mock → Verified |
| cross-org domain conflict | 409 when CustomDomain taken |
| IDOR | user A cannot POST `/api/orgs/{B}/domain` → 403 |

### Frontend / quality gates (AC16)

- `dotnet test` + `dotnet format --verify-no-changes`
- Frontend: `npm run build` + `tsc`
- E2E (demo): settings domain panel happy path mocked; middleware contract test with mocked resolve-host (extend branded-booking helpers). Full live custom domain SSL remains staging/prod manual (AC12).

---

## Out of scope

- Multi-domain per org
- Wildcard custom DNS for suppliers (Fase 2)
- Cloudflare for SaaS (Vercel only — per ADR-001)
- Static site export
- Blocking #298 on #271 full onboarding wizard (stub only)
- Changing path-mode public routes (`/book/{slug}`) contract beyond shell tenant injection

---

## Open Questions

(none — resolved)

| Question | Resolution |
|---|---|
| Block on #271? | No — settings panel ships in #298; onboarding step is stub/link |
| Duplicate resolve endpoint? | No — extend existing `GET /api/public/resolve-host` |
| Include `planTier` in resolve-host? | Yes per issue AC2; not PII; still exclude ContactEmail/token |
| Subdomain vs Slug? | Add `Org.Subdomain`; resolve prefers Subdomain, falls back to Slug |
| CNAME target | `cname.vercel-dns.com` via config; SSL via Vercel Custom Domains (INFRA) |

---

## Handoff table

| Item | Value |
|---|---|
| Issue | #298 |
| Design file | `Sessions/design-298.md` |
| Branch to create | `feature/298-custom-domain-booking` |
| Migration | `AddOrgCustomDomain` |
| Backend touchpoints | `Org.cs`, `PublicHostResolver`, `PublicHostController`, new `OrgDomainController`, `DomainVerificationService`, `IEntitlementService`, `AppDbContext`, rate limiter, tests |
| Frontend touchpoints | `middleware.ts`, `CustomDomainSettingsPage`, domain API/hooks, PublicSiteShell tenant headers, onboarding stub |
| Docs | `docs/INFRA.md` — Vercel Custom Domains + `*.casazen.it` wildcard + SSL |
| Stage 03 entry | All harness G1–G8 pass; implement BE+FE per this spec |
