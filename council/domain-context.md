# Domain Context: CasaZen AI-SDLC Design

> Vacation rental property management platform for the Italian market. Full-stack: ASP.NET Core 10 REST API + React 19 SPA. The council's mission is to design a 6-stage AI-SDLC with quality harness loops for both stacks.

---

## overview

CasaZen is a vacation-rental property management platform targeting Italian short-term rental operators. It enables property managers to list properties, manage bookings, process payments via Stripe, sync inventory across 6 OTA channels (Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda), and comply with Italian law.

The council is designing the **AI-SDLC**: the full development lifecycle governed by AI agent councils, one per stage, each with a quality harness loop that enforces stage-specific gates before the next stage can start.

**Target outcome**: a `.claude/sdlc/` folder with 6 stage directories, each containing a harness (quality loop), a stage coordinator, and specialist agents for that stage's work.

---

## stakeholders

| Stakeholder | Role / Interest | Authority | Notes |
|---|---|---|---|
| Property owners / managers | Primary users: manage properties, bookings, compliance | Informed | Italian market; UI must be in Italian |
| Guests | Book via OTAs or direct | Informed | Identity data subject to GDPR + Alloggiati Web |
| Italian authorities | CIN registry, police reporting, tourist tax collection | Regulatory | D.L.145/2023, D.L.286/1998 Art.7 |
| CasaZen dev team | Build and maintain the platform | Decision | Both repos: casazen/backend, casazen/frontend |

---

## regulatory-environment

**Italian rental compliance — embedded in harness quality gates, not a standalone agent:**

| Regulation | Requirement | Stage where enforced |
|---|---|---|
| D.L. 145/2023 (CIN) | Every property must have a CIN code `IT-XXXXX-XXXXXXXXXX`. Validated by `[CinCode]` attribute. | Development (gate), Review (audit) |
| D.L. 286/1998 Art.7 (Alloggiati Web) | Guest identity submitted to Italian police within 24h of check-in. Handled via background job, must respond in <3s. | Development (gate), Review (audit) |
| GDPR Art.17 | Guest data erasure on request. 7-year default retention. `GdprDataRetentionJob` handles automatic anonymisation. | Development (gate), Review (audit) |
| Tourist tax (tassa di soggiorno) | Rates vary by municipality. Stored in `TouristTaxRate` entity. Never hardcode. | Development (gate) |
| GDPR general | Personal data: consent recorded, retention enforced, exports available | Development (gate) |

**Key rule**: when a harness quality gate checks compliance, it must verify these specific items, not generic "GDPR compliance" boilerplate.

---

## tech-stack

**Backend (casazen/backend):**
- C# 13 / .NET 10 / ASP.NET Core Web API
- SQL Server 2022 + EF Core 10 (code-first, migrations in `Casazen.Infrastructure/Migrations/`)
- Auth0 JWT Bearer on all `/api` endpoints except `/health` and `/properties/search`
- Hangfire (background jobs: OTA sync, Alloggiati Web, GDPR retention, email queue, pricing)
- Stripe .NET SDK (webhook signature verification mandatory)
- SendGrid (template IDs only — no inline HTML)
- Polly (retry + circuit-breaker + rate-limit per OTA platform)
- xUnit (unit + integration tests); `dotnet test` + `dotnet format --verify-no-changes` before any PR

**Frontend (casazen/frontend):**
- TypeScript 5.9 + React 19.2 + Vite 8
- Tailwind CSS v4 + Radix UI primitives
- React Router v7 (all routes except `/login` and `/search` protected via `<ProtectedRoute>`)
- TanStack Query v5 (server state); Zustand v5 (UI + user global state)
- React Hook Form v7 + Zod v4 (form validation)
- Axios v1 with JWT Bearer interceptor (`src/lib/axios.ts`)
- Auth0 (`@auth0/auth0-react`); demo mode: `VITE_DEMO_MODE=true`
- Vitest v4 (unit); Playwright v1.60 (E2E)

---

## services

| Service | Port | Schema | Key Components |
|---|---|---|---|
| CasaZen API | 5001 (HTTPS) | SQL Server `casazen_db` | 12 controllers, 7 Hangfire jobs, 6 OTA adapters |
| CasaZen Frontend SPA | 5173 (dev) | — | 22 routes, 9 API modules, 5 TanStack Query files |
| SQL Server | 1433 | `casazen_db` | 14 EF Core entities |
| Hangfire Dashboard | `/hangfire` | — | 7 recurring + on-demand jobs |

---

## bounded-context-pattern

**Backend — layered architecture:**
```
Casazen.Core/          # Domain: entities, interfaces, validators (no external deps)
Casazen.Infrastructure/ # Data + external: EF Core, OTA adapters, Stripe, SendGrid, Alloggiati
Casazen.Web/           # Presentation: controllers, DTOs, Hangfire jobs, middleware
Casazen.Tests/         # Unit/ + Integration/
```

