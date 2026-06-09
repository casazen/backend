using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDefaultOrgs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data migration (Deploy 2). Idempotent and re-runnable: every step is guarded by
            // "OrgId IS NULL" / "ON CONFLICT DO NOTHING", and row counts are logged via RAISE NOTICE
            // (AC4). The default-Org slug is a deterministic function of OwnerId
            // (org-<sanitized-owner>-<md5 prefix>) so the same owner always maps to the same Org and
            // two distinct owners can never collide onto one Org (the cross-owner leak risk in AC10).
            migrationBuilder.Sql(@"
DO $$
DECLARE
    v_orgs     bigint;
    v_users    bigint;
    v_props    bigint;
    v_bookings bigint;
    v_payments bigint;
    v_leases   bigint;
BEGIN
    -- 1) One default Org per distinct Property.OwnerId (PlanTier.Starter = 0, active).
    INSERT INTO ""Orgs"" (
        ""Id"", ""Name"", ""Slug"", ""PlanTier"", ""DisplayName"", ""ContactEmail"",
        ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
    SELECT
        gen_random_uuid(),
        COALESCE(NULLIF(btrim(u.""FirstName"" || ' ' || u.""LastName""), ''), p.""OwnerId""),
        left('org-' || regexp_replace(lower(p.""OwnerId""), '[^a-z0-9]+', '-', 'g'), 90)
            || '-' || substr(md5(p.""OwnerId""), 1, 8),
        0,
        COALESCE(NULLIF(btrim(u.""FirstName"" || ' ' || u.""LastName""), ''), p.""OwnerId""),
        COALESCE(u.""Email"", ''),
        true,
        now(), now()
    FROM (SELECT DISTINCT ""OwnerId"" FROM ""Properties"") p
    LEFT JOIN ""Users"" u ON u.""Id"" = p.""OwnerId""
    ON CONFLICT (""Slug"") DO NOTHING;
    GET DIAGNOSTICS v_orgs = ROW_COUNT;

    -- 2) Link each owner's User row to their default Org (owners only).
    UPDATE ""Users"" u
    SET ""OrgId"" = o.""Id""
    FROM ""Orgs"" o
    WHERE u.""OrgId"" IS NULL
      AND o.""Slug"" = left('org-' || regexp_replace(lower(u.""Id""), '[^a-z0-9]+', '-', 'g'), 90)
            || '-' || substr(md5(u.""Id""), 1, 8)
      AND EXISTS (SELECT 1 FROM ""Properties"" p WHERE p.""OwnerId"" = u.""Id"");
    GET DIAGNOSTICS v_users = ROW_COUNT;

    -- 3a) Properties: OwnerId -> Org.
    UPDATE ""Properties"" p
    SET ""OrgId"" = o.""Id""
    FROM ""Orgs"" o
    WHERE p.""OrgId"" IS NULL
      AND o.""Slug"" = left('org-' || regexp_replace(lower(p.""OwnerId""), '[^a-z0-9]+', '-', 'g'), 90)
            || '-' || substr(md5(p.""OwnerId""), 1, 8);
    GET DIAGNOSTICS v_props = ROW_COUNT;

    -- 3b) Bookings: booking -> property.
    UPDATE ""Bookings"" b
    SET ""OrgId"" = p.""OrgId""
    FROM ""Properties"" p
    WHERE b.""OrgId"" IS NULL
      AND p.""Id"" = b.""PropertyId""
      AND p.""OrgId"" IS NOT NULL;
    GET DIAGNOSTICS v_bookings = ROW_COUNT;

    -- 3c) Payments: payment -> booking -> property.
    UPDATE ""Payments"" pay
    SET ""OrgId"" = b.""OrgId""
    FROM ""Bookings"" b
    WHERE pay.""OrgId"" IS NULL
      AND b.""Id"" = pay.""BookingId""
      AND b.""OrgId"" IS NOT NULL;
    GET DIAGNOSTICS v_payments = ROW_COUNT;

    -- 3d) LeaseContracts: lease -> property.
    UPDATE ""LeaseContracts"" l
    SET ""OrgId"" = p.""OrgId""
    FROM ""Properties"" p
    WHERE l.""OrgId"" IS NULL
      AND p.""Id"" = l.""PropertyId""
      AND p.""OrgId"" IS NOT NULL;
    GET DIAGNOSTICS v_leases = ROW_COUNT;

    -- 4) Dedicated fallback Org for the Step 3 quarantine rule (inactive, never a real tenant).
    INSERT INTO ""Orgs"" (
        ""Id"", ""Name"", ""Slug"", ""PlanTier"", ""DisplayName"", ""ContactEmail"",
        ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
    VALUES (
        gen_random_uuid(), 'CasaZen Unassigned', 'casazen-unassigned', 0,
        'CasaZen Unassigned', '', false, now(), now())
    ON CONFLICT (""Slug"") DO NOTHING;

    -- 5) Verification log (AC4).
    RAISE NOTICE 'BackfillDefaultOrgs: orgs_created=%, users_linked=%, properties=%, bookings=%, payments=%, leases=%',
        v_orgs, v_users, v_props, v_bookings, v_payments, v_leases;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Logical no-op (AC10b). A data backfill is not auto-reverted on rollback: nulling OrgId
            // blindly could orphan rows the app already depends on. The documented reversal lives in
            // the release runbook and is run manually only when a full rollback is required:
            //
            //   UPDATE "Bookings"       SET "OrgId" = NULL WHERE "OrgId" IN (SELECT "Id" FROM "Orgs" WHERE "Slug" LIKE 'org-%' OR "Slug" = 'casazen-unassigned');
            //   UPDATE "Payments"       SET "OrgId" = NULL WHERE "OrgId" IN (SELECT "Id" FROM "Orgs" WHERE "Slug" LIKE 'org-%' OR "Slug" = 'casazen-unassigned');
            //   UPDATE "LeaseContracts" SET "OrgId" = NULL WHERE "OrgId" IN (SELECT "Id" FROM "Orgs" WHERE "Slug" LIKE 'org-%' OR "Slug" = 'casazen-unassigned');
            //   UPDATE "Properties"     SET "OrgId" = NULL WHERE "OrgId" IN (SELECT "Id" FROM "Orgs" WHERE "Slug" LIKE 'org-%' OR "Slug" = 'casazen-unassigned');
            //   UPDATE "Users"          SET "OrgId" = NULL WHERE "OrgId" IN (SELECT "Id" FROM "Orgs" WHERE "Slug" LIKE 'org-%' OR "Slug" = 'casazen-unassigned');
            //   DELETE FROM "Orgs" WHERE "Slug" LIKE 'org-%' OR "Slug" = 'casazen-unassigned';
            //
            // Reverting Step 1 (AddOrgIdNullable) drops the columns entirely, which is the real
            // structural rollback path; this method intentionally does nothing.
        }
    }
}
