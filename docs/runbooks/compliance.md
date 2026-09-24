# Runbook: compliance status of the properties (suspension, nightly check, historic recalculation)

Task CO-06, audit defects A5-20 (P1) and A5-36 (P2). Until CO-06 the compliance status of a property was computed only
by the activation wizard: a host could delete the CIN (`PUT /api/properties/{id}/cin` with `null`), a required document
or the safety checklist of an **active** property and it stayed published; `Suspended` was never set; the backfill of
`AddPropertyComplianceStatus` had published every active property with any CIN, without documents or checklist.

Nothing has to be configured. The only choice for the product owner is whether the **first** recalculation emails the
hosts (section 5), to be made before the release reaches production.

## 1. Rules

One evaluation, used everywhere: `IPropertyComplianceStatusService.GetBlockingStepsAsync`
(`Casazen.Infrastructure/Services/PropertyComplianceStatusService.cs`). The activation wizard (CO-07) has no copy of
its own: its four blocking steps and their blocker codes are this evaluation.

| Blocking step | Blocker codes | Rule |
|---|---|---|
| `base-data` | `activation_base_data_incomplete` | name, address, city, max guests > 0, nightly rate > 0 |
| `cin` | `activation_cin_missing`, `activation_cin_invalid` | `CinFormat` (CO-01, [cin-format.md](cin-format.md)) |
| `documents` | `activation_documents_missing` | `Compliance:RequiredDocuments` (in practice the `default` list, `CinCertificate`: A5-19 is open in SU-04) |
| `safety` | `safety_*` | `SafetyChecklistRules` (CO-07): required items, facts and the final confirmation with the current text version |

The tourist tax and the iCal feed are warnings: they never block, never suspend.

| From | To | When |
|---|---|---|
| `Pending` | `Active` | the host completes the activation wizard (`POST …/compliance/activation/complete`, terms accepted) and no blocker is left |
| `Active` | `Suspended` | a blocker appears: at once after a change made through the API, at the latest at the nightly check |
| `Suspended` | `Active` | no blocker is left: at once after the change that completes the requirements (CIN, document, checklist, base data), at the latest at the nightly check; also the host's activation |
| `Suspended` | `Suspended` | some blocker is still there: nothing changes, no new email |

**Reactivation.** A suspended property is published again **as soon as its requirements are complete again**, without
a new activation: the host had already activated it and accepted the terms, the suspension only covers the missing
requirement. A `Pending` property is never published by a re-evaluation: its first publication is always the host's
activation. The frontend needs no change: the wizard shows the current status and blockers after every save.

### What triggers a re-evaluation

