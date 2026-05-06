# Italian Regulatory Compliance

**Domain**: Short-term rental Italy — D.L. 145/2023 (CIN), Alloggiati Web, tourist tax, GDPR, cedolare secca

## Agents
- `regulatory_agent`: monitors Italian gov sources
- `analyzer_agent`: gaps vs codebase → `github_agent`: creates issues

## Rules
- **CIN**: format `IT-XXXXX-XXXXXXXXXX`, validate + store per property (D.L. 145/2023)
- **Guest data**: GDPR-compliant, Alloggiati Web integration, data retention applies
- **Tourist tax**: regional rates in `TaxRate` entity — NEVER hardcode

Check `@.claude/context/regulations/` before implementing compliance features.
