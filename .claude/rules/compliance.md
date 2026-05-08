# Italian Regulatory Compliance

**Domain**: Short-term rental Italy — D.L. 145/2023 (CIN), Alloggiati Web, tourist tax, GDPR, cedolare secca

## Agents
- `regulatory_agent`: monitors Italian gov sources
- `analyzer_agent`: gaps vs codebase → `github_agent`: creates issues

## Rules
- **CIN**: format `IT-XXXXX-XXXXXXXXXX`, validate + store per property (D.L. 145/2023)
- **Guest data**: GDPR-compliant, Alloggiati Web integration, data retention applies
- **Tourist tax**: regional rates in `TaxRate` entity — NEVER hardcode

## Loading Regulatory Context (lazy — load only what you need)
1. Read `.claude/context/regulations/_index.md` for a topic overview
2. Load the **single** relevant file (e.g. `cin.md`, `gdpr.md`) — do NOT load the whole directory
3. Load additional files only if the task explicitly spans multiple regulations
