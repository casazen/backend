# Italian Regulatory Compliance

**Domain**: Short-term rental Italy — D.L. 145/2023 (CIN), Alloggiati Web, tourist tax, GDPR, cedolare secca

## Agents
- `regulatory_agent`: monitors Italian gov sources
- `analyzer_agent`: gaps vs codebase → `github_agent`: creates issues

## Rules
- **CIN** (D.L. 145/2023 art. 13-ter; format verified 2026-09 from official BDSR sources):
  - Format: `IT` + 6-digit ISTAT comune code (3 province + 3 comune) + 2-char ISTAT category (always letter+digit in real CINs, e.g. `A1`, `C2`) + random alphanumeric string (max 8 chars).
  - Typical length is 18, e.g. `IT058091C27G5FFZDZ`. No separators, no checksum.
  - **Single source of truth: `Casazen.Core/Regulatory/CinFormat.cs`** (normalize, validate, status). Frontend mirror: `src/lib/cin-format.ts`. Never write another CIN regex.
  - Normalize first: trim, remove all whitespace and hyphens/dashes, `ToUpperInvariant`. Then validate with `^IT\d{6}[A-Z0-9]{2}[A-Z0-9]{1,8}$` (ASCII digits) and reject the old invented format (`^IT\d{15}$` once normalized). Store and display the normalized form.
  - The CIN status (valid / missing / invalid) is computed on read, never stored. Invalid CINs are kept, not deleted.
  - Show the "invalid" state to the host only; guest-facing pages show the CIN code only when it is valid.
  - If the embedded ISTAT code differs from the property's comune, show a non-blocking warning only (`CinFormat.HasIstatComuneMismatch`): the CIN never changes after a relocation or reclassification. Needs a trusted ISTAT code on the property (not the free-text city).
  - Never derive a CIN from a regional code (CIR/CIS/CIPAT/CIU).
  - The old `IT-XXXXX-XXXXXXXXXX` format is WRONG.
  - Details and sources: `.claude/context/regulations/cin.md` § "Formato verificato (2026-09)"; runbook `docs/runbooks/cin-format.md`
- **Guest data**: GDPR-compliant, Alloggiati Web integration, data retention applies
- **Tourist tax**: regional rates in `TaxRate` entity — NEVER hardcode

## Loading Regulatory Context (lazy — load only what you need)
1. Read `.claude/context/regulations/_index.md` for a topic overview
2. Load the **single** relevant file (e.g. `cin.md`, `gdpr.md`) — do NOT load the whole directory
3. Load additional files only if the task explicitly spans multiple regulations
