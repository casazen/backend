# Domain Context: CasaZen

> API-first Italian property management platform targeting short-term vacation rentals with OTA automation, mandatory regulatory compliance, and AI-driven dynamic pricing.

---

## overview

CasaZen is a backend-first (ASP.NET Core / .NET 10) SaaS platform designed for Italian property owners and small property management companies. It automates the most burdensome aspects of managing vacation rental properties in Italy:

- **Multi-channel OTA synchronization**: Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda (6 platforms; only Airbnb has a live integration — others are stubs)
- **Italian regulatory compliance**: CIN national registration (D.L. 145/2023), Alloggiati Web police reporting, tourist tax calculation and collection by municipality, GDPR
- **AI dynamic pricing**: infrastructure implemented (PricingAdapterConfig, PricingHistory with confidence scores, 6 endpoints); no external AI provider wired up yet
- **Booking & guest management**: full lifecycle (Pending→Confirmed→CheckedIn→CheckedOut→Cancelled), tourist tax auto-calculation, partial refunds
- **Payments**: Stripe integration with webhook signature verification

**Current status**: Well-architected, layered codebase with 14 core entities, ~35 API endpoints, strong test culture, and active development. Approaching feature-completeness on the Italian short-term rental vertical — but still largely an MVP relative to the full vision.

**Strategic context for this council**: The owner wants to use CasaZen as a launchpad to disrupt the property management software market (both short-term and long-term rentals) by leveraging AI. The council must audit current state, challenge assumptions, and propose a bold pivot or expansion that wins a defensible market segment.

---

## stakeholders

| Stakeholder | Role / Interest | Authority | Notes |
|---|---|---|---|
| Luca (product owner / founder) | Product vision, market strategy, prioritization | Decision | Full authority; wants radical / disruptive proposals |
| Italian property owners (primary users) | Reduce admin burden, ensure compliance, maximize revenue | Advisory | Pain: multi-platform dashboards, police reports, tax calculations |
| Small PMCs (property management companies) | Manage 5–50 properties, need scalable tooling | Advisory | Underserved by current tools; high willingness-to-pay |
| Guests | Smooth booking experience, price transparency | Informed | Indirect users via OTA or future direct-booking channel |
| Italian regulators | CIN compliance, police reporting, GDPR | Informed | Non-negotiable constraints but also moat for compliant platforms |

---

## market-landscape

**Short-term rental software (Italy & Europe)**:
- **Incumbents**: Lodgify, Hostaway, Guesty, Smoobu, Beds24 — all channel managers with PMS features; crowded, feature-parity race
- **Gaps in incumbents**: (1) Italian regulatory compliance is manual or third-party add-on; (2) AI pricing is bolt-on (PriceDynamics, Pricelabs) not native; (3) no platform owns "regulatory intelligence" as a feature
- **Long-term rental software**: Immobiliare.it SaaS tools, Gestionale360, custom Excel — extremely fragmented, low innovation, no AI

**Disruption levers observed in market**:
1. AI-native underwriting (no incumbent uses AI for yield management end-to-end)
2. Regulatory-first moat: CIN, Alloggiati Web automation creates lock-in
3. Direct-booking engine bypassing OTA fees (Airbnb charges 3% host fee; 15%+ guest fee)
4. Long-term rental expansion: same compliance stack applies (GDPR, SCIA, deposito cauzionale digitale); 10x larger TAM than short-term
5. AI "property advisor" (pricing + demand forecasting + regulatory alerts) as premium tier

**TAM estimates** (Italy):
- ~600,000 active short-term rental listings (Airbnb + Booking.com)
- ~4 million residential rental units (long-term)
- ~15,000 property management companies

---

## regulatory-environment

| Regulation | Scope | CasaZen status |
|---|---|---|
| D.L. 145/2023 (CIN) | National CIN code mandatory per property | Implemented — validation, storage |
| D.L. 286/1998 (Alloggiati Web) | Police reporting within 24h of check-in | Implemented — AlloggiatiWebReport entity, client |
| GDPR / D.Lgs. 196/2003 | Guest data retention, right to erasure, export | Implemented — consent, erasure endpoints |
| Tourist tax (IMU/tassa soggiorno) | Varies by municipality; collected by host | Implemented — TaxRate entity per city |
| Cedolare secca | Flat tax regime for rental income reporting | Not implemented |
| SCIA (short-term rental notification) | Municipal notification per property | Not implemented |
| Deposito cauzionale digitale | Digital security deposit (emerging) | Not implemented |

**Strategic note**: Italian regulatory compliance is a genuine moat. Any platform that automates Alloggiati Web + CIN + tourist tax reliably already has a hard-to-replicate advantage.

---

## financial-context

- **Current model**: Not defined (pre-revenue / MVP stage)
- **Comparable SaaS pricing** (channel managers): €30–€150/month per property; Guesty at €300+/month for PMCs
- **AI pricing tools** (Pricelabs, PriceDynamics): €10–€20/month/property as bolt-on
- **Direct-booking commission avoidance**: saving 15–18% OTA fees is a compelling value prop for a direct booking engine
- **Key financial levers**: (1) per-property subscription; (2) transaction fee on Stripe payments; (3) premium AI advisory tier; (4) white-label for PMCs

