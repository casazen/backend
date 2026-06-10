---
pattern: builder-validator
protocol: deliberative-voting
topic: |
  Design a go-to-market and product roadmap for CasaZen as an AI-powered direct booking &
  rental OS (short-term + long-term rentals), grounded in
  Sessions/market-analysis-2026/AI-short/long-term-platform.md
  ("Piattaforma AI per affitti brevi/lunghi e direct booking"), current codebase and
  documentation. Deliverables: (1) full business plan, (2) long-term shared implementation
  plan from current state to sellable production platform, (3) macro-specs in Sessions/specs/
  anchored to current implementation. Include Italian legal/compliance path (CIN D.L. 145/2023,
  GDPR, Alloggiati Web, tourist tax, company formation) and lawful cost-minimization
  strategies (grants, tax incentives, freemium hosting, phased licensing). Position per market
  analysis: democratic SaaS 1–500 units, subscription-first, AI copilot full lifecycle,
  direct booking focus.
max_rounds: 4
output_style: standard
devils_advocate: true
avatars_enabled: false
setup_date: 2026-06-05
council_path: councils/casazen-platform-launch
agents:
  - slug: gtm-strategist
    role: GTM Strategist (Builder)
    skill_path: .claude/skills/council-gtm-strategist/SKILL.md
    archetype: market-analyst
  - slug: product-architect
    role: Product Architect (Validator)
    skill_path: .claude/skills/council-product-architect/SKILL.md
    archetype: architect
  - slug: legal-compliance
    role: Legal & Compliance Advisor (Validator)
    skill_path: .claude/skills/council-legal-compliance/SKILL.md
    archetype: legal-advisor
  - slug: financial-strategist
    role: Financial Strategist (Validator)
    skill_path: .claude/skills/council-financial-strategist/SKILL.md
    archetype: financial-controller
---

## Council Summary

**Scenario**: Produce CasaZen's go-to-market strategy and product roadmap — from current PMS implementation to a sellable, production-ready AI rental OS. Ground all recommendations in the market analysis document and existing codebase/docs.

**Pattern**: builder-validator — GTM Strategist (builder) drafts the integrated business plan + roadmap + macro-spec index; three validators (Product Architect, Legal & Compliance, Financial Strategist) challenge feasibility, compliance, and economics.

**Protocol**: deliberative-voting — `PROPOSE | OBJECT | APPROVE | ABSTAIN | REJECT`. Consensus = all non-abstaining APPROVE.

**Output template**: `draft-and-review` → primary session folder `Sessions/casazen-platform-launch/`

**Expected deliverables** (written by coordinator on consensus):

| Artifact | Path |
|----------|------|
| Business plan | `Sessions/casazen-platform-launch/business-plan.md` |
| Implementation roadmap (long-term) | `Sessions/casazen-platform-launch/implementation-roadmap.md` |
| Council decision record | `Sessions/casazen-platform-launch/decision.md` |
| Macro-specs (one per roadmap phase/feature) | `Sessions/specs/spec-{slug}.md` |

**Session slug**: `casazen-platform-launch`

**Coexistence**: This council lives in `councils/casazen-platform-launch/`. The SDLC design council remains at `council/` unchanged.

**Devil's Advocate**: enabled — post-deliberation review challenges the final output before consolidation.

**Launch**: Use `council-launch` with config path `councils/casazen-platform-launch/config.md` and session `Sessions/casazen-platform-launch/`.
