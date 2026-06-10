---
round: 1
role: legal-compliance
vote: PROPOSE
---

## Legal & Compliance Advisor — Round 1 Response

**Vote**: PROPOSE

**Reasoning**:
The launch path is fundamentally lawful and compliance sequencing is sound: the draft correctly keeps CasaZen as **pure B2B SaaS** (subscription only, no take-rate on operator's own bookings), markets the implemented Italian stack (CIN, Alloggiati Web, tourist tax, GDPR) as a differentiator rather than assuming operator obligations, and defers IVA/formation/RLI/DAC7/AI-Act items to counsel. **No illegal tactic found** — every cost lever (SRLS, forfettario, freemium infra, OSS, Invitalia/PNRR grants, R&D credit) is lawful. Three items must be tightened before becoming code: (1) "SRLS €1 + forfettario" is a category error — regime forfettario is for individuals only, never an SRLS/SRL (IRES/IRAP + IVA entities); (2) Phase 1 payment flow must use **Stripe Connect with operator as merchant of record** so CasaZen never holds guest funds; (3) IVA/e-invoicing (SDI) + OSS must be encoded into `spec-saas-billing` from the first charge.

**Details**:
- **Risk matrix (high items)**: Stripe Connect/MoR for guest payments [H]; IVA/SDI e-invoicing + OSS before first charge [H]; automated RLI/cedolare filing = regulated tax-intermediary activity [H if attempted]; operator STR-obligation creep [H]; GDPR controller/processor split + DPA + subprocessors (Supabase EU, Auth0, Stripe, SendGrid) + SCCs [M]; $0 infra vs Alloggiati-24h/GDPR jobs [M/H]; EU AI Act transparency (Aug 2026) [M]; marketplace VAT + DAC7 + payouts Phase 4 [M]; B2B ToS/SLA + AI disclaimer [M].
- **Launch checklist (ordered, before first paying customer)**: (0) commercialista decides entity+regime; (1) incorporate/P.IVA + ATECO 62.01/63.11 + PEC + bank; (2) stand up SDI e-invoicing (Stripe ≠ fattura elettronica); (3) lock IVA/OSS matrix (IT 22% / EU-B2B reverse charge + VIES / EU-B2C OSS >€10k); (4) Stripe Connect, operator = MoR; (5) B2B ToS+SLA, Privacy Policy, DPA, subprocessor list + SCCs, cookie consent; (6) AI Act transparency disclosure + decision logging + human-in-loop; (7) upgrade to ≥€5 Railway before first real guest booking; (8) compliance-claims marketing review; (9) Phase 3 gate: counsel-reviewed LTR templates; (10) Phase 4 gate: DAC7 + marketplace VAT + Connect payouts + supplier KYC; (11) optional startup-innovativa + grants.
- **Lawful cost levers confirmed**: SRLS €1 ✓; forfettario (individuals only) ✓; freemium infra $0→€5 with pre-booking upgrade guardrail ✓; OSS .NET/React ✓; Invitalia Smart&Start / regional / PNRR ✓; R&D tax credit ✓; startup innovativa status ✓; EU-B2B reverse charge ✓.
- **UNLAWFUL tactics flagged**: None found.
- **Answer Q3 (RLI/cedolare scope)**: out of scope for unattended automation — automated RLI submission requires acting as intermediario abilitato (DPR 322/1998) via Entratel, a regulated status pure SaaS lacks. Scope Phase 3 to: (a) compliant lease-contract generation (counsel-reviewed templates), (b) cedolare-secca decision support + indicative computation, (c) RLI data pre-fill/export + guided manual-registration checklist (30-day deadline, notifications). Defer assisted filing to partnership with authorized intermediary.
- **Answer Q4 (formation + IVA before billing)**: gating conditions before `spec-saas-billing` charges anyone. Forfettario = individual only; SRLS/SRL = 22% IVA + mandatory SDI. Given grants/hiring/EU-B2B ambitions, SRLS/SRL is the coherent vehicle (final tax trade-off → Financial). Pre-Phase-1 blockers: entity+regime decided; P.IVA+ATECO; SDI live; IVA/OSS matrix encoded; Stripe Connect MoR.
- **Resolution conditions (PROPOSE → APPROVE)**: C1 corrected entity/regime decision tree; C2 spec-saas-billing IVA/OSS+SDI + "P.IVA+SDI live" entry gate; C3 Stripe Connect MoR / never hold guest funds; C4 rescope spec-ltr-rli-registration to contract-gen + decision-support + guided manual reg; C5 add DPA + subprocessor list + AI-transparency gates to spec-onboarding-plg and spec-ai-copilot-messaging.

**Disclaimer**: Not legal advice. Counsel "Y" items require qualified Italian/EU counsel + commercialista.