**Frontend — feature-slice architecture:**
```
src/api/        # Per-domain API modules (one per backend controller group)
src/features/   # Feature slices: pages + components + Zod schemas
src/queries/    # TanStack Query hooks (use-*.ts)
src/types/      # TypeScript interfaces, re-exported from index.ts
src/store/      # Zustand: ui-store, user-store
src/components/ # auth/, layout/, shared/, ui/ (Radix)
src/config/     # env.config.ts, auth.config.ts, api.config.ts, demo.config.ts
```

**Key naming rules**: `PascalCase` classes; `I` prefix interfaces; `*Controller`, `*Service`, `*Repository` suffixes (backend). `camelCase` files; `use-` prefix hooks; `*.api.ts` pattern for API modules (frontend).

---

## cross-context-integration

- Frontend ↔ Backend: REST over HTTPS. Axios intercepts JWT on every request. `ApiClient.unwrap()` handles both bare `T` and `{ data: T }` envelope.
- Backend ↔ Stripe: webhook at `/api/webhooks/stripe`. Signature verification via `StripeWebhookHandler`. Long ops offloaded to Hangfire.
- Backend ↔ OTA: `IOtaAdapter` implementations in `Casazen.Infrastructure/OTA/`. Polly policies per platform. Circuit breaker opens after 5 failures, stays open 60s.
- Backend ↔ Alloggiati Web: `AlloggiatiWebService` custom HTTP client. Must respond in <3s — always queue to Hangfire.
- Backend ↔ Auth0: JWT Bearer middleware validates `sub` claim as user ID.

---

## docker-infrastructure

- `Dockerfile`: multi-stage build (SDK image → runtime image)
- Local DB: PostgreSQL 16 / Supabase (see `docs/INFRA.md`)
- CI/CD: `.github/workflows/ci-cd.yml` (build + test on push; deploy on release tag)

---

## testing-landscape

**Backend:**
- Framework: xUnit (unit + integration)
- Location: `Casazen.Tests/Unit/` + `Casazen.Tests/Integration/`
- Coverage targets: critical paths 100%, services 80%, controllers 70%
- Pre-commit gate: `dotnet test` + `dotnet format --verify-no-changes`
- In-memory SQL Server in CI (when no connection string available)

**Frontend:**
- Unit/component: Vitest v4 + Testing Library (`@testing-library/react`) with `jsdom`
- E2E: Playwright v1.60
- Test locations: `src/**/__tests__/` (unit); `e2e/` (E2E)
- Pre-PR gate: `npm test` + `npm run lint`

**Non-obvious gotchas:**
- Never `.Result`/`.Wait()` in async .NET code — deadlock risk
- `DbContext` is scoped per request — never singleton or static
- OTA webhooks must respond in <3s — always queue long work
- `DateTime.UtcNow` internally; convert to local only for display
- Tourist tax rates from `TouristTaxRate` entity only — never hardcode
- Frontend: `VITE_API_BASE_URL` defaults to port 3000, backend runs on 5001 — must set explicitly

---

## documents-index

| Document | Summary | Relevant to |
|---|---|---|
| `docs/BUSINESS.md` | Domain entities (14), business processes (booking flow, check-in, OTA sync, payment), Italian regulatory glossary | All agents needing domain context |
| `docs/TECHNICAL.md` | Backend architecture diagram, API reference (all 12 controllers), EF Core ER diagram, design patterns, Hangfire job schedule | Architect, Security, DevOps agents |
| `docs/PROJECT.md` | Backend AI context: conventions, where-to-find-things table, 12 gotchas | All backend-facing agents |
| `docs/FRONTEND-TECHNICAL.md` | Frontend architecture, routing table (22 routes), API layer, TanStack Query hooks, Auth0 flow, test setup | Architect, DevOps agents |
| `docs/FRONTEND-PROJECT.md` | Frontend AI context: conventions, where-to-find-things, 9 gotchas | All frontend-facing agents |
| `.claude/rules/github-flow-mandatory.md` | GitHub Flow rules: branch → PR → review → merge (no direct push to main) | All agents |
| `.claude/rules/code-style.md` | Async patterns, EF Core migration rules, test naming + coverage targets | Architect, QA agents |
| `.claude/rules/security.md` | Security guardrails: no secrets, JWT required, parameterized SQL, HTTPS, Stripe sig verification | Security agents |
| `.claude/rules/compliance.md` | Italian regulatory rules: CIN format, GDPR, Alloggiati Web, tourist tax | Compliance harness gates |
| `.claude/rules/integrations.md` | Auth0, Stripe, SendGrid, OTA adapter rules | Architect, Security agents |
| `.claude/rules/gotchas.md` | DateTime UtcNow, DbContext scope, OTA webhook timeout, tourist tax no-hardcode | All agents |
| `.claude/context/regulations/` | Full Italian regulatory detail (lazy-load by topic: cin.md, gdpr.md, alloggiati.md, etc.) | Harness compliance gates |
