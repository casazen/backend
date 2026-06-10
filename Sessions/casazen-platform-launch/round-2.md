# Round 2 — CasaZen Platform Launch

## Responses

### GTM Strategist (Builder) — PROPOSE
Produced `draft-v2.md` resolving all 13 Round 1 conditions; 17 macro-specs post-split across Phase 0 / 1 / 1.5 / 2 / 3 / 4. Residual question to PA on `OrgId` FK merge order.

### Product Architect (Validator) — APPROVE
All 6 conditions resolved against the real codebase. Answered the merge-order question (land tenant-boundary migration first; Phase 1.5 rebases; new tables carry `OrgId` from creation). Non-blocking notes: add "every new tenant-scoped table inherits OrgId+entitlement" invariant; separate platform-account vs connected-account Stripe webhook routing.

### Legal & Compliance Advisor (Validator) — OBJECT
C1–C5 all resolved. New blocking issue **C6**: `spec-ltr-recurring-rent` (the new tenant→landlord rent flow surfaced by the verified LTR rebaseline) omits the Stripe Connect / landlord-as-MoR control that C3 mandated → PSD2 payment-intermediary / operator drift. One-line fix → APPROVE. Counsel-required reminders on RLI delega, residential-rent IVA-exempt + bollo, extra-EU tenant duty.

### Financial Strategist (Validator) — APPROVE
All 3 conditions incorporated in operative sections. Y1 floor now reconciles (≈€90.7k). SRL 22% IVA is a pass-through, not a margin cost; breakeven unchanged (~1–2 accounts cash). Flag: drop the stale "market file not on disk" caveat.

## Coordinator Synthesis

**Consensus**: No — 1 OBJECT (Legal), 2 APPROVE (PA, Financial), 1 PROPOSE (builder). 0 REJECT.

**Agreements**: The revised architecture, phasing (Phase 1 / parallel Phase 1.5 LTR / Phase 2+), corrected Phase 0 baseline, legal launch sequence (C1–C5), and financial model (ARPU relabel, check-in infra trigger, AI caps) are all accepted by their respective owners.

**Single outstanding objection**:
- **[Legal C6]** `spec-ltr-recurring-rent` must mandate **Stripe Connect with the landlord as merchant of record; CasaZen never holds or settles tenant rent funds** (mirroring `spec-direct-checkout`), and reflect residential-rent **IVA-exempt (Art.10 DPR 633/72) + €2 imposta di bollo** treatment on the rent receipt.

**Non-regression note**: C6 is additive and squarely within the Legal/payments lane. It is consistent with — and pre-anticipated by — the Product Architect's Round 2 note that billing/checkout/rent flows share the Stripe webhook surface and that Connect is a distinct integration, and with the Financial Strategist's note that residential rent via landlord-MoR is pass-through with no CasaZen margin impact. Therefore the PA and Financial APPROVE votes are **carried forward** to Round 3; only the C6 delta (Legal's lane) is re-validated.

**Revised proposal for Round 3**: Builder applies the single C6 fix → `draft-v3.md`. Legal re-votes (expected APPROVE per its own statement). On Legal APPROVE → consensus (PA + Financial + Legal all APPROVE) → write final deliverables.
