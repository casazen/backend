# CasaZen — MVP Execution Plan (Phase 1)

> **Council**: Platform Launch · **Phase**: MVP Execution · **Date**: 2026-06-09
> **Status**: consensus (Round MVP-1) · **Source**: `round-mvp-1.md`

---

## 1. Objective

Deliver the **first sellable MVP**: an external property manager self-onboards, gets an `Org`, publishes a branded commission-free direct-booking site, takes a guest payment (operator = MoR via Stripe Connect), and pays CasaZen a subscription — with Italian compliance (CIN, Alloggiati, tourist tax) auto-firing.

---

## 2. Current state (2026-06-09)

| Spec | Status | Artifact |
|------|--------|----------|
| `spec-tenant-boundary` | ✅ Complete | v1.1.6, PR #203/#104 |
| `spec-public-booking-readmodel` | 🟡 Stage 03 done | PR #213/#112, review pending |
| `spec-branded-booking-site` | 🟡 In progress | `feature/215-branded-booking-site`, `PublicOrgController` |
| `spec-connect-onboarding` | ❌ Not started | — |
| `spec-direct-checkout` | ❌ Not started | — |
| `spec-saas-billing` | ❌ Not started | — |
| `spec-onboarding-plg` | ❌ Not started | — |

**Estimated engineering progress**: ~22% of Phase 1 MVP scope.

---

## 3. Execution sequence

### Wave 1 — Unblock payments (weeks 1–2)
| Priority | Spec | Action | Owner |
|:---:|------|--------|-------|
| **P0** | `spec-connect-onboarding` | New work: issue → design → dev | — |
| **P0** | `spec-public-booking-readmodel` | Review → release → merge | — |

### Wave 2 — Revenue path (weeks 2–4)
| Priority | Spec | Action | Depends on |
|:---:|------|--------|------------|
| **P1** | `spec-direct-checkout` | After Connect merges | connect-onboarding, read-model |
| **P1** | `spec-branded-booking-site` | Complete FE routes + publish gate (AC11) | read-model; Connect for publish |

### Wave 3 — Sellable gate (weeks 4–6)
| Priority | Spec | Action | Depends on |
|:---:|------|--------|------------|
| **P2** | `spec-saas-billing` | Requires P.IVA + SDI [COUNSEL_REQUIRED] | tenant-boundary |
| **P2** | `spec-onboarding-plg` | DPA + activation checklist | tenant-boundary, branded-site signals |

---

## 4. Binding gates (non-negotiable)

| Gate | Enforced at | Spec |
|------|-------------|------|
| `charges_enabled` before publish/checkout | Connect onboarding complete | `spec-connect-onboarding` AC5/AC10 |
| No public bookable property until Alloggiati cron proven OR €5/mo tier | Before `IsActive` public | DA #2 public-funnel gate |
| P.IVA + SDI live before first CasaZen charge | Before subscription billing prod | Legal C2 |
| DPA accepted at self-serve signup | Before PLG prod | `spec-onboarding-plg` |
| RF2 webhook separation | Connect vs platform secrets | connect-onboarding, saas-billing |

---

## 5. MVP tiers

### Tier A — Design-partner MVP (target: 4–6 weeks)
- Connect onboarding (test/live)
- Public read-model released
- Direct checkout (commission-free)
- Branded booking site (publish gated)
- Manual plan assignment (AC11b from tenant-boundary)
- **Not sellable** — no subscription billing, assisted onboarding

### Tier B — Sellable MVP (target: 6–8 weeks)
- Tier A +
- SaaS billing (Stripe Billing + IVA/OSS + SDI)
- PLG onboarding (DPA, activation milestones)
- **Sellable** per Phase 1 exit criteria

---

## 6. Parallel work rules

| Work | Parallel OK? | Publish/live OK? |
|------|:------------:|:----------------:|
| Branded-site BE (`PublicOrgController`) | ✅ | N/A |
| Branded-site FE routes/shell | ✅ | ❌ until Connect |
| Connect onboarding | ✅ (with read-model release) | — |
| SaaS billing design | ✅ (while Wave 2 dev) | ❌ until P.IVA/SDI |

---

## 7. Next work

1. `spec-connect-onboarding`
2. After PR #213/#112 review: finish `spec-public-booking-readmodel`

---

## 8. Exit criteria checklist

- [ ] Operator creates Connect account; `charges_enabled == true`
- [ ] Guest completes checkout on branded site; funds settle to operator (MoR)
- [ ] No `ownerId`/PII in anonymous responses (read-model)
- [ ] CIN displayed; Alloggiati job fires on confirmed booking
- [ ] Operator pays CasaZen subscription; SDI invoice issued
- [ ] Self-serve signup provisions `Org` + plan + DPA

---

## Deliberation trail

| Round | Outcome |
|-------|---------|
| 1–3 (2026-06-05) | Business plan, roadmap, 18 macro-specs — consensus |
| DA (2026-06-05) | 16 amendments accepted |
| MVP-1 (2026-06-09) | Execution sequencing consensus — this plan |
