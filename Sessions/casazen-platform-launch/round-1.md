# Round 1 — CasaZen Platform Launch

## Responses

### GTM Strategist (Builder)
**Vote**: PROPOSE
**Key points**: Integrated draft written to `draft.md` — business plan + 6-phase roadmap + 12-slug macro-spec index, anchored to market analysis (F1–F10). Flagged that the market-analysis file was missing on disk (since restored by Coordinator).

### Product Architect (Validator)
**Vote**: OBJECT
**Key points**: Phase 0 baseline is materially stale. Verified by Coordinator against the real codebase: **22 entities, 16 controllers, 10 jobs** (docs say 14/12/7). A full **LTR lease subsystem already exists** (`LeaseContract`, `LeaseRegistration` RLI via Openapi.it, `Party`, `LeaseEvent`, `LeasesController`, e-sign + RLI polling jobs) and a **context-scoped RBAC** (`AppContext`/`UserContextMembership`/`Role`/`RolePermission`). DB is Supabase Postgres, not SQL Server. `spec-direct-booking-engine` is under-scoped (no public booking/checkout path); `spec-multi-tenant-orgs` must split (boundary in Phase 1, seats in Phase 2). Interface is `IChannelAdapter`, not `IOtaAdapter`.

### Legal & Compliance Advisor (Validator)
**Vote**: PROPOSE
**Key points**: Launch path lawful; **no illegal tactic found**. Three corrections: (1) "SRLS + forfettario" is a category error (forfettario = individuals only); (2) Phase 1 payments must use **Stripe Connect, operator = merchant of record** (CasaZen never holds guest funds); (3) **IVA/SDI e-invoicing + OSS** must be in `spec-saas-billing` from first charge. RLI/cedolare automated filing out of scope (regulated intermediary) → contract-gen + decision-support + guided manual reg. Add DPA/subprocessors/AI-transparency gates.

### Financial Strategist (Validator)
**Vote**: APPROVE (3 binding conditions)
**Key points**: Burn within constraints; cash breakeven ~1–2 accounts (founder unpaid), ramen ~17. Conditions: (1) tighten infra upgrade trigger to "first real guest check-in" (Alloggiati 24h clock); (2) relabel ARPU to €180–200 launch / €275 Y3, fix Y1 ARR floor ~€90k; (3) hard AI fair-use caps in `spec-ai-copilot-messaging` → GM ≥80%. LTV:CAC 6–17×, payback ~4 months. Grants = Phase 2+ optimization, not month-1.

## Coordinator Synthesis

**Consensus**: No (1 OBJECT, 2 PROPOSE, 1 conditional APPROVE; 0 REJECT → no rejection).

**Agreements**: Positioning (democratic 1–500 units, AI lifecycle copilot, zero-commission subscription) is sound and market-anchored. Pure B2B SaaS model is the correct legal posture. `spec-saas-billing` and a Phase 1 tenant boundary are correctly identified hard gates. Burn is genuinely low. The macro-spec → `Sessions/specs/` format is compatible.

**Outstanding objections (all must be resolved in Round 2 draft)**:
1. **[Product Architect]** Correct Phase 0 to real codebase (22 entities/16 controllers/10 jobs, Supabase Postgres, list LTR subsystem + context-RBAC, `IChannelAdapter`).
2. **[Product Architect]** Rescope + reorder LTR to "complete + verify" (recurring-rent ledger is the real gap; FE; verification), move parallel to Phase 1 (~Phase 1.5).
3. **[Product Architect]** Split `spec-direct-booking-engine` → read-model + checkout + branded-site; restate search+Stripe as seed not shortcut.
4. **[Product Architect]** Split `spec-multi-tenant-orgs` → Phase 1 `spec-tenant-boundary` (+plan entitlement) + Phase 2 `spec-org-seats-collaboration`; keep `spec-saas-billing` Phase 1.
5. **[Product Architect]** `spec-unified-inbox`: add explicit new entities (Conversation/Message/Thread) + async ingestion job; state per-adapter inbound-message assumption.
6. **[Legal C1]** Replace "SRLS €1 + forfettario" with corrected entity/regime decision tree.
7. **[Legal C2]** `spec-saas-billing`: IVA/OSS matrix + SDI e-invoicing + "P.IVA + SDI live" Phase 1 entry gate before first charge.
8. **[Legal C3]** Mandate Stripe Connect, operator = merchant of record; CasaZen never holds guest funds.
9. **[Legal C4]** Rescope `spec-ltr-rli-registration` → contract-gen + cedolare decision-support + RLI export/guided manual registration (no unattended filing).
10. **[Legal C5]** Add DPA + subprocessor list + AI-transparency disclosure gates to `spec-onboarding-plg` and `spec-ai-copilot-messaging`.
11. **[Financial 1]** Tighten infra upgrade trigger to "first real guest check-in via platform"; prove GH Actions cron fires Alloggiati + GDPR jobs in the $0 window.
12. **[Financial 2]** Relabel ARPU €180–200 launch / €275 Y3; fix Y1 ARR floor ~€90k; reconcile vs €4–8/unit pricing.
13. **[Financial 3]** Make AI fair-use caps a hard product constraint (cheap-model default + confidence-gated routing + caching + overage metering) → GM ≥80%.

**Revised proposal for Round 2**: Builder revises `draft.md` → `draft-v2.md` incorporating all 13 conditions. Validators re-vote on the revised draft. The objections are complementary and non-conflicting; convergence is expected in Round 2.
