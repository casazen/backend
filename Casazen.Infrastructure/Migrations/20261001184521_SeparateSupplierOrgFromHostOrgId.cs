using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PL-05 (A1-40) data migration: <c>User.OrgId</c> becomes the user's <b>host</b> org only. Before PL-05 the supplier
    /// registration (<c>SupplierService</c>) also wrote the new supplier org into <c>User.OrgId</c> when it was empty, and
    /// the host onboarding then reused it: the properties, bookings, plan and public site of a supplier who became a
    /// host landed on an <c>OrgType.Supplier</c> org. The code no longer writes a supplier org into <c>OrgId</c> and the
    /// host resolvers ignore one; this migration repairs the existing rows.
    /// </summary>
    /// <remarks>
    /// <para>For every supplier org (<c>OrgType = 1</c>), in one transaction, under a write lock on <c>Users</c>,
    /// <c>Orgs</c> and <c>SupplierProfiles</c> (an old instance still running cannot link accounts meanwhile):</para>
    /// <list type="number">
    /// <item>The supplier link is never lost: an account whose <c>OrgId</c> is a supplier org with a profile and that has
    /// no <c>SupplierOrgId</c> gets it (same rule as <c>fix-orphaned</c>'s <c>supplierLinksBackfilled</c>).</item>
    /// <item>A supplier org <b>without host data</b> (the common case): its accounts lose the <c>OrgId</c> link and keep
    /// <c>SupplierOrgId</c>. An account whose <c>SupplierOrgId</c> is another org keeps it (clearing it would leave this
    /// profile held by nobody); no host resolver reads a supplier <c>OrgId</c>, so it is harmless and only counted.</item>
    /// <item>A supplier org <b>with host data</b> (A1-40 already happened): the org is in practice the host's org, so it
    /// becomes <c>OrgType = Host</c> and keeps every host row and its org-level data (slug, plan, Stripe, Connect, public
    /// site, consents); its supplier side moves to a <b>new</b> supplier org: profile (the public showcase slug moves with
    /// it), availability, <c>ServiceRequests.SupplierOrgId</c>, <c>StayCheckouts.CleaningSupplierOrgId</c> and
    /// <c>Users.SupplierOrgId</c>. Nothing is deleted.</item>
    /// </list>
    /// <para>Host data = org-level billing or site fields (Stripe customer or subscription, Connect account, custom domain
    /// or subdomain, a paid tier) or a row of any table whose foreign key references <c>Orgs</c>, except the supplier and
    /// identity links (<c>SupplierProfiles.OrgId</c>, <c>ServiceRequests.SupplierOrgId</c>, <c>Users.OrgId</c>,
    /// <c>DeviceRegistrations.OrgId</c>). The foreign keys are read from the catalog, so a host table added later counts
    /// too. Counts and the split org ids are logged with <c>RAISE NOTICE</c> (no email, no name). Idempotent: a second run
    /// finds no supplier org with host data and no account to detach. Runbooks: <c>docs/runbooks/suppliers.md</c> §13 and
    /// <c>docs/runbooks/tenant-child-orgid-migration.md</c>. Down is a logical no-op: restoring the old links would bring
    /// the bug back.</para>
    /// </remarks>
    public partial class SeparateSupplierOrgFromHostOrgId : Migration
    {
        /// <summary>The repair (one <c>DO</c> block). Public so that a PostgreSQL test runs exactly this statement.</summary>
        public const string SeparateSql = """
            DO $$
            DECLARE
                o                  record;
                fk                 record;
                v_profile          record;
                v_has_host         boolean;
                v_found            boolean;
                v_supplier_org     uuid;
                v_count            bigint;
                v_backfilled       bigint := 0;
                v_detached         bigint := 0;
                v_kept_links       bigint := 0;
                v_split            bigint := 0;
                v_reclassified     bigint := 0;
                v_unheld_split     bigint := 0;
                v_split_ids        text[] := ARRAY[]::text[];
            BEGIN
                LOCK TABLE "Users", "Orgs", "SupplierProfiles" IN SHARE ROW EXCLUSIVE MODE;

                -- 1. Keep the supplier link before touching OrgId (pre-SU-08 accounts never got SupplierOrgId).
                UPDATE "Users" AS u
                   SET "SupplierOrgId" = u."OrgId", "UpdatedAt" = now()
                  FROM "Orgs" AS org
                 WHERE org."Id" = u."OrgId"
                   AND org."OrgType" = 1
                   AND u."SupplierOrgId" IS NULL
                   AND EXISTS (SELECT 1 FROM "SupplierProfiles" AS sp WHERE sp."OrgId" = org."Id");
                GET DIAGNOSTICS v_backfilled = ROW_COUNT;

                FOR o IN SELECT org."Id" FROM "Orgs" AS org WHERE org."OrgType" = 1 ORDER BY org."CreatedAt", org."Id" LOOP
                    -- Host data: org-level billing or site fields (PlanTier 0 = Starter, SubscriptionStatus 0 = None)...
                    SELECT org."StripeCustomerId" IS NOT NULL OR org."SubscriptionId" IS NOT NULL
                           OR org."SubscriptionStatus" <> 0 OR org."PlanTier" <> 0
                           OR org."StripeConnectedAccountId" IS NOT NULL
                           OR org."CustomDomain" IS NOT NULL OR org."Subdomain" IS NOT NULL
                      INTO v_has_host
                      FROM "Orgs" AS org
                     WHERE org."Id" = o."Id";

                    -- ...or a row of any table referencing the org, except the supplier and identity links.
                    IF NOT v_has_host THEN
                        FOR fk IN
                            SELECT c.conrelid::regclass AS tbl, a.attname AS col
                              FROM pg_constraint AS c
                              JOIN pg_attribute AS a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
                             WHERE c.contype = 'f'
                               AND c.confrelid = '"Orgs"'::regclass
                               AND cardinality(c.conkey) = 1
                               AND NOT (c.conrelid = '"SupplierProfiles"'::regclass AND a.attname = 'OrgId')
                               AND NOT (c.conrelid = '"ServiceRequests"'::regclass AND a.attname = 'SupplierOrgId')
                               AND NOT (c.conrelid = '"Users"'::regclass AND a.attname IN ('OrgId', 'SupplierOrgId'))
                               AND NOT (c.conrelid = '"DeviceRegistrations"'::regclass AND a.attname = 'OrgId')
                             ORDER BY 1, 2
                        LOOP
                            EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE %I = $1)', fk.tbl, fk.col)
                               INTO v_found
                              USING o."Id";
                            IF v_found THEN
                                v_has_host := true;
                                EXIT;
                            END IF;
                        END LOOP;
                    END IF;

                    IF NOT v_has_host THEN
                        -- 2. Supplier org only: its accounts are suppliers, not hosts of it.
                        UPDATE "Users" AS u
                           SET "OrgId" = NULL, "UpdatedAt" = now()
                         WHERE u."OrgId" = o."Id"
                           AND (u."SupplierOrgId" = o."Id"
                                OR NOT EXISTS (SELECT 1 FROM "SupplierProfiles" AS sp WHERE sp."OrgId" = o."Id"));
                        GET DIAGNOSTICS v_count = ROW_COUNT;
                        v_detached := v_detached + v_count;

                        SELECT count(*) INTO v_count FROM "Users" AS u WHERE u."OrgId" = o."Id";
                        v_kept_links := v_kept_links + v_count;
                        CONTINUE;
                    END IF;

                    -- 3. Host data on a supplier org: the supplier side moves to a new supplier org.
                    SELECT sp."LegalName", sp."Email" INTO v_profile
                      FROM "SupplierProfiles" AS sp
                     WHERE sp."OrgId" = o."Id";
                    IF FOUND THEN
                        v_supplier_org := gen_random_uuid();
                        -- Same shape as SupplierService.RegisterAsync (OrgType 1 = Supplier, PublicHostMode 1 = CasazenPath).
                        INSERT INTO "Orgs" (
                            "Id", "Name", "Slug", "OrgType", "PlanTier", "DisplayName", "ContactEmail",
                            "ContactEmailPublic", "SubscriptionStatus", "ConnectChargesEnabled", "ConnectPayoutsEnabled",
                            "ConnectDetailsSubmitted", "IsActive", "CreatedAt", "UpdatedAt", "HasPartitaIva",
                            "PublicHostMode", "DomainVerificationStatus")
                        VALUES (
                            v_supplier_org, v_profile."LegalName",
                            left('supplier-' || replace(v_supplier_org::text, '-', ''), 30), 1, 0,
                            v_profile."LegalName", v_profile."Email",
                            false, 0, false, false,
                            false, true, now(), now(), false,
                            1, 0);

                        -- Profile and availability in one statement: the availability foreign key is checked at its end.
                        WITH moved_days AS (
                            UPDATE "SupplierAvailability" SET "OrgId" = v_supplier_org WHERE "OrgId" = o."Id"
                        )
                        UPDATE "SupplierProfiles" SET "OrgId" = v_supplier_org WHERE "OrgId" = o."Id";

                        UPDATE "ServiceRequests" SET "SupplierOrgId" = v_supplier_org WHERE "SupplierOrgId" = o."Id";
                        UPDATE "StayCheckouts" SET "CleaningSupplierOrgId" = v_supplier_org
                         WHERE "CleaningSupplierOrgId" = o."Id";
                        UPDATE "Users" SET "SupplierOrgId" = v_supplier_org, "UpdatedAt" = now()
                         WHERE "SupplierOrgId" = o."Id";
                        GET DIAGNOSTICS v_count = ROW_COUNT;
                        IF v_count = 0 THEN
                            v_unheld_split := v_unheld_split + 1;
                        END IF;

                        v_split := v_split + 1;
                        v_split_ids := v_split_ids || format('%s->%s', o."Id", v_supplier_org);
                    END IF;

                    -- OrgType 0 = Host: the org keeps its host rows, accounts (OrgId) and org-level data.
                    UPDATE "Orgs" SET "OrgType" = 0, "UpdatedAt" = now() WHERE "Id" = o."Id";
                    v_reclassified := v_reclassified + 1;
                END LOOP;

                RAISE NOTICE 'SeparateSupplierOrgFromHostOrgId: supplier_links_backfilled=%, host_links_cleared=%, supplier_org_links_kept=%, orgs_reclassified_host=%, supplier_sides_split=%, split_without_holder=%',
                    v_backfilled, v_detached, v_kept_links, v_reclassified, v_split, v_unheld_split;
                IF v_split > 0 THEN
                    RAISE NOTICE 'SeparateSupplierOrgFromHostOrgId: host org -> new supplier org: %',
                        array_to_string(v_split_ids, ', ');
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SeparateSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Logical no-op, like BackfillGuestOrgIds: the old OrgId links were never recorded, and restoring an OrgId
            // that points to a supplier org (or merging a split supplier side back) would bring A1-40 back.
        }
    }
}
