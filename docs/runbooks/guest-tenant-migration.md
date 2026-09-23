# Runbook: guests per tenant (TN-1)

Task TN-1 (audit defects A9-01, A2-02, A5-07, A2-35, A1-28 guest part, A9-11 guest delete). Until this
change a `Guest` row was global: a host booking reused the guest found by e-mail in the whole database,
and an org could read, edit, delete or anonymize a guest as soon as one of its bookings pointed at it.
Now every guest belongs to exactly one org (`Guests.OrgId`, FK `Restrict` to `Orgs`), the EF global
tenant filter applies to `Guest` like to `Property` and `Booking`, and every guest endpoint is scoped to
the caller's org.

The migrations run automatically at API startup (`Database.Migrate()` in `Program.cs`) on Railway `test`
first, then `production`. Nothing has to be configured; the steps below are checks for the product owner.

## What the migrations do

| Migration | Effect |
|---|---|
| `AddGuestOrgIdNullable` | Adds `Guests.OrgId uuid NULL` and index `IX_Guests_OrgId`. |
| `BackfillGuestOrgIds` | Data only, idempotent (see below). Logs `RAISE NOTICE 'BackfillGuestOrgIds: …'` with the counts. |
| `MakeGuestOrgIdRequired` | Pre-flight: aborts with `Pre-flight failed: …` if a guest has no org or a booking / Alloggiati report still points at a guest of another org. Then `SET NOT NULL` and FK `FK_Guests_Orgs_OrgId` (`ON DELETE RESTRICT`). |

`BackfillGuestOrgIds`:

1. **Owner.** Each guest gets the org of its earliest use (booking, or Alloggiati report through its
   booking; ties broken by org id). The owner keeps the original row and its id.
2. **Split.** For every other org whose bookings or Alloggiati reports point at that guest, a **full copy**
   of the row is created for that org (new id, same data and `CreatedAt`, `UpdatedAt` = migration time)
   and that org's bookings and reports are re-linked to the copy. Check-in sessions reference bookings,
   so they follow automatically. The copy keeps the same `DocumentScanUrl` (same stored file).
3. **Unassigned.** Guests with no booking and no report (created from the guest list, never visible to
   anyone because access required a booking) go to the inactive quarantine org `casazen-unassigned`
   (created by `BackfillDefaultOrgs`, re-created if missing). No tenant sees them; the GDPR retention job
   still processes them.

Why a full copy: the shared row mixes data written by every org that used it (creation, check-in, edits)
and nothing records which org wrote which field. Dropping fields could delete data an org must keep for
its own stays. From the migration on, the copies diverge and no org can reach the other's row.

## Before the deploy (read-only checks)

Run on the target schema (`casazen_test`, then `casazen_prod`) before promoting, to know what will change:

```sql
-- Guests referenced by bookings of more than one org (they will be split).
SELECT g."Id", count(DISTINCT b."OrgId") AS orgs, count(*) AS bookings
FROM "Guests" g JOIN "Bookings" b ON b."GuestId" = g."Id"
GROUP BY g."Id" HAVING count(DISTINCT b."OrgId") > 1;

-- Guests with no booking and no Alloggiati report (they will go to 'casazen-unassigned').
SELECT count(*) FROM "Guests" g
WHERE NOT EXISTS (SELECT 1 FROM "Bookings" b WHERE b."GuestId" = g."Id")
  AND NOT EXISTS (SELECT 1 FROM "AlloggiatiWebReports" r WHERE r."GuestId" = g."Id");
```

Take a backup first (Supabase point-in-time recovery, or `pg_dump` of `Guests`, `Bookings`,
`AlloggiatiWebReports` and `Orgs`).

## After the deploy (verification)

```sql
-- Must be 0: no booking / report points at a guest of another org.
SELECT count(*) FROM "Bookings" b JOIN "Guests" g ON g."Id" = b."GuestId" WHERE g."OrgId" <> b."OrgId";
SELECT count(*) FROM "AlloggiatiWebReports" r
JOIN "Bookings" b ON b."Id" = r."BookingId" JOIN "Guests" g ON g."Id" = r."GuestId"
WHERE g."OrgId" <> b."OrgId";

-- Copies created by the split: same e-mail and CreatedAt, different orgs.
SELECT lower("Email") AS email, "CreatedAt", count(DISTINCT "OrgId") AS orgs
FROM "Guests" GROUP BY 1, 2 HAVING count(DISTINCT "OrgId") > 1;

-- Quarantined guests.
SELECT g."Id", g."CreatedAt" FROM "Guests" g JOIN "Orgs" o ON o."Id" = g."OrgId"
WHERE o."Slug" = 'casazen-unassigned';
```

If the API does not start and the logs show `Pre-flight failed`, the backfill left inconsistent rows
(e.g. a booking written by an old instance between step 2 and step 3). Re-run the body of
`BackfillGuestOrgIds` (it is idempotent) and restart the API: `MakeGuestOrgIdRequired` is applied then.

Rollback: reverting `MakeGuestOrgIdRequired` makes the column nullable again; `BackfillGuestOrgIds` is a
logical no-op on the way down; reverting `AddGuestOrgIdNullable` drops the column. The copies stay as
ordinary guest rows, each referenced by its own bookings.

## API behaviour after TN-1

| Endpoint | Behaviour |
|---|---|
| `GET /api/guests?search=&page=1&pageSize=20` | Guests of the caller's org only, newest first, deleted ones excluded. Response `{ items, totalCount, page, pageSize }` with summary rows (`pageSize` max 100). |
| `GET /api/guests/{id}`, `GET /api/guests/email/{email}` | `GuestDto` (no bookings graph, no org id, no document storage path, no consent IP). Another org's guest: 404 `guest_not_found`. |
| `POST /api/guests` | Created in the caller's org. Same e-mail in **another** org: created normally (nothing revealed). Same e-mail in the **same** org: 409 `guest_email_exists`. |
| `PUT /api/guests/{id}` | Caller's org only, otherwise 404 `guest_not_found`. |
| `DELETE /api/guests/{id}` | No booking or report: row removed (204). Past bookings only: row kept for the FK, marked deleted and anonymized like the GDPR erasure (204). A pending, confirmed or checked-in booking ending today (Europe/Rome) or later: 409 `guest_has_open_bookings`. Never a 500. |
| `/api/gdpr/guests/{id}/…` | Caller's org only, otherwise 404. |

Host bookings (`POST /api/bookings`) and direct bookings keep creating one guest snapshot per booking
(PR #431), now owned by the property's org; there is no lookup by e-mail across orgs. For this reason
there is **no** unique index on `(OrgId, lower(Email))`: one org legitimately holds several rows with the
same e-mail (one per booking).
