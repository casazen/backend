# Runbook: CIN format and data migration

Task CO-01 (audit defects A5-05, R-02). The code applies it by itself: this page explains what changes for the
data already in the database, how to check it after the deploy and what hosts see.

## The rule

| Step | Behaviour | Code |
|---|---|---|
| Normalize | Remove every whitespace (non-breaking spaces too) and every hyphen/dash (`-`, U+2010–U+2015, U+2212), upper case. Nothing left means "no CIN". Other characters (`.`, `/`, a `CIN:` prefix) are kept, so the value is rejected | `Casazen.Core/Regulatory/CinFormat.cs` → `Normalize` |
| Validate | `^IT\d{6}[A-Z0-9]{2}[A-Z0-9]{1,8}$` on the normalized value (ASCII digits), and not the old invented format (`^IT\d{15}$`, i.e. `IT-12345-0123456789` normalized) | `CinFormat.IsValid` |
| Store | Always the normalized form (`IT058091C27G5FFZDZ`), on create, update and `PUT /api/properties/{id}/cin` | `PropertyService` |
| Status | Computed on every read (`Valid` / `Missing` / `Invalid`), never stored | `CinFormat.GetStatus`, `CinComplianceRules.ResolveStatus` |
| Frontend | Same rule, same regexes | `src/lib/cin-format.ts` (frontend repo) |

Official composition (MiTur interoperability decree prot. 16726 of 06/06/2024): `IT` + ISTAT province (3 digits) +
ISTAT comune (3 digits) + ISTAT category (2 characters) + random string (at most 8). Sources and real examples:
`.claude/context/regulations/cin.md`, section "Formato verificato (2026-09)".

API errors:

| Case | Response |
|---|---|
| Wrong format on any property endpoint | `400 validation_error`, field `CinCode`, message `CinInvalidFormat` (IT/EN) |
| Wrong format reaching the service | `422 invalid_cin_format` |
| CIN already used by another property (`PUT .../cin`) | `409 duplicate_cin` |

## Data migration `NormalizeCinCodes`

EF migration `20260923230539_NormalizeCinCodes`, data only (no schema change). It runs with the other migrations
when the backend starts on Railway; no manual step is needed.

- Every non-null `Properties.CinCode` is rewritten in normalized form; a value that becomes empty (`'  '`, `'-'`) becomes `NULL`.
- **Nothing is deleted.** CINs that are still not in the official format, above all the old `IT-12345-0123456789`
  that the previous validation forced hosts to type, stay in the column and are reported as **invalid**.
- `Down` is a no-op: the original spacing and case are not kept.
- The SQL is `NormalizeCinCodes.NormalizeSql`; `NormalizeCinCodesPostgresIntegrationTests` checks it against `CinFormat.Normalize` on PostgreSQL.

### Check after the deploy (test, then production)

Read-only queries on the environment schema (`casazen_test` / `casazen_prod`):

```sql
-- CINs still containing spaces, hyphens or lower case (expected: 0 rows)
SELECT "Id" FROM "Properties" WHERE "CinCode" ~ '[[:space:]a-z-]';

-- CINs that the new rule marks invalid, by org (to contact the hosts)
SELECT "OrgId", count(*) FROM "Properties"
WHERE "CinCode" IS NOT NULL
  AND ("CinCode" !~ '^IT[0-9]{6}[A-Z0-9]{2}[A-Z0-9]{1,8}$' OR "CinCode" ~ '^IT[0-9]{15}$')
GROUP BY "OrgId";
```

The same list is in the app: **Admin → CIN** (`/admin/cin`, filter "invalid") and, for each host, the CIN
compliance page.

## What hosts and guests see

- **Host:** properties with an old-format or otherwise wrong CIN show "CIN non valido" in the property list, the
  detail page and the CIN compliance page. The activation wizard keeps the CIN step pending until a valid CIN is saved.
- **Guest (public site, search):** only a valid CIN is shown, as plain text ("CIN IT058091C27G5FFZDZ"). No
  "invalid" or "missing" badge is shown to guests.
- Publication rules are unchanged (they belong to task CO-06).

## Not done yet

- **ISTAT check (warning only):** `CinFormat.HasIstatComuneMismatch` compares the ISTAT code inside the CIN with a
  trusted ISTAT code of the property. It is not wired to any endpoint because properties only have a free-text
  city and `ItalianComuneRegistry` holds 12 comuni. Wire it (as a non-blocking warning) once the ISTAT registry
  of task SU-04 gives each property a reliable code.
- The format of the category (2 characters) and the variable length of the random part are taken from the
  official composition, read through search-engine extracts (see `cin.md`, "Limite della verifica"). Re-check
  against the decree text before relying on stricter rules.
