---
pattern: builder-validator
protocol: deliberative-voting
topic: |
  Design and scaffold a 6-stage AI-SDLC for CasaZen (.NET 10 backend + React 19 frontend)
  where each stage has a dedicated council of specialist agents and a quality harness loop.
  Stages: Planning → Design → Development → Review → Release → Operations.
  Italian regulatory compliance (CIN D.L.145/2023, GDPR, Alloggiati Web, tourist tax)
  must be enforced at the correct stages with verifiable, executable quality gates — not
  as a standalone agent, but embedded in harness checks and domain context.
max_rounds: 4
output_style: standard
devils_advocate: true
setup_date: 2026-06-02
agents:
  - slug: sdlc-architect
    role: SDLC Architect
    skill_path: .claude/skills/council-sdlc-architect/SKILL.md
    archetype: architect
  - slug: process-quality-engineer
    role: Process Quality Engineer
    skill_path: .claude/skills/council-process-quality-engineer/SKILL.md
    archetype: qa-strategist
  - slug: security-engineer
    role: Security Engineer
    skill_path: .claude/skills/council-security-engineer/SKILL.md
    archetype: security-engineer
  - slug: platform-devops-validator
    role: Platform DevOps Validator
    skill_path: .claude/skills/council-platform-devops-validator/SKILL.md
    archetype: devops-engineer
---

## Council Summary

**Scenario**: Design a 6-stage AI-SDLC for the CasaZen platform (backend: .NET 10 / ASP.NET Core; frontend: React 19 / TypeScript / Vite). The council's deliverable is the complete SDLC scaffold: six stage folders under `.claude/sdlc/`, each with a harness loop, a coordinator, and specialist agents tailored to that stage. Italian regulatory compliance (CIN, GDPR, Alloggiati Web, tourist tax) is embedded in domain context and harness quality gates — not modelled as a separate agent.

**Pattern**: builder-validator — SDLC Architect (builder) produces the full stage design; three validators (Process Quality, Security, DevOps) validate each stage against their domain criteria before the design is approved.

**Protocol**: deliberative-voting with votes `PROPOSE | OBJECT | APPROVE | ABSTAIN | REJECT`. Consensus = all non-abstaining APPROVE.

**Output template**: `draft-and-review` → writes `Sessions/casazen-ai-sdlc-design/decision.md`.

**Session slug convention**: `casazen-ai-sdlc-design`

**Devil's Advocate**: enabled — post-deliberation review challenges the final SDLC design before consolidation.
