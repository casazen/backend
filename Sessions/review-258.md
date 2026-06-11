# Review — Issue #258 Programmatic Compliance SEO (US-020)

> **Stage 04 — Review** · Date: 2026-06-11 · Iteration 1/3  
> **PRs**: BE [#261](https://github.com/casazen/backend/pull/261) · FE [#138](https://github.com/casazen/frontend/pull/138)  
> **Design spec**: `Sessions/design-258.md` **not found** — reviewed against GitHub Issue #258 ACs and PR diffs.

## Verdict

**REQUEST CHANGES (process)** — **0 critical (🔴) findings**; G2 passes. Stage 04 exit blocked on **G1** (backend merge conflict) and **G3** (open high findings below — resolve or defer with tracking issues before Stage 05).

---

PR #261 / #138 Review — Iteration 1/3

### 🔴 Critical (must fix)

_None._

### 🟡 High (resolve or defer)

1. **[backend PR merge] G1 — PR #261 not mergeable**  
   `gh pr view 261 --json mergeable` → `CONFLICTING` / `mergeStateStatus: DIRTY`. Rebase onto `develop` and resolve conflicts before merge.

2. **[compliance-guide-page.tsx:53, tourist-tax-calculator-page.tsx] AC11 — CSR instead of SSR/ISR**  
   Public SEO routes fetch JSON client-side and inject meta tags via `useSeoMeta` after hydration. Crawlers that do not execute JS will see an empty shell — undermines the organic-traffic goal. Defer to a follow-up (Vercel SSR/ISR or prerender) or accept explicit MVP scope downgrade in the issue.

3. **[compliance-guide-page.tsx:53] OWASP A03 — unsanitized AI HTML (`dangerouslySetInnerHTML`)**  
   `bodyHtml` from LLM generation is rendered without sanitization (DOMPurify or server-side allowlist). Prompt injection or model output could inject script tags on public pages. Sanitize before render or on persist.

4. **[SeoContentService.cs:70] Tourist tax calc ignores children and `MinimumAge`**  
   `CalculateTouristTaxAsync` multiplies `NumberOfAdults × nights × rate` only; `NumberOfChildren` is echoed but not applied, and `TouristTaxRate.MinimumAge` is unused. Widget collects bambini — users may get incorrect estimates vs municipal rules.

5. **[seo-dashboard-page.tsx] AC7 ops — no counsel-review UI**  
   Backend enforces `[COUNSEL_REQUIRED]` via `PATCH /api/admin/seo/pages/{id}/review-status` (`counselApproved` flag). FE exposes `useUpdateSeoReviewStatus` but dashboard has no action to mark Draft → Reviewed or record counsel approval — first-100-pages legal gate is API-only.

6. **[PublicContentController.cs] No rate limit on public calculate endpoint**  
   `/api/public/tourist-tax/calculate` is anonymous with no `[EnableRateLimiting]`. Low-risk abuse vector; align with existing public booking/check-in limiters.

### 🟢 Medium

1. **[ItalianComuneRegistry.cs] Phase-1 hardcoded comune list (4 cities)** — acceptable MVP; document expansion path.
2. **[seo-dashboard-page.tsx:413] Rigenera hardcodes `comuneCodes: ['013075']`** — fine for demo/E2E; generalize when registry expands.
3. **[SeoContentService.cs:461] Budget reset uses calendar month of `LastResetAt`** — edge case on first-run mid-month; non-blocking.
4. **Sitemap served from API host** (`/sitemap-compliance.xml` on Railway) — ensure prod routing exposes it on `www.casazen.it` or submit via Search Console on API domain.

### ⚪ Low

1. Integration test `AC2_ComplianceGuide_Returns404_WhenNotReviewed_InTestingEnvAllowsDraft` name mismatches behavior (expects 200 in Testing) — rename for clarity.
2. `StubAiProvider` static in-memory cache — dev/test only; document production `IAiProvider` wiring before scale.

---

## Council findings (deduplicated)

### Code reviewer

| Sev | Finding |
|---|---|
| 🟡 | CSR vs SSR/ISR (AC11) |
| 🟡 | Tax calc logic incomplete (children / minimum age) |
| 🟡 | Admin counsel workflow UI missing |
| 🟢 | Hardcoded comune registry + regenerate scope |
| ⚪ | Test naming |

### Security auditor

| Sev | Finding |
|---|---|
| 🟡 | Stored XSS risk on AI `bodyHtml` without sanitization |
| 🟡 | Public calculate endpoint lacks rate limiting |
| ✅ | Admin routes: `[Authorize(Policy = "AdminOnly")]` on `AdminSeoController` |
| ✅ | Public routes: explicit `[AllowAnonymous]` with documented justification |
| ✅ | No IDOR surface (platform-scoped SEO entities, no owner/user binding) |
| ✅ | No raw SQL in Infrastructure diff |
| ✅ | No Guest PII in responses/logs |
| ✅ | Stripe webhook not modified (G8 N/A) |
| ✅ | GDPR: public pages collect no PII |
| ✅ | FE admin SEO under `/app/admin/seo` → `ProtectedRoute` + `ContextRouteGuard` (`admin.seo.read`) |
| ✅ | AI disclaimers present (AC7 / AI Act notice) |
| ✅ | No “100% compliant” / “nessun rischio” claims in copy |

---

## AC coverage

| AC | Status | Evidence |
|---|---|---|
| AC1 Entities | ✅ | `SeoContentPage`, `SeoContentRevision`, `PlatformAiBudget` + EF migration |
| AC2 Compliance guide API | ✅ | `GET /api/public/content/affitti-brevi/{region}/{comune}`; Draft allowed staging/dev/test only |
| AC3 Tourist tax page API | ✅ | `GET /api/public/content/tassa-soggiorno/{comune}` |
| AC4 Admin generate | ✅ | `POST /api/admin/seo/generate` → Hangfire `SeoPageGenerationJob` |
| AC5 Generation job | ✅ | Per-page try/catch; Economy tier only |
| AC6 Refresh job | ✅ | `SeoContentRefreshJob` cron `0 4 1 * *` |
| AC7 Legal disclaimers + counsel gate | ✅ BE / 🟡 FE | Disclaimers in DTO + footer; `CounselRequired` + 403 without `counselApproved`; no admin UI |
| AC8 Sitemap | ✅ | `GET /sitemap-compliance.xml` lists Reviewed pages |
| AC9 AI budget | ✅ | `PlatformAiBudget.MonthlyTokenCap`; stop batch; cache hit = 0 tokens |
| AC10 CTA | ✅ | `SeoCtaDto` + `SeoCtaBlock` |
| AC11 Public FE route | 🟡 | `/p/affitti-brevi/:region/:comune` — CSR not SSR/ISR |
| AC12 Tax widget | 🟡 | API-backed calc, no LLM at runtime; children/age logic incomplete |
| AC13 Admin dashboard | ✅ | Lists status/comune/refresh; Rigenera triggers generate |
| AC14 Supplier microsite deferred | ✅ | Enum + log stub; filtered from generation |

---

## Cross-repo API contract (G4)

| FE call | BE route | Match |
|---|---|---|
| `PublicSeoApi.getComplianceGuide` | `GET /api/public/content/affitti-brevi/{region}/{comune}` | ✅ |
| `PublicSeoApi.getTouristTaxPage` | `GET /api/public/content/tassa-soggiorno/{comune}` | ✅ |
| `PublicSeoApi.calculateTouristTax` | `POST /api/public/tourist-tax/calculate` | ✅ |
| `AdminSeoApi.listPages` | `GET /api/admin/seo/pages` | ✅ |
| `AdminSeoApi.generatePages` | `POST /api/admin/seo/generate` | ✅ |
| `AdminSeoApi.updateReviewStatus` | `PATCH /api/admin/seo/pages/{id}/review-status` | ✅ |
| `AdminSeoApi.getBudget` | `GET /api/admin/seo/budget` | ✅ |

---

## Test evidence

| Suite | Result |
|---|---|
| BE `SeoContentServiceTests` + `ComplianceSeoIntegrationTests` | **10/10 passed** (local run 2026-06-11) |
| FE E2E `compliance-seo.spec.ts` | **CI green** on PR #138 (`e2e` check SUCCESS) |
| FE Vitest `seo-disclaimer-footer.test.tsx` | Present (AC7 disclaimer lines) |

---

## Gate Status

| Gate | Status | Notes |
|---|---|---|
| G1 PR(s) mergeable | ❌ | BE #261 `CONFLICTING`; FE #138 `MERGEABLE` |
| G2 No critical findings | ✅ | **0 open 🔴** |
| G3 High findings addressed | ❌ | 5 open 🟡 — resolve or defer with issues |
| G4 Cross-repo consistency | ✅ | FE API paths match BE contract |
| G5 No IDOR | ✅ | N/A — platform SEO entities |
| G6 No raw SQL | ✅ | No `FromSqlRaw` / `ExecuteSqlRaw` in diff |
| G7 PII not exposed | ✅ | Public SEO responses contain no guest PII |
| G8 Stripe signature | ✅ | N/A — webhook not modified |
| G9 GDPR guest fields | ✅ | N/A — no new guest creation flows |
| G10 Frontend auth routes | ✅ | Admin SEO under `ProtectedRoute` + permission guard |

**G2 pass condition met (0 🔴).** Full Stage 04 exit requires G1 + G3 resolution.

---

## Handoff

- **If fixing in Stage 03**: prioritize G1 rebase, then 🟡 items #3 (sanitize HTML) and #4 (tax calc) before production SEO index.
- **If deferring 🟡**: create tracking issues for SSR/ISR, DOMPurify, counsel UI, rate limit.
- **Stage 05 entry**: both PRs mergeable, G3 cleared or deferred with issue links.