| Change | Code |
|---|---|
| CIN saved or removed (`PUT /api/properties/{id}/cin`) | `PropertyService.UpdatePropertyCinAsync` |
| Property updated (`PUT /api/properties/{id}`: base data, city, CIN sent with the PATCH) | `PropertyService.UpdatePropertyAsync` |
| Document uploaded or deleted | `PropertyDocumentService` |
| Safety checklist saved (`PUT …/compliance/safety-checklist`) | `PropertySafetyChecklistService.SaveAsync` |
| Activation completed with blockers left | `ComplianceWizardService.CompleteActivationAsync` → `ActivateAsync` |
| Every night, every active or suspended property | job `property-compliance-check` ([hangfire.md §10](hangfire.md#10-property-compliance-check-co-06)) |

Only an `Active` property (suspension) or a `Suspended` one (reactivation) can change on a re-evaluation; a pending one
is left as it is.

Saving the safety checklist **without** the final confirmation clears the confirmation (CO-07, SC-08): on an active
property that is a missing requirement, so the property is suspended until the host saves it again with the
confirmation (which reactivates it). The checklist form of the wizard asks for the confirmation at every save.

## 2. What "suspended" means

| Surface | Behaviour |
|---|---|
| Public property page, search, org booking site | Not listed, `404` (`PublicListing.IsPublished`: active **and** `ComplianceStatus = Active`) |
| Public availability (BK-05) | `404 public_property_not_found` |
| Direct booking / checkout | Refused (`404`), like a pending property |
| Confirmed bookings | **Kept**: nothing is cancelled or refunded; check-in, Alloggiati and check-out go on |
| iCal export to the OTAs (`/api/public/ical/{token}`) | Still served, exactly as for a never-published property: it only lists busy nights (confirmed stays, blocks), never availability. Stopping it would free on Airbnb/Booking.com the nights of the stays that remain valid (double bookings) |
| Host console | Wizard: `complianceStatus: "Suspended"`, `suspendedAt`, `suspensionReasons` (blocker codes at the suspension) and the current blockers in `steps`; cockpit: listed among the properties to activate; check-out wizard: "compliance" step as a warning |

Stored on `Properties`: `ComplianceSuspendedAt` and `ComplianceSuspensionReasons` (blocker codes at the suspension,
`text[]`), cleared by the reactivation; `ComplianceCheckedAt` = last evaluation of an active or suspended property
(`NULL` = never evaluated, i.e. published before CO-06). Migration `AddPropertyComplianceSuspension` adds the three columns, nullable, without data
changes.

## 3. Email to the host

Template `property-compliance-suspended` (`EmailTemplates.PropertyComplianceSuspended`, texts IT/EN in `EmailTexts`),
to the org contact address (`Org.ContactEmail`), queued on Hangfire (`IEmailQueue` → `EmailDeliveryJob`, FD-13) after
the commit. Content: property name, one line per missing step (base data, CIN, documents, safety checklist), "confirmed
bookings remain valid", button to the activation wizard (`App__PublicSiteBaseUrl/app/short-rent/properties/{id}/activation`).
Italian by default ([email.md](email.md#language)).

- **Once per suspension.** Only the evaluation that moves the property from `Active` to `Suspended` queues it, under the
  PostgreSQL advisory lock `PropertyComplianceStatus` (1030) on the property: a host request and the nightly job, or
  two requests, never send it twice. A suspended property that is still incomplete stays as it is, so the next nights
  send nothing.
- A delivery that fails is retried by `EmailDeliveryJob`; the suspension is never undone because of the email.
- No email when the property goes back to `Active`: the reactivation follows the host's own change and the console
  shows it at once. A reactivation by the nightly check (e.g. a required-document list made shorter) is only logged
  (`Property … compliance status Suspended -> Active`).

## 4. Nightly check

Recurring job `property-compliance-check`, `0 4 * * *` UTC (05:00/06:00 in Italy), `[DisableConcurrentExecution]` plus
the session advisory lock `PropertyComplianceCheckRun` (1031): the job and the one-shot command never run together
(a second run logs `Property compliance check skipped: another run is in progress`). Every `Active` or `Suspended`
property is evaluated in turn (suspension or reactivation); a property that fails is logged
(`Compliance check of property … failed`) and the run goes on.

Why a nightly check when every change re-evaluates at once: no requirement of the current model expires with time (the
documents and the checklist have no expiry that blocks), so it is a safety net for what no request sees: a new
required-document list or rule deployed, a new declaration text (`SafetyChecklistRules.DeclarationTextVersion`: every
checklist confirmed with the old text stops being complete), a row changed outside the API. **A change of those rules
suspends, the following night, every active property that no longer meets them, with one email each**: announce it
to the hosts before deploying it (same procedure as section 5). A rule made looser reactivates, the following night,
the suspended properties that now meet it.

Summary line at the end of each run:
`Property compliance check completed: N active or suspended properties checked, S suspended, R reactivated, E hosts notified, F failed, blockers …`.

## 5. Recalculation of the historic properties (A5-36)

The first run of `property-compliance-check` after the deploy of CO-06 **is** the recalculation: every property still
`Active` is evaluated with the rules above. Every property published before CO-06 has `ComplianceCheckedAt = NULL`;
its first evaluation emails the host **only if** `Compliance__StatusCheck__NotifyOnFirstCheck=true` (default `false`,
so the hosts are not surprised by an email they were not told about). After the first evaluation every later
suspension emails the host, whatever the setting.

### 5.1 Expected impact

Since CO-07 the safety checklist must be (re)confirmed with the new text: the migration `AddDl145SafetyChecklist`
imported the old answers without confirmation. **Every property published before CO-07 whose host has not saved and
confirmed the new checklist will be suspended**, plus the ones without the CIN certificate or with a missing or invalid
CIN (the backfill published any CIN). Expect most, possibly all, of the active properties.

Before the release, on the target schema (`casazen_test`, then `casazen_prod`), read-only:

```sql
-- Active properties and what the first check will find missing. Lower bound: the checklist items themselves
-- (extinguishers, detectors, …) are evaluated by the app only; the dry run below gives the exact numbers.
SELECT count(*)                                                              AS active,
       count(*) FILTER (WHERE NOT (cin_ok AND doc_ok AND checklist_ok AND base_ok)) AS to_suspend_at_least,
       count(*) FILTER (WHERE NOT checklist_ok)                               AS checklist_not_confirmed,
       count(*) FILTER (WHERE NOT doc_ok)                                     AS no_cin_certificate,
       count(*) FILTER (WHERE NOT cin_ok)                                     AS cin_missing_or_invalid,
       count(*) FILTER (WHERE NOT base_ok)                                    AS base_data_incomplete
FROM (
  SELECT p."Id",
         (p."CinCode" ~ '^IT[0-9]{6}[A-Z0-9]{2}[A-Z0-9]{1,8}$' AND p."CinCode" !~ '^IT[0-9]{15}$') AS cin_ok,
         EXISTS (SELECT 1 FROM casazen_prod."PropertyDocuments" d
                 WHERE d."PropertyId" = p."Id" AND d."DocumentType" = 'CinCertificate')          AS doc_ok,
         EXISTS (SELECT 1 FROM casazen_prod."PropertySafetyChecklists" c
                 WHERE c."PropertyId" = p."Id" AND c."ConfirmedAt" IS NOT NULL
                   AND c."ConfirmedTextVersion" = '2026-09-v1')                                  AS checklist_ok,
         (btrim(p."Name") <> '' AND btrim(p."Address") <> '' AND btrim(p."City") <> ''
          AND p."MaxGuests" > 0 AND p."NightlyRate" > 0)                                        AS base_ok
  FROM casazen_prod."Properties" p
  WHERE p."ComplianceStatus" = 1   -- Active
) x;

-- Hosts to warn: contact address and number of their active properties missing something
SELECT o."Id", o."DisplayName", o."ContactEmail", count(*) AS properties
FROM casazen_prod."Properties" p JOIN casazen_prod."Orgs" o ON o."Id" = p."OrgId"
WHERE p."ComplianceStatus" = 1
  AND NOT EXISTS (SELECT 1 FROM casazen_prod."PropertySafetyChecklists" c
                  WHERE c."PropertyId" = p."Id" AND c."ConfirmedAt" IS NOT NULL
                    AND c."ConfirmedTextVersion" = '2026-09-v1')
GROUP BY o."Id", o."DisplayName", o."ContactEmail"
ORDER BY properties DESC;
```

Exact numbers, per blocker, with the same code as the job (nothing written, no email), once CO-06 is deployed:

```bash
# inside the container (railway ssh), or locally with the target environment's variables
dotnet Casazen.Web.dll compliance:recalculate --dry-run
# locally from the repo:
dotnet run --project Casazen.Web -- compliance:recalculate --dry-run
```

Output: `compliance:recalculate (dry run, nothing changed): checked=… suspended=… reactivated=… hostsNotified=… failed=… blockers=activation_documents_missing:…,safety_confirmation_missing:…`.
Like `storage:migrate-legacy`, the command applies pending EF migrations first: run it locally only against a schema
that already has the release deployed.

### 5.2 Warning the hosts first (recommended)

1. On `test`: deploy, run the dry run, check the numbers and the email (section 7).
2. Before merging the release to `main`: run the SQL above on `casazen_prod`, write to the hosts of the second query
   (outside CasaZen, from the product owner's mailbox or newsletter tool): what changes, that the listing on the direct
   booking site will be suspended until the new safety checklist is confirmed (and the CIN and CIN certificate are in
   place) and published again as soon as it is, that confirmed bookings are not touched, and the date of the release.
3. Choose the email of the first check:
   - hosts already warned → leave `Compliance__StatusCheck__NotifyOnFirstCheck` unset (`false`);
   - not warned, or a reminder is wanted → Railway `production` → service `casazen/backend` → Variables →
     `Compliance__StatusCheck__NotifyOnFirstCheck=true` **before** the deploy (the first night after the deploy sends
     one email per suspended property).
4. Merge the release. The recalculation runs the following night at 04:00 UTC; to run it right away (e.g. at the time
   announced to the hosts): `dotnet Casazen.Web.dll compliance:recalculate` in the Railway shell. Exit code 0 = done,
   2 = another run in progress, 1 = error or some property failed (see the logs).
5. After the recalculation the setting has no more effect on those properties (they have `ComplianceCheckedAt`); it
   can be removed.

Rollback: a property suspended by mistake (a wrong rule) is reactivated by the nightly check once the rule is fixed
and deployed, or right away with `compliance:recalculate`; there is no other bulk "unsuspend". Reverting the migration
only drops the three columns (the statuses stay).

## 6. Configuration (Railway variables, optional)

| Variable | Default | Meaning |
|---|---|---|
| `Compliance__StatusCheck__NotifyOnFirstCheck` | `false` | Email the host when the first evaluation of a property published before CO-06 suspends it (section 5) |
| `Compliance__RequiredDocuments__…` | `default: CinCertificate` | Required documents (unchanged; a new list applies at the next night) |
| `App__PublicSiteBaseUrl` | — | Base of the button of the email (already required by FD-13) |

## 7. Checks after the deploy (test, then production)

1. Active test property with every requirement: remove the CIN from the property page → the wizard shows
   "Suspended", the public page `GET /api/properties/{id}/public` answers `404`, the host receives "Annuncio sospeso"
   with the CIN line, the confirmed bookings are still there. Enter the CIN again → `Active` at once, public page
   `200`, no second email.
2. Logs of the first night (or of the command): the summary line of section 4; no `failed`.
3. SQL:

```sql
-- Status of the properties after the recalculation (Pending 0, Active 1, Suspended 2)
SELECT "ComplianceStatus", count(*), count(*) FILTER (WHERE "ComplianceCheckedAt" IS NULL) AS never_checked
FROM casazen_prod."Properties" GROUP BY 1 ORDER BY 1;

-- Suspensions per reason
SELECT reason, count(*) FROM casazen_prod."Properties", unnest("ComplianceSuspensionReasons") AS reason
WHERE "ComplianceStatus" = 2 GROUP BY 1 ORDER BY 2 DESC;

-- Expected 0 rows: an active property never evaluated after the first night
SELECT "Id" FROM casazen_prod."Properties" WHERE "ComplianceStatus" = 1 AND "ComplianceCheckedAt" IS NULL;
```

## 8. Not done / open

- The web app shows the "Suspended" badge and the current blockers, but not yet `suspendedAt` / `suspensionReasons`
  nor a warning on the checklist form that saving without confirmation suspends a published property (frontend
  follow-up).
- No email on the reactivation (section 3).
- The iCal export keeps exporting the busy nights of a suspended property (section 2). Whether it should also close the
  whole calendar on the OTAs while the CIN is missing is a product decision (DUBBI CO-06).
- Required documents per region: A5-19 (SU-04).
