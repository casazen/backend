---
round: 2
role: legal-compliance
vote: OBJECT
---

## Legal & Compliance Advisor — Round 2 Response

**Vote**: OBJECT

**Reasoning**:
draft-v2 cleanly resolves all five Round 1 conditions: §A.11 corrects the entity/regime tree (forfettario = individual/ditta only; SRLS/SRL = IRES/IRAP + 22% IVA + SDI), `spec-saas-billing` carries the IVA/OSS matrix + SDI + "P.IVA + SDI live" Phase 1 entry gate, `spec-direct-checkout` mandates Stripe Connect with operator as MoR, `spec-ltr-rli-registration` is rescoped to assisted/attended (CasaZen ≠ intermediario abilitato), and DPA + subprocessor list + EU AI Act disclosure land in onboarding/AI specs. The rebaseline also scheduled a fix for a real GDPR data-minimization issue (anonymous `/search` leaking raw `Property` incl. `OwnerId`). However, the verified LTR rebaseline introduced a **new money-movement flow** — `spec-ltr-recurring-rent` (Phase 1.5) — that lists Stripe + PSD2/SCA but does NOT apply the operator-MoR/Stripe-Connect control C3 mandated. Collecting tenant rent into CasaZen's own balance is the same High-severity risk class C3 prevents (PSD2 payment-intermediary/e-money + rent-collection-agent/operator drift). Under "non-compliance is a blocker" I OBJECT on this single narrow point; everything else is met.

**Details**:
- **Condition check**: C1 Resolved (§A.11 voids SRLS+forfettario combo; A/B/C tree). C2 Resolved (IVA/OSS + SDI + entry gate before first charge). C3 Resolved for guest-booking flow (Connect, operator MoR). C4 Resolved (RLI rescoped to attended; templates + cedolare decision-support + export/guided 30-day checklist). C5 Resolved (DPA + subprocessors + AI-Act disclosure in onboarding & AI specs).
- **New legal issue (BLOCKER)**: `spec-ltr-recurring-rent` tenant→landlord rent flow omits Connect/landlord-MoR → PSD2/operator drift.
- **Residual non-blocking (counsel-required)**: enforce C4 in code (per-filing landlord delega capture; ToS places filing responsibility on landlord/intermediary; confirm who holds intermediario abilitato in Openapi.it chain); residential rent generally IVA-exempt (Art.10 DPR 633/72) + €2 imposta di bollo >€77.47 except cedolare — wire into rent receipts; `LeaseContract.HasExtraEUTenant` → surface landlord's Art.7 D.Lgs 286/1998 authority-communication duty; ship public read-model before/with any public surface; vehicle→billing not a hard predecessor (the "P.IVA + SDI live before first charge" gate is the only hard stop).
- **Resolution (C6, blocking)**: in `spec-ltr-recurring-rent`, mandate **Stripe Connect with the landlord as merchant of record; CasaZen never holds or settles tenant rent funds** (mirror `spec-direct-checkout`), and reflect residential-rent IVA-exempt + bollo treatment on the rent receipt. One-line regulatory-gate addition → then APPROVE.

**Disclaimer**: Not legal advice.
