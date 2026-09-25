using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// SU-14 (A4-22): one supplier profile per email. Unique index <c>UIX_SupplierProfiles_NormalizedEmail</c> on
    /// <c>lower(btrim("Email"))</c> of <c>SupplierProfiles</c>, blank emails excluded (raw SQL: EF Core cannot model an
    /// expression index, so the index is not in the EF model; see <c>SupplierProfileEmailIndex</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Migrations run at startup (<c>Program.cs</c>), so failing on existing duplicates would stop the deployment that
    /// ships the safe <c>fix-orphaned</c>. The duplicates are therefore <b>merged first, with the rules of
    /// <c>SupplierService.FixOrphanedSupplierOrgsAsync</c></b> (keep the two in step): keeper = the active profile, then
    /// the one an account holds, then the oldest; service requests, legacy supplier jobs, availability (the keeper's day
    /// wins), missing categories and comuni, account links and devices move to the keeper; the duplicate profile is
    /// deleted, and its org too unless it also holds host data (then only the supplier profile goes). Counts and the
    /// merged org ids are logged with <c>RAISE NOTICE</c> (no email).
    /// </para>
    /// <para>
    /// A group the rules do not decide alone (a suspended profile, or profiles held by different accounts) is not
    /// merged: the migration then <b>fails with a clear error</b> listing those org ids, nothing is changed (one
    /// transaction) and the previous deployment keeps running. Runbook: <c>docs/runbooks/suppliers.md</c> section 9.
    /// Down drops the index only: merged profiles are not split again.
    /// </para>
    /// </remarks>
    public partial class SupplierProfileEmailUnique : Migration
    {
        /// <summary>Name of the unique index (same as <c>SupplierProfileEmailIndex.Name</c>).</summary>
        public const string IndexName = "UIX_SupplierProfiles_NormalizedEmail";

        /// <summary>The merge of the existing duplicates (one <c>DO</c> block). Public so that a PostgreSQL test can run it.</summary>
        public const string MergeDuplicatesSql = """
            DO $$
            DECLARE
                g                      record;
                v_keeper               uuid;
                v_duplicate            uuid;
                v_accounts             bigint;
                v_held                 bigint;
                v_count                bigint;
                v_keeper_categories    jsonb;
                v_keeper_comuni        jsonb;
                v_duplicate_categories jsonb;
                v_duplicate_comuni     jsonb;
                v_added_categories     jsonb;
                v_added_comuni         jsonb;
                v_groups               bigint := 0;
                v_merged               bigint := 0;
                v_requests             bigint := 0;
                v_kept_orgs            bigint := 0;
                v_blocked              text[] := ARRAY[]::text[];
            BEGIN
                -- No profile is written by the running deployment between the merge and the index creation.
                LOCK TABLE "SupplierProfiles" IN SHARE ROW EXCLUSIVE MODE;

                FOR g IN
                    SELECT array_agg(sp."OrgId" ORDER BY sp."CreatedAt", sp."OrgId") AS org_ids
                    FROM "SupplierProfiles" AS sp
                    WHERE btrim(sp."Email") <> ''
                    GROUP BY lower(btrim(sp."Email"))
                    HAVING count(*) > 1
                    ORDER BY min(sp."CreatedAt")
                LOOP
                    v_groups := v_groups + 1;

                    -- A suspended profile (SupplierStatus.Suspended = 2) is never merged: it could lift the suspension.
                    IF EXISTS (SELECT 1 FROM "SupplierProfiles" WHERE "OrgId" = ANY (g.org_ids) AND "Status" = 2) THEN
                        v_blocked := v_blocked
                            || format('[%s] supplier_duplicate_suspended', array_to_string(g.org_ids, ', '));
                        CONTINUE;
                    END IF;

                    -- Profiles held by different accounts: which account keeps the supplier is a decision.
                    SELECT count(DISTINCT u."Id"), count(DISTINCT p.org_id)
                      INTO v_accounts, v_held
                      FROM unnest(g.org_ids) AS p(org_id)
                      JOIN "Users" AS u ON u."SupplierOrgId" = p.org_id OR u."OrgId" = p.org_id;
                    IF v_held > 1 AND v_accounts > 1 THEN
                        v_blocked := v_blocked
                            || format('[%s] supplier_duplicate_several_accounts', array_to_string(g.org_ids, ', '));
                        CONTINUE;
                    END IF;

                    -- Keeper: the active profile (SupplierStatus.Active = 1), then the one an account holds, then the oldest.
                    SELECT sp."OrgId" INTO v_keeper
                      FROM "SupplierProfiles" AS sp
                     WHERE sp."OrgId" = ANY (g.org_ids)
                     ORDER BY (sp."Status" = 1) DESC,
                              EXISTS (SELECT 1 FROM "Users" AS u
                                       WHERE u."SupplierOrgId" = sp."OrgId" OR u."OrgId" = sp."OrgId") DESC,
                              sp."CreatedAt",
                              sp."OrgId"
                     LIMIT 1;

                    FOREACH v_duplicate IN ARRAY g.org_ids LOOP
                        CONTINUE WHEN v_duplicate = v_keeper;

                        UPDATE "ServiceRequests" SET "SupplierOrgId" = v_keeper WHERE "SupplierOrgId" = v_duplicate;
                        GET DIAGNOSTICS v_count = ROW_COUNT;
                        v_requests := v_requests + v_count;

                        UPDATE "SupplierJobs" SET "SupplierOrgId" = v_keeper WHERE "SupplierOrgId" = v_duplicate;

                        -- A day the keeper already has keeps the keeper's value.
                        UPDATE "SupplierAvailability" AS a
                           SET "OrgId" = v_keeper
                         WHERE a."OrgId" = v_duplicate
                           AND NOT EXISTS (SELECT 1 FROM "SupplierAvailability" AS k
                                            WHERE k."OrgId" = v_keeper AND k."Date" = a."Date");

                        -- Categories and comuni: the duplicate's string items the keeper lacks are appended, in order.
                        SELECT kp."CategoriesJson", kp."ComuniJson", dp."CategoriesJson", dp."ComuniJson"
                          INTO v_keeper_categories, v_keeper_comuni, v_duplicate_categories, v_duplicate_comuni
                          FROM "SupplierProfiles" AS kp, "SupplierProfiles" AS dp
                         WHERE kp."OrgId" = v_keeper AND dp."OrgId" = v_duplicate;

                        IF jsonb_typeof(v_keeper_categories) IS DISTINCT FROM 'array' THEN
                            v_keeper_categories := '[]'::jsonb;
                        END IF;
                        IF jsonb_typeof(v_keeper_comuni) IS DISTINCT FROM 'array' THEN
                            v_keeper_comuni := '[]'::jsonb;
                        END IF;
                        IF jsonb_typeof(v_duplicate_categories) IS DISTINCT FROM 'array' THEN
                            v_duplicate_categories := '[]'::jsonb;
                        END IF;
                        IF jsonb_typeof(v_duplicate_comuni) IS DISTINCT FROM 'array' THEN
                            v_duplicate_comuni := '[]'::jsonb;
                        END IF;

                        SELECT coalesce(jsonb_agg(x.value ORDER BY x.ord), '[]'::jsonb) INTO v_added_categories
                          FROM (SELECT DISTINCT ON (e.value) e.value, e.ord
                                  FROM jsonb_array_elements(v_duplicate_categories) WITH ORDINALITY AS e(value, ord)
                                 WHERE jsonb_typeof(e.value) = 'string'
                                   AND NOT v_keeper_categories @> jsonb_build_array(e.value)
                                 ORDER BY e.value, e.ord) AS x;
                        SELECT coalesce(jsonb_agg(x.value ORDER BY x.ord), '[]'::jsonb) INTO v_added_comuni
                          FROM (SELECT DISTINCT ON (e.value) e.value, e.ord
                                  FROM jsonb_array_elements(v_duplicate_comuni) WITH ORDINALITY AS e(value, ord)
                                 WHERE jsonb_typeof(e.value) = 'string'
                                   AND NOT v_keeper_comuni @> jsonb_build_array(e.value)
                                 ORDER BY e.value, e.ord) AS x;

                        IF jsonb_array_length(v_added_categories) > 0 OR jsonb_array_length(v_added_comuni) > 0 THEN
                            UPDATE "SupplierProfiles"
                               SET "CategoriesJson" = CASE WHEN jsonb_array_length(v_added_categories) = 0
                                                           THEN "CategoriesJson"
                                                           ELSE v_keeper_categories || v_added_categories END,
                                   "ComuniJson" = CASE WHEN jsonb_array_length(v_added_comuni) = 0
                                                       THEN "ComuniJson"
                                                       ELSE v_keeper_comuni || v_added_comuni END,
                                   "UpdatedAt" = now()
                             WHERE "OrgId" = v_keeper;
                        END IF;

                        -- The duplicate's accounts reach the keeper; a supplier-only account (OrgId = duplicate) gets the
                        -- explicit link too, in case the duplicate org is kept below.
                        UPDATE "Users"
                           SET "SupplierOrgId" = v_keeper, "UpdatedAt" = now()
                         WHERE "SupplierOrgId" = v_duplicate
                            OR ("OrgId" = v_duplicate AND "SupplierOrgId" IS NULL);

                        -- The availability days the keeper already had go with the profile (ON DELETE CASCADE).
                        DELETE FROM "SupplierProfiles" WHERE "OrgId" = v_duplicate;

                        -- The org goes too when it is a plain supplier org (OrgType.Supplier = 1) with no host data.
                        IF EXISTS (SELECT 1 FROM "Orgs" WHERE "Id" = v_duplicate AND "OrgType" = 1)
                           AND NOT EXISTS (SELECT 1 FROM "ConsentRecords" WHERE "OrgId" = v_duplicate)
                           AND NOT EXISTS (SELECT 1 FROM "SignupAttributions" WHERE "OrgId" = v_duplicate) THEN
                            BEGIN
                                UPDATE "Users" SET "OrgId" = v_keeper, "UpdatedAt" = now() WHERE "OrgId" = v_duplicate;
                                UPDATE "DeviceRegistrations" SET "OrgId" = v_keeper WHERE "OrgId" = v_duplicate;
                                DELETE FROM "Orgs" WHERE "Id" = v_duplicate;
                            EXCEPTION WHEN foreign_key_violation THEN
                                -- Host rows (properties, bookings, ...) still reference it: kept without its profile.
                                v_kept_orgs := v_kept_orgs + 1;
                            END;
                        ELSE
                            v_kept_orgs := v_kept_orgs + 1;
                        END IF;

                        v_merged := v_merged + 1;
                        RAISE NOTICE 'SupplierProfileEmailUnique: supplier profile % merged into %', v_duplicate, v_keeper;
                    END LOOP;
                END LOOP;

                RAISE NOTICE 'SupplierProfileEmailUnique: % duplicate email group(s), % profile(s) merged, % service request(s) moved, % duplicate org(s) kept for their host data',
                    v_groups, v_merged, v_requests, v_kept_orgs;

                IF cardinality(v_blocked) > 0 THEN
                    RAISE EXCEPTION 'SupplierProfileEmailUnique: % supplier email group(s) need a manual decision and were not merged: %',
                        cardinality(v_blocked), array_to_string(v_blocked, '; ')
                        USING HINT = 'Nothing was changed and the unique email index was not created. See docs/runbooks/suppliers.md section 9.';
                END IF;
            END $$;
            """;

        /// <summary>The unique index, created once no duplicate is left.</summary>
        public const string CreateIndexSql = """
            CREATE UNIQUE INDEX "UIX_SupplierProfiles_NormalizedEmail"
                ON "SupplierProfiles" (lower(btrim("Email")))
                WHERE btrim("Email") <> '';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MergeDuplicatesSql);
            migrationBuilder.Sql(CreateIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Merged profiles are not split again: only the index goes.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "UIX_SupplierProfiles_NormalizedEmail";""");
        }
    }
}