---

## operational-context

- **Development team**: Solo founder / small team; single active contributor on backend
- **Frontend**: Separate repository (casazen/frontend); React-based; field naming conflicts with backend not yet resolved
- **CI/CD**: GitHub Actions; Hangfire for background jobs; no production deployment documented
- **OTA completeness gap**: 5 of 6 OTA adapters are stubs — real integration only for Airbnb
- **AI pricing**: Infrastructure exists but no external AI/ML service wired in
- **Observability**: Grafana stack planned (recent commit); not yet live

---

## services

| Service | Port | Schema | Key Components |
|---|---|---|---|
| Casazen.Web (API) | 5001 (HTTPS) | — | 12 controllers, Auth0 JWT middleware, Hangfire dashboard, Swagger |
| SQL Server | 1433 | CasazenDb | 14 entity tables + migrations |
| Hangfire | Embedded | CasazenDb | OTA sync jobs (hourly/15min), GDPR retention, pricing sync |
| Stripe | External | — | Payment processing, webhook handler |
| SendGrid | External | — | Transactional email templates |
| Auth0 | External | — | JWT issuance and validation |
| Alloggiati Web | External | — | Italian police reporting API |

---

## tech-stack

- **Language / Runtime**: C# 13 / .NET 10 — ASP.NET Core Web API
- **ORM**: EF Core with SQL Server provider; code-first migrations
- **Background jobs**: Hangfire (embedded, SQL-backed)
- **Resilience**: Polly (retry with exponential backoff, circuit breaker, timeout, rate limiting — per OTA platform)
- **Auth**: Auth0 + JWT Bearer token validation
- **Payments**: Stripe SDK with webhook signature verification
- **Email**: SendGrid (template IDs, no inline HTML)
- **Testing**: xUnit, Mock\<IRepository\> pattern
- **CI**: GitHub Actions
- **Planned observability**: Grafana + structured logging (ILogger)

---

## bounded-context-pattern

```
Casazen.Web (Presentation)
├── Controllers/          # 12 controllers — Properties, Bookings, Guests, Payments, OTA, Pricing, Compliance, etc.
├── Middleware/
└── DTOs/

Casazen.Core (Domain — no external dependencies)
├── Entities/             # 14 entities (Property, Booking, Guest, Payment, TouristTaxRate, ...)
├── Repositories/         # Interfaces only
├── Services/             # Interfaces only
└── Enums/

Casazen.Infrastructure (Data + External)
├── Data/                 # DbContext, EF migrations
├── Repositories/         # IRepository implementations
├── Services/             # IService implementations
└── OTA/                  # 6 adapters (Airbnb real; others stub)
    External/             # Stripe, SendGrid, AlloggiatiWeb, Hangfire jobs

Casazen.Tests
├── Unit/
└── Integration/
```

**Patterns**: Repository pattern (all data access via interfaces), DI via Program.cs, Adapter pattern per OTA platform, Conventional Commits + GitHub Flow.

---

## cross-context-integration

- **OTA → Bookings**: Hangfire pulls bookings from OTA adapters hourly, writes to Bookings table
- **Bookings → AlloggiatiWeb**: On check-in, background job submits guest data to police API within 24h
- **Bookings → Stripe**: On booking creation, payment intent created; on cancellation, refund triggered
- **Pricing → OTA**: AI pricing sync pushes nightly rates to OTA platforms via adapter interfaces
- **FE ↔ BE**: REST API; field naming conflicts exist (NightlyRate vs pricePerNight, PostalCode vs zipCode) — tracked in open issues #86, #90, #91

---

## testing-landscape

- **Framework**: xUnit with AAA pattern
- **Mocking**: `Mock<IRepository>` (Moq)
- **Coverage targets**: Critical paths 100%, services 80%, controllers 70%
- **Known gaps**: OTA resilience regression tests (#35), property detail epic regression tests (#158), AI pricing has minimal test coverage
- **Integration tests**: Present for key flows; some integration test breakage fixed in recent commits

---

## current-backlog-snapshot

**Priority open issues** (as of 2026-05-26):
- #158: Integration + regression tests for property detail epic (in-sprint)
- #91: Contract audit FE/BE field misalignment (decision pending)
- #86, #90: Field naming conflicts (NightlyRate/pricePerNight, PostalCode/zipCode)
- #58: Email notifications for booking confirmations
- #51: Automatic refunds on cancellation
- #35: OTA resilience regression tests
- #33: Webhook handlers for inbound OTA notifications
- #32, #31: OTA availability/booking pull endpoints
- #30–#26: Real API implementations for Agoda, TripAdvisor, VRBO, Expedia, Booking.com

**Strategic gaps not yet in backlog**:
- Direct booking engine (bypass OTA fees)
- Long-term rental module
- AI pricing external provider integration
- Cedolare secca / SCIA compliance automation
- PMC white-label / multi-tenant architecture
- Mobile app / PWA for property owners
