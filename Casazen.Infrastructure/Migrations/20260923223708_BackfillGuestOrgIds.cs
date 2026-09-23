using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillGuestOrgIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TN-1 data migration (step 2 of 3, between AddGuestOrgIdNullable and MakeGuestOrgIdRequired).
            // Until now a Guest row was global: a host booking reused the guest found by e-mail in the
            // whole database, so one row could be referenced by bookings of several orgs (A9-01, A2-02,
            // A5-07). This step gives every guest exactly one org:
            //
            //   1) Owner. A guest still without OrgId gets the org of its earliest use (booking, or
            //      Alloggiati report through its booking; ties broken by org id, so the result is
            //      deterministic). The owner keeps the original row and its Id.
            //   2) Split. For every other org whose bookings or Alloggiati reports still point at a
            //      guest owned by another org, a full copy of the guest row is created for that org
            //      (new Id, same data, CreatedAt kept, UpdatedAt = now) and that org's bookings and
            //      reports are re-linked to the copy. The copy is a faithful duplicate because the
            //      shared row mixes data written by every org that used it (creation, check-in, edits)
            //      and there is no way to tell which org wrote which field: dropping fields could
            //      delete data an org must keep for its own stays. From now on the copies diverge.
            //      Copies reference the same DocumentScanUrl file, if any.
            //      GuestCheckInSessions reference bookings, not guests: they follow their booking.
            //   3) Unassigned. Guests with no booking and no report (created from the guest list, which
            //      never showed them to anyone because access required a booking) cannot be attributed
            //      to an org: they go to the inactive quarantine org 'casazen-unassigned' (created by
            //      BackfillDefaultOrgs, re-created here if missing), so no tenant sees them. The GDPR
            //      retention job still processes them.
            //
            // Idempotent: step 1 only touches rows with NULL OrgId, step 2 only rows whose booking or
            // report org differs from the guest org, step 3 only rows still NULL. The column list of the
            // copy is read from the catalog so that every Guests column is duplicated. Counts are logged
            // with RAISE NOTICE. Runbook: docs/runbooks/guest-tenant-migration.md.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    v_cols        text;
    v_src_cols    text;
    v_owners      bigint;
    v_copies      bigint;
    v_bookings    bigint;
    v_reports     bigint;
    v_unassigned  bigint := 0;
    v_quarantine  uuid;
BEGIN
    -- 1) Owner org = org of the earliest booking/report that uses the guest.
    WITH usage AS (
        SELECT b.""GuestId"", b.""OrgId"", b.""CreatedAt"" AS ""UsedAt""
        FROM ""Bookings"" b
        UNION ALL
        SELECT r.""GuestId"", b.""OrgId"", r.""CreatedAt""
        FROM ""AlloggiatiWebReports"" r
        JOIN ""Bookings"" b ON b.""Id"" = r.""BookingId""
    ), owner AS (
        SELECT DISTINCT ON (""GuestId"") ""GuestId"", ""OrgId""
        FROM usage
        ORDER BY ""GuestId"", ""UsedAt"", ""OrgId""
    )
    UPDATE ""Guests"" g
    SET ""OrgId"" = owner.""OrgId""
    FROM owner
    WHERE g.""Id"" = owner.""GuestId""
      AND g.""OrgId"" IS NULL;
    GET DIAGNOSTICS v_owners = ROW_COUNT;

    -- 2) One copy per (guest, other org) still referenced across the tenant boundary.
    DROP TABLE IF EXISTS pg_temp.tn1_guest_split;
    CREATE TEMP TABLE tn1_guest_split ON COMMIT DROP AS
    SELECT p.""SourceId"", p.""OrgId"", gen_random_uuid() AS ""CopyId""
    FROM (
        SELECT b.""GuestId"" AS ""SourceId"", b.""OrgId""
        FROM ""Bookings"" b
        JOIN ""Guests"" g ON g.""Id"" = b.""GuestId""
        WHERE g.""OrgId"" <> b.""OrgId""
        UNION
        SELECT r.""GuestId"", b.""OrgId""
        FROM ""AlloggiatiWebReports"" r
        JOIN ""Bookings"" b ON b.""Id"" = r.""BookingId""
        JOIN ""Guests"" g ON g.""Id"" = r.""GuestId""
        WHERE g.""OrgId"" <> b.""OrgId""
    ) p;

    SELECT string_agg(quote_ident(a.attname), ', ' ORDER BY a.attnum),
           string_agg('g.' || quote_ident(a.attname), ', ' ORDER BY a.attnum)
    INTO v_cols, v_src_cols
    FROM pg_attribute a
    WHERE a.attrelid = '""Guests""'::regclass
      AND a.attnum > 0
      AND NOT a.attisdropped
      AND a.attgenerated = ''
      AND a.attname NOT IN ('Id', 'OrgId', 'UpdatedAt');

    -- Identifiers come from the catalog (quote_ident), never from input.
    EXECUTE format(
        'INSERT INTO ""Guests"" (""Id"", ""OrgId"", ""UpdatedAt"", %s) '
        'SELECT s.""CopyId"", s.""OrgId"", now(), %s '
        'FROM tn1_guest_split s JOIN ""Guests"" g ON g.""Id"" = s.""SourceId""',
        v_cols, v_src_cols);
    GET DIAGNOSTICS v_copies = ROW_COUNT;

    UPDATE ""Bookings"" b
    SET ""GuestId"" = s.""CopyId""
    FROM tn1_guest_split s
    WHERE b.""GuestId"" = s.""SourceId""
      AND b.""OrgId"" = s.""OrgId"";
    GET DIAGNOSTICS v_bookings = ROW_COUNT;

    UPDATE ""AlloggiatiWebReports"" r
    SET ""GuestId"" = s.""CopyId""
    FROM tn1_guest_split s, ""Bookings"" b
    WHERE r.""GuestId"" = s.""SourceId""
      AND b.""Id"" = r.""BookingId""
      AND b.""OrgId"" = s.""OrgId"";
    GET DIAGNOSTICS v_reports = ROW_COUNT;

    -- 3) Guests nobody uses: quarantine org, visible to no tenant.
    IF EXISTS (SELECT 1 FROM ""Guests"" WHERE ""OrgId"" IS NULL) THEN
        INSERT INTO ""Orgs"" (
            ""Id"", ""Name"", ""Slug"", ""PlanTier"", ""DisplayName"", ""ContactEmail"",
            ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
        VALUES (
            gen_random_uuid(), 'CasaZen Unassigned', 'casazen-unassigned', 0,
            'CasaZen Unassigned', '', false, now(), now())
        ON CONFLICT (""Slug"") DO NOTHING;

        SELECT ""Id"" INTO v_quarantine FROM ""Orgs"" WHERE ""Slug"" = 'casazen-unassigned';

        UPDATE ""Guests"" SET ""OrgId"" = v_quarantine WHERE ""OrgId"" IS NULL;
        GET DIAGNOSTICS v_unassigned = ROW_COUNT;
    END IF;

    RAISE NOTICE 'BackfillGuestOrgIds: owners_assigned=%, guest_copies=%, bookings_relinked=%, reports_relinked=%, unassigned_guests=%',
        v_owners, v_copies, v_bookings, v_reports, v_unassigned;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Logical no-op, like BackfillDefaultOrgs: the split cannot be undone safely (the copies may
            // have diverged since). Rolling back AddGuestOrgIdNullable drops the column; the copies stay
            // as ordinary guest rows, each still referenced by its own bookings.
        }
    }
}
