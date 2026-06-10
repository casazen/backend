# Domain Context: CasaZen Platform Launch

> AI-powered direct booking & rental OS for the Italian/European short-term and long-term rental market. Council mission: business plan, implementation roadmap, and macro-specs from current codebase to sellable production platform.

---

## overview

CasaZen is evolving from a vacation-rental PMS (Italian STR compliance, OTA sync, Stripe, AI pricing) into an **AI-powered direct booking & rental OS** — democratic SaaS from 1 to 500 units, subscription-first, AI copilot across the full rental lifecycle, strong direct booking focus.

**Primary market reference**: `Sessions/market-analysis-2026/AI-short/long-term-platform.md` ("Piattaforma AI per affitti brevi/lunghi e direct booking").

**Council deliverables**:
1. **Business plan** — positioning, segments, pricing, GTM, unit economics, 5-year vision
2. **Implementation roadmap** — phased path from current implementation to sellable production platform
3. **Macro-specs** — in `Sessions/specs/`, each anchored to current codebase gaps and roadmap phases

**Constraints**: operate legally in Italy; maximize lawful cost minimization (grants, tax incentives, freemium tiers, phased licensing); no illegal shortcuts.

---

## stakeholders

| Stakeholder | Role / Interest | Authority | Notes |
|-------------|-----------------|-----------|-------|
| Founder / operator | Launch platform, minimize burn, reach revenue | Decision | Zero/low budget preference per `Sessions/decision-hosting-zero-budget.md` |
| Property managers (10–200 units) | MVP wedge — direct booking, unified inbox, AI copilot | Customer | Primary GTM segment per market analysis |
| Hosts (1–9 units) | Phase 2 — simplified onboarding, freemium | Customer | Secondary segment |
| Italian authorities | CIN, Alloggiati Web, tourist tax, business registration | Regulatory | Non-negotiable compliance |
| OTA platforms | Channel distribution + API dependencies | Partner / constraint | Airbnb, Booking.com, etc. |
| CasaZen dev (AI-SDLC) | Implements macro-specs via existing 6-stage pipeline | Execution | Specs land in `Sessions/specs/` |

---

## market-landscape

**Source**: `Sessions/market-analysis-2026/AI-short/long-term-platform.md`

| Signal | Data point |
|--------|------------|
| STR market size | ~$101.7B (2025) → $121.9B (2033), CAGR 3.7% |
| Vacation rental software | ~$1.5B (2024) → $3.2B (2033), CAGR ~9.2% |
| Direct booking share | ~29% direct vs 71% OTA (15–20% commission) |
| Last-minute bookings | 27% within 0–7 days (2026) |
| Competitive gap | No "democratic" OS from 1–500 units with native STR+LTR + AI copilot + direct booking |

**Positioning**: "AI-powered direct booking & rental OS" — subscription per unit/portfolio, transparent pricing, no hidden booking commissions.

**MVP wedge**: property managers 10–200 units, Italy/Spain/France, direct booking + unified inbox + AI operational copilot.

**Product tiers (from analysis)**: Starter (1–3 units) → Pro (4–100) → Scale/Enterprise (50–500+).

---

## regulatory-environment

### Platform product compliance (what CasaZen must enforce for customers)

| Regulation | Requirement | Current CasaZen status |
|------------|-------------|------------------------|
| D.L. 145/2023 (CIN) | Property CIN `IT-XXXXX-XXXXXXXXXX` | Implemented — `CinCodeAttribute` |
| D.L. 286/1998 Art.7 (Alloggiati Web) | Guest police report within 24h of check-in | Implemented — background job |
| GDPR | Consent, retention (7y default), erasure Art.17 | Implemented — `GdprService`, retention job |
| Tourist tax | Municipality rates, never hardcoded | Implemented — `TouristTaxRate` entity |
| Stripe / PSD2 | Payment processing, webhook verification | Implemented |

### Company / operator compliance (launching the SaaS in Italy)

