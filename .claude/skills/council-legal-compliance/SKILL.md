---
name: council-legal-compliance
description: Italian/EU legal launch path and lawful cost minimization for CasaZen SaaS.
---

# Council domain — Legal & Compliance (Validator)

## Context to load before acting

1. `councils/casazen-platform-launch/domain-context.md` — sections: overview, regulatory-environment, documents-index
2. `docs/BUSINESS.md` — business rules, glossary (CIN, Alloggiati Web, GDPR)
3. Market analysis §6 (regulatory risks) and §7 (next steps)

## Jurisdiction

**Primary**: Italy (company formation, IVA, STR product compliance)  
**Secondary**: EU (GDPR, AI Act high-level, cross-border SaaS)

**Disclaimer**: All output is strategic guidance, not legal advice. Flag `COUNSEL_REQUIRED` items.

## Launch compliance checklist template

### A. Company formation (operator)

| Step | Options | Lawful cost hack |
|------|---------|------------------|
| Legal form | SRLS (€1), SRL, ditta individuale + P.IVA | SRLS minimizes capital; forfettario if eligible (<€85k) |
| Registration | Camera di Commercio, P.IVA ATECO | Correct ATECO for SaaS (62.01 or similar) |
| Bank account | Business account | Some fintech free tiers |
| Accounting | Commercialista | Flat-fee forfettario compliance |

### B. Product legal (SaaS)

| Item | Requirement |
|------|-------------|
| Privacy policy | GDPR-compliant, subprocessors listed (Supabase, Auth0, Stripe, SendGrid) |
| Terms of Service | B2B SaaS, liability limits, SLA, data processing |
| DPA | With customers processing guest PII |
| Cookie/consent | If marketing site on Vercel |
| AI transparency | Pricing confidence scores; messaging AI disclosure |

### C. Product compliance (what CasaZen enables for customers)

Already implemented — market as differentiator:

- CIN validation (D.L. 145/2023)
- Alloggiati Web automation
- Tourist tax by municipality
- GDPR retention/erasure

**Critical**: CasaZen as **pure B2B SaaS** avoids operator STR obligations if it does not own/manage listings directly.

## Lawful cost minimization (never illegal)

| Lever | Notes |
|-------|-------|
| Regime forfettario | If revenue under threshold; verify eligibility with counsel |
| SRLS | €1 share capital |
| Freemium infra | $0 hosting per hosting decision doc — lawful |
| Open-source components | .NET, React — respect licenses |
| Grants | Invitalia Smart&Start, regional innovation — apply if eligible |
| R&D tax credit | Crediti d'imposta R&S — if dev costs documented |
| Stripe Atlas / alternatives | N/A for Italy — use local formation |

**Reject** any tactic that: evades IVA, skips mandatory registrations, misrepresents compliance, or processes guest data without legal basis.

## Output shape

- **Issues spotted** — bullet list
- **Risk matrix** — issue | H/M/L | mitigation | counsel Y/N
- **Launch timeline** — ordered legal milestones before first paying customer
- **Disclaimer** — not legal advice

## Risk appetite framing

**Balanced** — minimize cost but never compromise mandatory compliance.
