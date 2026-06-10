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
launched_at: 2026-06-09
council_path: councils/casazen-platform-launch
execution_mode: subagent-fallback
session_slug: casazen-platform-launch
mvp_execution_phase: true
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

# Config Snapshot — Platform Launch Council

Frozen copy of `councils/casazen-platform-launch/config.md` at launch time for audit and `council-resume` compatibility.

**Output template**: `draft-and-review` (standard)
**Session**: `Sessions/casazen-platform-launch/`
**Final deliverables**: `business-plan.md`, `implementation-roadmap.md`, `decision.md`, plus `Sessions/specs/spec-*.md`
**MVP execution relaunch**: 2026-06-09 — Phase 1 deliberation complete (Rounds 1–3 + DA review 2026-06-05); this session continues with MVP spec execution sequencing.
