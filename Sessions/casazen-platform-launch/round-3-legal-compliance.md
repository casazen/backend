---
round: 3
role: legal-compliance
vote: APPROVE
---

## Legal & Compliance Advisor — Round 3 Response

**Vote**: APPROVE

**Reasoning**:
C6 is fully resolved. The `spec-ltr-recurring-rent` regulatory gate now mandates **Stripe Connect with the LANDLORD as merchant of record, CasaZen never holding or settling tenant rent funds** (mirroring `spec-direct-checkout`), and the depends-on column was upgraded to "Stripe Connect"; the **residential-rent IVA treatment** (IVA-exempt Art. 10 DPR 633/72; €2 imposta di bollo > €77.47 except cedolare-secca) is reflected on the receipt and marked [COUNSEL_REQUIRED]. RF1 adds a binding invariant that the rent ledger cannot ship un-scoped, and RF2 separates platform- vs connected-account webhook routing — both strengthen payment controls without new legal gaps. Per my Round 2 commitment, I approve.

**Details**:
- **C6 check**: RESOLVED. Both elements present and precise — (1) Stripe Connect / landlord-MoR, CasaZen never holds tenant funds; (2) residential-rent IVA-exempt + bollo on receipt, flagged COUNSEL_REQUIRED. No payment-intermediary/e-money or rent-agent drift remains.
- **Carried non-blocking counsel-required reminders (for decision record)**:
  1. RLI (C4) enforcement in code — per-filing landlord authorization (delega capture) in create→e-sign→RLI→receipt; ToS places filing responsibility on landlord/authorized intermediary; confirm who holds *intermediario abilitato* in the Openapi.it chain; `spec-ltr-verification` asserts the attended-confirmation step.
  2. Rent-receipt tax finalization — confirm IVA-exempt + bollo / cedolare-secca with a commercialista (gated COUNSEL_REQUIRED).
  3. Extra-EU tenants — `LeaseContract.HasExtraEUTenant` → surface landlord's authority-communication duty (cessione di fabbricato / hospitality, Art.7 D.Lgs 286/1998).
  4. Public read-model timing — ship `spec-public-booking-readmodel` before/with any public surface; consider hardening the already-live anonymous `/search` now (returns `OwnerId`).
  5. Pre-launch counsel pack — entity/regime (§A.11) + IVA/OSS/SDI before first charge (entry gate encodes this); DPA/ToS/privacy/AI-Act review; grants/R&D-credit eligibility docs.
  6. §D Q3 answer: the §A.11 vehicle decision need NOT be a hard predecessor to Phase 1 dev; concurrent dev is fine up to the "P.IVA + SDI live before first charge" gate (the only hard stop).

**Disclaimer**: Not legal advice. COUNSEL_REQUIRED items need qualified Italian/EU counsel + commercialista before execution.
