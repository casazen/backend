---
pattern: hub-and-spoke
protocol: deliberative-voting
topic: "Define the strategic next steps for CasaZen (backend + frontend) to disrupt the property management software market — both short-term rentals and long-term leases — by leveraging AI. The council will audit the current codebase, backlog, and one-year roadmap, then challenge existing assumptions and propose a bold market positioning and development pivot that maximizes competitive advantage."
max_rounds: 4
output_style: standard
devils_advocate: true
devils_advocate_mode: brief
setup_date: 2026-05-26
agents:
  - slug: ai-native-market-strategist
    role: AI-Native Market Strategist
    skill_path: .claude/skills/council-ai-native-market-strategist/SKILL.md
    archetype: market-analyst
  - slug: tech-architect
    role: Technical Architect
    skill_path: .claude/skills/council-tech-architect/SKILL.md
    archetype: architect
  - slug: product-strategist
    role: Product Strategist
    skill_path: .claude/skills/council-product-strategist/SKILL.md
    archetype: product-analyst
  - slug: financial-controller
    role: Financial Controller
    skill_path: .claude/skills/council-financial-controller/SKILL.md
    archetype: financial-controller
  - slug: regulatory-moat-strategist
    role: Regulatory Moat Strategist
    skill_path: .claude/skills/council-regulatory-moat-strategist/SKILL.md
    archetype: custom
---

## Council Summary

**Scenario**: CasaZen strategic disruption — property management SaaS, Italian market, AI-leveraged.

The council must audit the current CasaZen codebase state and backlog, challenge the existing iterative roadmap, and produce a bold strategic direction with a concrete 12-month roadmap for capturing a defensible market segment using AI.

**Output template**: `decision` (hub-and-spoke → decision.md with user stories, acceptance criteria, architectural decisions, and deliberation trail)

**Session slug convention**: `casazen-market-disruption-strategy` (kebab-case, max 48 chars)

**Devils Advocate**: enabled — brief mode (max 5 challenges, no extended reasoning)
