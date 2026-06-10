# Council Decision Record — CasaZen Platform Launch

> **Pattern**: builder-validator · **Protocol**: deliberative-voting · **Rounds**: 3 (consensus)
> **Date**: 2026-06-05 · **Session**: `Sessions/casazen-platform-launch/`
> **Final artifact**: `draft-v3.md` → `business-plan.md` + `implementation-roadmap.md` + 17 macro-specs in `Sessions/specs/`

---

## 1. Decision

The council **approves by consensus** a go-to-market and product strategy positioning CasaZen as an **AI-powered, direct-booking-first, STR+LTR rental operating system for Italy's independent operators (1–500 units)**, monetized as a **transparent per-unit subscription with zero booking commission**, and a phased implementation plan from the verified current codebase to a sellable production platform — executed lawfully on a near-zero launch budget.

**Final votes (Round 3):** GTM Strategist PROPOSE (author) · Product Architect **APPROVE** · Legal & Compliance **APPROVE** · Financial Strategist **APPROVE**. No REJECT in any round.

---

## 2. Agreed proposal (summary)

- **Positioning**: democratic 1–500 units; AI copilot across the full lifecycle; commission-free direct booking; native Italian compliance (STR **and** LTR) as the moat.
- **Wedge**: Italian property managers, 10–200 units (F7).
- **Pricing**: Starter freemium → Pro €4–8/unit/mo → Scale custom; launch ARPU €180–200, drifting to €275 by Y3 (within €150–400 benchmark, F9).
- **Revenue mix**: 70–80% SaaS, 10–20% marketplace, rest services (F10).
- **Legal vehicle**: SRLS/SRL (IRES/IRAP + 22% IVA + SDI), not forfettario (individuals only); pursue *startup innovativa* + lawful grants/R&D credit.
- **Roadmap**: Phase 0 (verified baseline) → Phase 1 (direct booking + tenant boundary + billing) ∥ Phase 1.5 (LTR complete+verify) → Phase 2 (inbox + AI copilot + seats) → Phase 3 (marketplace + GVR) → Phase 4 (scale + EU).

---

## 3. User stories (→ macro-specs)

| ID | As a… | I want… | Spec | Phase |
|----|-------|---------|------|:---:|
| **US-001** | guest | to search public listings without exposing owner/PII data | `spec-public-booking-readmodel` | 1 |
| **US-002** | guest | to book and pay directly, the operator being merchant of record | `spec-direct-checkout` | 1 |
| **US-003** | property manager | a branded direct-booking website per organization | `spec-branded-booking-site` | 1 |
| **US-004** | PM agency | an organization/tenant boundary with plan entitlement | `spec-tenant-boundary` | 1 |
| **US-005** | CasaZen | to charge customers a subscription with correct IVA/OSS + SDI invoicing | `spec-saas-billing` | 1 |
| **US-006** | new customer | to self-serve onboard from signup to first booking | `spec-onboarding-plg` | 1 |
| **US-007** | long-rent landlord | automatic recurring monthly rent billing (landlord = MoR) | `spec-ltr-recurring-rent` | 1.5 |
| **US-008** | long-rent landlord | a frontend over the existing lease workflow | `spec-ltr-frontend` | 1.5 |
| **US-009** | CasaZen | assurance the existing lease→e-sign→RLI→receipt flow works E2E | `spec-ltr-verification` | 1.5 |
| **US-010** | long-rent landlord | assisted RLI registration + cedolare decision support (no unattended filing) | `spec-ltr-rli-registration` | 1.5 |
| **US-011** | property manager | one inbox aggregating OTA + direct guest messages | `spec-unified-inbox` | 2 |
| **US-012** | property manager | AI-drafted replies with hard fair-use cost caps | `spec-ai-copilot-messaging` | 2 |
| **US-013** | PM agency | to invite team members with seat-scoped RBAC | `spec-org-seats-collaboration` | 2 |
| **US-014** | property manager | a supplier marketplace with platform take-rate | `spec-supplier-marketplace` | 3 |
| **US-015** | property manager | Google Vacation Rentals direct distribution | `spec-google-vacation-rentals` | 3 |
| **US-016** | enterprise agency | multi-brand, SSO, SLA, portfolio AI | `spec-enterprise-scale` | 4 |
| **US-017** | CasaZen | ES/FR compliance modules replicating the Italian pattern | `spec-eu-compliance-es-fr` | 4 |

Acceptance criteria (BE + FE) and regulatory gates per story live in the corresponding `Sessions/specs/spec-{slug}.md`.

---

## 4. Architectural decisions

