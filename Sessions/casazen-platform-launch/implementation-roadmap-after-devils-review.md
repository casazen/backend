# CasaZen — Implementation Roadmap (after Devil's Advocate review)

> **Overlay, not a rewrite.** The consensus `implementation-roadmap.md` is **preserved unchanged**. This file records the amendments accepted in `devils-advocate-review.md` (challenges #1/#16, #2, #6, #13, #14). Sections marked **SUPERSEDES** replace the same section of the original; everything else still stands.

---

## Phase 0 — add a security hotfix (SUPERSEDES Phase 0 "reality flags"; addresses #13)

`GET /api/properties/search` is `[AllowAnonymous]` and **returns the raw `Property` including `OwnerId`** — this is a **live PII/IDOR exposure today**, not a Phase 1 feature gap. Pulled forward:

- **Phase 0 hotfix — `anonymous-search-pii-stripping` [FACT, security].** Immediately strip `OwnerId` and any owner/PII fields from the anonymous `/search` response (a minimal projection / `[JsonIgnore]` + DTO), independent of and **ahead of** the full `spec-public-booking-readmodel`. This is a hardening fix shippable on the $0 tier in Phase 0; `spec-public-booking-readmodel` then supersedes it with the complete public read-model in Phase 1.
- Rationale: a known data leak must not wait behind a feature epic. Carried as Counsel-required item #4 in `decision.md`; now also a Phase 0 engineering action.

---

## Phase 1 — Stripe Connect onboarding is a deliverable, not a footnote (SUPERSEDES Phase 1 specs/ordering; addresses #1, #16)

The original listed "Stripe → Stripe Connect (operator MoR)" only as a *dependency*; **no spec owned operator/landlord onboarding/KYC**. Corrected:

- **Specs (now 7):** `spec-public-booking-readmodel`, **`spec-connect-onboarding` (new)**, `spec-direct-checkout`, `spec-branded-booking-site`, `spec-tenant-boundary`, `spec-saas-billing`, `spec-onboarding-plg`.
- **Internal ordering (revised):** `spec-tenant-boundary` (`Org` + `StripeConnectedAccountId`) → **`spec-connect-onboarding`** (creates the connected account + KYC + `charges_enabled`) → `spec-direct-checkout` (charge-gated on `charges_enabled`) and `spec-public-booking-readmodel` → `spec-direct-checkout`; `spec-branded-booking-site` publish is **gated on onboarding completion**.
- **Exit criterion (added):** the operator's connected account is **`charges_enabled`** before the branded site can go live — closing the permanent-`409` trap in `spec-direct-checkout` AC3.

Macro-spec index becomes **18 specs** (was 17).

---

## Phase 1 — infra upgrade trigger moved earlier + public-funnel gate (SUPERSEDES Phase 1 "Infra" + decision Infra gate; addresses #2)

**Problem.** The original trigger ($0 → ~€5/mo at **first real guest check-in**) collides with the **Alloggiati 24h** duty that *starts at that same check-in*. You cannot prove the $0-window cron at the instant the legal clock starts.

**Amendment.**
- **Upgrade trigger now fires earlier — at the moment a property goes publicly bookable / first confirmed direct booking** (strictly before any check-in can occur), not at check-in.
- **Public-funnel gate:** a property **cannot be published as publicly bookable** until **either** (a) the $0-window GH Actions cron is **proven** firing the Alloggiati + GDPR endpoints on schedule, **or** (b) the **~€5/mo always-on tier is active**. No public booking surface → no guest → no unguarded 24h Alloggiati duty.
- Net: the cost-minimization lever and the hard legal deadline no longer share the same trigger event. R5 in the business plan and the Infra gate in `decision.md` are read with this revised trigger.

---

## Phase 1.5 — "parallel" qualified at the data layer (SUPERSEDES Phase 1.5 header + AD-2 reading; addresses #6)

The original "PARALLEL — depends on neither billing nor multi-tenancy" is true at the **feature** level but contradicts **RF1** (every tenant-scoped table carries `OrgId`) and **RF3** (migration order). Corrected statement:

> **Phase 1.5 runs parallel in feature work and design, but is serialized at the database layer.** Its recurring-rent ledger and any new tables **carry `OrgId` from creation** (RF1) and its **migrations rebase onto the post-Phase-1 EF snapshot** after `spec-tenant-boundary`'s `OrgId` migration lands (RF3). "No dependency on billing/multi-tenancy" means **no functional dependency** (rent billing doesn't need SaaS billing or seats) — **not** independence from the tenant boundary's schema. Build in parallel; **merge migrations in order**.

This is consistent with AD-6 and RF3 in the originals; only the Phase 1.5 header's absolute "parallel" wording is amended.

---

## Sequencing note (addresses #14, partial)

Indicative, non-binding durations are recorded in `business-plan-after-devils-review.md` §10 ([EST], not a committed schedule). The roadmap remains **dependency-ordered**, not date-ordered.

---

## Deliberation trail — Devil's Advocate Review

Amendments #1/#16 (Connect onboarding = Phase 1 deliverable, index → 18), #2 (trigger + funnel gate), #6 (parallel qualified), #13 (Phase 0 PII hotfix), #14 (durations [EST]) accepted; consensus `implementation-roadmap.md` preserved unchanged. Full disposition: `devils-advocate-review.md`.
