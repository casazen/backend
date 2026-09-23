# Italian Regulatory Compliance

**Domain**: Short-term rental Italy — D.L. 145/2023 (CIN), Alloggiati Web, tourist tax, GDPR, cedolare secca

## Agents
- `regulatory_agent`: monitors Italian gov sources
- `analyzer_agent`: gaps vs codebase → `github_agent`: creates issues

## Rules
- **CIN** (D.L. 145/2023 art. 13-ter; format verified 2026-09 from official BDSR sources):
  - Format: `IT` + 6-digit ISTAT comune code (3 province + 3 comune) + 2-char ISTAT category (letter+digit, e.g. `A1`, `C2`) + random alphanumeric string (max 8 chars).
  - Typical length is 18, e.g. `IT058091C27G5FFZDZ`. No separators, no checksum.
  - Normalize first: trim, remove spaces and hyphens, `ToUpperInvariant`. Then validate with `^IT\d{6}[A-Z]\d[A-Z0-9]{1,8}$`. Store and display the normalized form.
  - If the embedded ISTAT code differs from the property's comune, show a non-blocking warning only: the CIN never changes after a relocation or reclassification.
  - Never derive a CIN from a regional code (CIR/CIS/CIPAT/CIU).
  - The old `IT-XXXXX-XXXXXXXXXX` format is WRONG.
  - Details and sources: `.claude/context/regulations/cin.md` § "Formato verificato (2026-09)"
- **Guest data**: GDPR-compliant, Alloggiati Web integration, data retention applies
- **Tourist tax**: regional rates in `TaxRate` entity — NEVER hardcode

## Loading Regulatory Context (lazy — load only what you need)
1. Read `.claude/context/regulations/_index.md` for a topic overview
2. Load the **single** relevant file (e.g. `cin.md`, `gdpr.md`) — do NOT load the whole directory
3. Load additional files only if the task explicitly spans multiple regulations