- **AD-1 — Verified baseline supersedes docs.** Real codebase: **22 entities, 16 controllers, 10 jobs, Supabase PostgreSQL**, a built LTR lease+RLI+e-sign subsystem, and context-scoped RBAC. `PROJECT.md`/`TECHNICAL.md` are stale (claim SQL Server / 14 / 12) → Phase 0 docs-update hygiene item.
- **AD-2 — LTR is "complete + verify", not greenfield.** The lease/RLI/e-sign engine exists; the only real gap is a recurring-rent ledger/job. LTR runs as a **parallel Phase 1.5** (no dependency on billing/multi-tenancy).
- **AD-3 — Direct booking is a real epic, not a shortcut.** Anonymous `/properties/search` returns the raw `Property` (incl. `OwnerId`) → split into read-model + checkout + branded-site.
- **AD-4 — Tenant boundary before seats.** Introduce `Org` + `OrgId` FK + plan entitlement in Phase 1 (defer seats/invitations to Phase 2, reusing `UserContextMembership`/`RequireContext`). **Invariant: every new tenant-scoped table inherits `OrgId` + entitlement.**
- **AD-5 — Payments: merchant-of-record always the operator/landlord.** Guest checkout (`spec-direct-checkout`) and LTR rent (`spec-ltr-recurring-rent`) use **Stripe Connect with operator/landlord as MoR**; CasaZen never holds/settles guest or tenant funds. SaaS billing uses platform-account Stripe Billing. Separate platform-vs-connected webhook routing.
- **AD-6 — Migration sequencing.** Land `OrgId` migration first (nullable → backfill → NOT NULL + FK); Phase 1.5 migrations rebase onto the updated EF snapshot; new tables carry `OrgId` from creation.
- **AD-7 — AI cost is a hard product constraint.** Cheap-model default + confidence-gated frontier routing + caching + overage metering → AI ≤10–15% of ARPU, gross margin ≥80%.
- **AD-8 — Compliance stays async/off-request.** All compliance/ingestion work runs via Hangfire (Alloggiati on check-in, GDPR retention, inbox ingestion), consistent with the existing webhook pattern.

---

## 5. Regulatory & financial gates (binding)

- **Legal entry gate (Phase 1):** P.IVA + ATECO + **SDI e-invoicing live before the first SaaS charge**; IVA/OSS matrix (IT 22% / EU-B2B reverse charge + VIES / EU-B2C OSS >€10k) encoded in `spec-saas-billing`.
- **RLI (Phase 1.5):** operator-attended only; CasaZen ≠ *intermediario abilitato*; contract templates counsel-reviewed; residential-rent IVA-exempt (Art. 10 DPR 633/72) + €2 bollo on receipts >€77.47 except cedolare. **[COUNSEL_REQUIRED]**
- **Infra trigger:** upgrade $0 → ~€5/mo Railway Hobby at **first real guest check-in** (Alloggiati 24h clock); prove the $0-window GH Actions cron fires Alloggiati + GDPR endpoints.
- **Financial baseline:** launch ARPU €180–200; cash breakeven ~1–2 accounts (founder unpaid); CAC ≤ ~€1,700 (target €300–800, mostly non-cash); payback <12 mo; NRR ≥100%.

---

## 6. Counsel-required items (carried from Legal)

1. RLI delega capture + ToS placing filing responsibility on landlord/authorized intermediary; confirm intermediario abilitato in the Openapi.it chain.
2. Rent-receipt tax finalization (IVA-exempt + bollo / cedolare) with a commercialista.
3. Extra-EU tenant authority-communication duty (Art. 7 D.Lgs 286/1998) surfaced in LTR flow.
4. Public read-model shipped before/with any public surface (anonymous `/search` currently leaks `OwnerId`).
5. Pre-launch counsel pack: entity/regime, ToS/SLA, DPA + subprocessors (Supabase EU, Auth0, Stripe, SendGrid), EU AI Act disclosure, grant/R&D-credit eligibility documentation.

---

## 7. Deliberation summary

| Round | GTM (Builder) | Product Architect | Legal & Compliance | Financial | Outcome |
|:---:|:---:|:---:|:---:|:---:|---|
| 1 | PROPOSE | **OBJECT** | PROPOSE | APPROVE* | No consensus → 13 conditions |
| 2 | PROPOSE | **APPROVE** | **OBJECT** (C6) | **APPROVE** | No consensus → 1 fix (C6) |
| 3 | PROPOSE | APPROVE (carried) | **APPROVE** | APPROVE (carried) | **Consensus** |

\* Round 1 Financial APPROVE was conditional (3 binding conditions, folded into Round 2).

**Pivotal moment:** the Product Architect's Round 1 OBJECT, when verified by the Coordinator against the real codebase, revealed the docs were stale and the LTR subsystem + context-RBAC already existed — reshaping the entire roadmap (LTR → parallel Phase 1.5) and spec set (12 → 17 specs). Legal's Round 2 C6 then closed the last gap by extending the merchant-of-record control to the newly-surfaced LTR rent flow.

Full per-round detail: `round-1.md`, `round-2.md`, `round-3.md` and the per-role `round-{N}-{role}.md` files.
