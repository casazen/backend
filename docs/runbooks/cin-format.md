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

EF migration `20260923234340_NormalizeCinCodes`, data only (no schema change). It runs with the other migrations
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
- Publication (CO-06, [compliance.md](compliance.md)): a missing or invalid CIN blocks the activation; removing the CIN
  of an active property suspends it from the booking site at once; a valid CIN entered again publishes it again (when
  the other requirements are complete).

## CIN deadline and host alerts (CO-20)

Task CO-20 (audit defect A5-31). Before it the deadline was a constant (01/03/2026) in the code, the "days left" stopped
at 0, so from March 2026 the console said "the deadline is today" every day, and the daily alert only wrote a log line.

### The deadline is configuration, off by default

The date "01/03/2026 for existing operators" was **not found in any official source** (RS-2, `.claude/context/regulations/cin.md`,
"Note di verifica"): the official sources found give applicability from 02/11/2024 and 01/01/2025 as the term to obtain
the CIN. So the code has no date and never calls it a legal deadline.

| Variable (Railway) | Value | Default |
|---|---|---|
| `Cin__ExposureDeadline` | Date `yyyy-MM-dd` (Europe/Rome calendar), e.g. `2026-03-01`. Empty or missing = no deadline | empty (`appsettings.json`) |
| `Cin__AlertDaysBefore__0`, `__1`, … | Days before the deadline at which hosts are alerted, 1–366 | 30, 7, 1 |

A value that is not a `yyyy-MM-dd` date, or a threshold outside 1–366, **stops the startup** with a message naming the
variable (`CinOptionsValidator`). Code: `Casazen.Core/Options/CinOptions.cs`, `Casazen.Core/Regulatory/CinDeadline.cs`.

To set it (product owner decision): Railway → environment `test` → backend service → Variables → add
`Cin__ExposureDeadline`, redeploy, check the console (below), then the same in `production`. To remove it, delete the
variable or leave it empty.

### What hosts see (console, CIN compliance page and property list)

`GET /api/properties/cin-compliance` → `summary.deadlineStatus`, computed on the Europe/Rome day of the request:

| `deadlineStatus` | `deadline` / `daysUntilDeadline` | Banner (only with properties without a valid CIN) | Deadline card |
|---|---|---|---|
| `none` (no deadline) | `null` / `null` | "Il CIN è obbligatorio: va esposto all'esterno dell'immobile e indicato in ogni annuncio." | hidden |
| `upcoming` | date / days left (> 0) | "Mancano N giorni alla scadenza del …" | "Mancano N giorni" |
| `today` | date / `0` | "La scadenza del … è oggi." | "Oggi" |
| `passed` | date / negative | "La scadenza del … è superata: inserisci il CIN al più presto." | "Superata" |

The penalties line cites art. 13-ter, comma 9, D.L. 145/2023 (€800–8,000 for a missing CIN, €500–5,000 for not
displaying or stating it), as documented in `cin.md`. Texts in `src/i18n/locales/it.json` / `en.json` (`cin.banner.*`,
`cin.summary.*`) of the frontend.

### Daily alert (`cin-deadline-alert`, 08:00 UTC)

`CinDeadlineAlertJob` → `CinDeadlineAlertService` (`Casazen.Infrastructure/Services/CinDeadlineAlertService.cs`). It runs
after the nightly compliance check of CO-06 (`property-compliance-check`, 04:00 UTC).

- **Properties:** `IsActive` with compliance status `Pending` or `Active` and a CIN missing or not valid (`CinFormat`).
- **Stages**, each sent **once per property**: every `Cin__AlertDaysBefore` threshold reached (a run 10 days before the
  deadline sends the 30-day stage, saying "10 days left"), the deadline day, the day after it or later ("deadline
  passed", once). Without a deadline: **one** reminder of the obligation, without a date. Before the first threshold the
  run stops at once and logs nothing.
- **No duplicates:** each stage is claimed with a compare-and-set on the table `CinAlertStates` (one row per property:
  `Deadline`, `Stage`, `AlertCount`, `LastAlertAt`), so reruns, retries, a manual trigger or two runs at once never send
  it again. One run at a time: Hangfire `DisableConcurrentExecution` plus the PostgreSQL advisory lock
  `CinDeadlineAlertsRun` (1042).
- **No duplicate with the suspension (CO-06):** a `Suspended` property never gets the CIN alert, its host already has the
  "Annuncio sospeso" email. A published (`Active`) property without a valid CIN is first re-evaluated by CO-06: it is
  suspended and its host gets the CO-06 email only (none when it is the first evaluation of a historic property and
  `Compliance__StatusCheck__NotifyOnFirstCheck` is off, see [compliance.md](compliance.md)).
- **Email:** template `cin-deadline-alert` (`EmailTemplates.CinDeadlineAlert`, texts `CinDeadlineAlert_*` in
  `EmailTexts.resx` / `.en.resx`), in Italian, one per org to `Org.ContactEmail`, listing its properties of the stage,
  with the "Apri Conformità CIN" button to `/app/short-rent/compliance/cin` (from `App__PublicSiteBaseUrl`). Queued on
  Hangfire (FD-13). A delivery that fails after the claim is logged and not repeated.
- **Changes:** a different `Cin__ExposureDeadline` (or setting one after the reminder without date) starts the sequence
  again for the new date. A property whose CIN becomes valid gets nothing more; if its CIN is removed later, the stages
  already sent are not repeated (an active property is suspended by CO-06 with its own email).
- **Logs:** one `Information` line per run that sent something ("CIN deadline alert (passed, stage -1): N properties
  alerted, M emails queued"); errors per property or org with its id. No daily line when nothing is due.

Check (read-only, environment schema):

```sql
-- Stage reached per property (-1 = after the deadline or the reminder without date, 0 = deadline day)
SELECT "PropertyId", "Deadline", "Stage", "AlertCount", "LastAlertAt" FROM "CinAlertStates" ORDER BY "LastAlertAt" DESC;
```

The Hangfire dashboard can trigger `cin-deadline-alert` by hand: it sends only what is due and not yet sent.

## Not done yet

- **ISTAT check (warning only):** `CinFormat.HasIstatComuneMismatch` compares the ISTAT code inside the CIN with a
  trusted ISTAT code of the property. It is not wired to any endpoint because properties only have a free-text
  city and `ItalianComuneRegistry` holds 12 comuni. Wire it (as a non-blocking warning) once the ISTAT registry
  of task SU-04 gives each property a reliable code.
- The format of the category (2 characters) and the variable length of the random part are taken from the
  official composition, read through search-engine extracts (see `cin.md`, "Limite della verifica"). Re-check
  against the decree text before relying on stricter rules.
