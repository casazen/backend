---
round: 2
role: gtm-strategist
vote: PROPOSE
---

## GTM Strategist — Round 2 Response

**Vote**: PROPOSE

**Reasoning**:
draft-v2 resolves all 13 conditions and opens with a "Changes from Round 1" table mapping each condition # to its concrete change. Phase 0 is rebuilt on the Coordinator-verified baseline (22 entities / 16 controllers / 10 jobs / Supabase PostgreSQL) and now explicitly enumerates the existing LTR lease+RLI+e-sign subsystem and the AppContext/UserContextMembership/Role/RolePermission context-RBAC. LTR is rescoped from greenfield to complete+verify and moved to a parallel Phase 1.5 whose only true gap is the recurring-rent ledger. The direct-booking and multi-tenant specs are split per PA; legal conditions land as concrete spec gates (corrected SRLS/SRL-vs-forfettario tree, IVA/OSS + SDI billing entry gate, Stripe Connect operator-MoR, assisted-not-unattended RLI, DPA/subprocessors/AI-Act); financial conditions land as the check-in-driven infra trigger, relabeled ARPU (€180–200 launch / €275 Y3, Y1 floor ≈ €90k), and hard AI fair-use caps (≤10–15% ARPU, GM ≥80%).

**Details**:
- draft-v2.md written: yes (original draft.md untouched)
- Updated roadmap phases: Phase 0 (verified baseline), Phase 1 (direct-booking split + tenant-boundary + saas-billing + onboarding), Phase 1.5 (LTR complete+verify, parallel), Phase 2 (unified inbox + AI copilot + org seats), Phase 3 (supplier marketplace + Google Vacation Rentals), Phase 4 (enterprise scale + EU ES/FR compliance)
- Final macro-spec slug list (post-split, 17 specs):
  - **P1**: spec-public-booking-readmodel, spec-direct-checkout, spec-branded-booking-site, spec-tenant-boundary, spec-saas-billing, spec-onboarding-plg
  - **P1.5**: spec-ltr-recurring-rent, spec-ltr-frontend, spec-ltr-verification, spec-ltr-rli-registration (rescoped/assisted)
  - **P2**: spec-unified-inbox, spec-ai-copilot-messaging, spec-org-seats-collaboration
  - **P3**: spec-supplier-marketplace, spec-google-vacation-rentals
  - **P4**: spec-enterprise-scale, spec-eu-compliance-es-fr
- Condition coverage: 13/13 (condition 2↔9 reconciled as complementary: verify existing RLI flow + reframe as operator-attended assisted registration)
- Residual open question for Product Architect: does `spec-tenant-boundary`'s `OrgId` FK on `LeaseContract`/`Payment` impose a merge-order dependency on the parallel Phase 1.5 LTR specs (pre- or post-`OrgId` schema)? Single touch-point between the two parallel tracks.