| Area | Typical requirements | Cost-minimization levers (lawful) |
|------|---------------------|-----------------------------------|
| Legal form | SRL / SRLS / individual + Partita IVA | SRLS (€1 capital), forfettario if eligible |
| Tax regime | IVA, imposta di bollo SaaS invoices | Regime forfettario (if under thresholds), reverse charge B2B EU |
| Data protection | GDPR DPA, privacy policy, DPO if required | Template + self-assessment; DPO only if mandatory |
| Software / AI | EU AI Act (limited risk for operational AI), transparency | Document AI decisions (pricing confidence already logged) |
| STR operator obligations | If CasaZen operates own listings — full compliance | **Avoid** — pure SaaS B2B reduces operator burden |
| Grants / incentives | Invitalia, regional innovation funds, PNRR digitalization | Apply where eligible; document use-of-funds |

**Disclaimer**: council output is strategic guidance, not legal advice. External counsel required before company formation and ToS launch.

---

## financial-context

| Item | Current state | Notes |
|------|---------------|-------|
| Hosting | $0 tier possible | `Sessions/decision-hosting-zero-budget.md` — Render/Railway Free + GH Actions cron |
| Upgrade trigger | ~$5/mo Railway Hobby | When Hangfire reliability needed for prod |
| ARPU benchmark | €150–400/mo per account (10–100 units) | From market analysis vs Lodgify/Guesty |
| Revenue mix target | 70–80% subscription, 10–20% marketplace, rest services | Per market analysis |
| Burn constraint | Minimize until first paying customers | Freemium infra, phased feature rollout |

**Key metrics for business plan**: CAC, LTV, payback, gross margin (cloud + AI API costs), months to breakeven.

---

## operational-context

- **Implementation pipeline**: AI-SDLC (`.claude/sdlc/`) consumes macro-specs from `Sessions/specs/`
- **Existing macro-specs**: property-detail, admin-backend, pricing-adapter-verification, role-onboarding, split-layer
- **Current release cadence**: GitHub Flow — feature → develop → staging → main
- **Infra**: Supabase (PostgreSQL), Vercel (FE), Railway/Render (BE)

---

## documents-index

See `Docs/INDEX.md`. Priority reads for this council:

- `Sessions/market-analysis-2026/AI-short/long-term-platform.md` — **mandatory**
- `docs/BUSINESS.md`, `docs/PROJECT.md`, `docs/INFRA.md`
- `Sessions/decision-hosting-zero-budget.md`
- `Sessions/specs/spec-*.md` — existing spec format to follow

---

## services

| Service | Port | Schema | Key Components |
|---------|------|--------|----------------|
| Casazen.Web (API) | 5001 | PostgreSQL (Supabase) | 12 controllers, Hangfire jobs, Stripe webhooks |
| React SPA (frontend) | 5173 / Vercel | — | Feature-slice, TanStack Query, Auth0 |
| Background jobs | in-process Hangfire | — | OTA sync, Alloggiati Web, GDPR, pricing, email |

---

## tech-stack

- **Backend**: C# 13 / .NET 10, ASP.NET Core, EF Core 10, PostgreSQL
- **Frontend**: React 19, TypeScript, Vite, TanStack Query, Zustand
- **Auth**: Auth0 JWT
- **Payments**: Stripe
- **Email**: SendGrid
- **OTA**: 6 adapters with Polly resilience
- **CI/CD**: GitHub Actions, Docker

---

## bounded-context-pattern

Layered backend: `Casazen.Core` (entities, interfaces) → `Casazen.Infrastructure` (repos, services, OTA, external) → `Casazen.Web` (controllers, DTOs, jobs).

Frontend: feature-slice under `src/features/`, shared components, API layer.

---

## cross-context-integration

- OTA adapters ↔ Booking/Property entities
- Stripe webhooks ↔ Payment/Booking
- Alloggiati Web ↔ Guest/Booking (async job on check-in)
- Pricing adapter ↔ Property nightly rates
- Auth0 JWT ↔ all `/api` endpoints

**Roadmap gaps** (from market analysis vs current state): direct booking website builder, unified inbox, LTR contracts, marketplace/suppliers, AI copilot messaging, Google Vacation Rentals integration.

---

## docker-infrastructure

Multi-stage Dockerfile, docker-compose for local dev. Production: Railway/Render + Supabase + Vercel per `docs/INFRA.md`.

---

## testing-landscape

xUnit (backend), Vitest/RTL (frontend). Coverage targets: critical paths 100%, services 80%, controllers 70%. Existing integration tests for API, migrations, OTA resilience.
