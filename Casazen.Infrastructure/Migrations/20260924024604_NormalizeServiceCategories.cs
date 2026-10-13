using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// SU-03 (A4-05, A6-03): stores every existing service category as a code of
    /// <c>Casazen.Core.Suppliers.ServiceCategories</c>. The supplier wizard saved Italian labels
    /// (<c>Pulizie</c>, <c>Manutenzione</c>, ...) while hosts searched by code (<c>cleaning</c>, ...), so hosts never
    /// found real suppliers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data only, no schema change. Converted columns: <c>SupplierProfiles.CategoriesJson</c> and
    /// <c>SupplierInviteRecords.CategoriesJson</c> (JSON arrays: each string item is mapped, duplicates removed, first
    /// occurrence order kept) and <c>ServiceRequests.Category</c>. A value is compared trimmed and lower case with the
    /// map below: the codes themselves (<c>Cleaning</c> → <c>cleaning</c>), the six labels of the old supplier wizard
    /// (Pulizie, Manutenzione, Giardinaggio, Eventi, Noleggio, Escursioni) and the Italian labels the web app showed for
    /// the host codes (Idraulico, Lavanderia). <c>UpdatedAt</c> is not touched.
    /// </para>
    /// <para>
    /// A value that cannot be mapped is NOT deleted: it stays as it is, is logged with <c>RAISE WARNING</c> (table, row
    /// id, value; WARNING reaches the PostgreSQL server log) and is listed by <c>GET /api/admin/suppliers/unmapped-categories</c>.
    /// Idempotent. Down is a no-op: the labels are not needed and the codes are valid for the old code too.
    /// Runbook: <c>docs/runbooks/service-categories.md</c>.
    /// </para>
    /// </remarks>
    public partial class NormalizeServiceCategories : Migration
    {
        /// <summary>The whole data migration (one <c>DO</c> block). Public so that a PostgreSQL test can run it again.</summary>
        public const string NormalizeSql = """
            DO $$
            DECLARE
                -- normalized (trimmed, lower case) stored value -> category code
                v_map jsonb := jsonb_build_object(
                    'cleaning', 'cleaning',
                    'maintenance', 'maintenance',
                    'plumbing', 'plumbing',
                    'laundry', 'laundry',
                    'linen', 'linen',
                    'check-in', 'check-in',
                    'gardening', 'gardening',
                    'events', 'events',
                    'rental', 'rental',
                    'excursions', 'excursions',
                    'pulizie', 'cleaning',
                    'manutenzione', 'maintenance',
                    'giardinaggio', 'gardening',
                    'eventi', 'events',
                    'noleggio', 'rental',
                    'escursioni', 'excursions',
                    'idraulico', 'plumbing',
                    'lavanderia', 'laundry');
                v_codes text[] := ARRAY['cleaning', 'maintenance', 'plumbing', 'laundry', 'linen', 'check-in',
                                        'gardening', 'events', 'rental', 'excursions'];
                v_profiles bigint;
                v_invites  bigint;
                v_requests bigint;
                v_unmapped bigint := 0;
                r record;
            BEGIN
                UPDATE "SupplierProfiles" AS sp
                SET "CategoriesJson" = n.normalized
                FROM (
                    SELECT p."OrgId" AS id,
                           COALESCE((
                               SELECT jsonb_agg(d.item ORDER BY d.first_ord)
                               FROM (
                                   SELECT m.item, min(m.ord) AS first_ord
                                   FROM (
                                       SELECT CASE
                                                  WHEN jsonb_typeof(t.e) = 'string'
                                                       AND v_map ->> lower(btrim(t.e #>> ARRAY[]::text[], E' \t\r\n')) IS NOT NULL
                                                  THEN to_jsonb(v_map ->> lower(btrim(t.e #>> ARRAY[]::text[], E' \t\r\n')))
                                                  ELSE t.e
                                              END AS item,
                                              t.ord
                                       FROM jsonb_array_elements(p."CategoriesJson") WITH ORDINALITY AS t(e, ord)
                                   ) AS m
                                   GROUP BY m.item
                               ) AS d
                           ), '[]'::jsonb) AS normalized
                    FROM "SupplierProfiles" AS p
                    WHERE jsonb_typeof(p."CategoriesJson") = 'array'
                ) AS n
                WHERE sp."OrgId" = n.id AND sp."CategoriesJson" IS DISTINCT FROM n.normalized;
                GET DIAGNOSTICS v_profiles = ROW_COUNT;

                UPDATE "SupplierInviteRecords" AS si
                SET "CategoriesJson" = n.normalized
                FROM (
                    SELECT i."Id" AS id,
                           COALESCE((
                               SELECT jsonb_agg(d.item ORDER BY d.first_ord)
                               FROM (
                                   SELECT m.item, min(m.ord) AS first_ord
                                   FROM (
                                       SELECT CASE
                                                  WHEN jsonb_typeof(t.e) = 'string'
                                                       AND v_map ->> lower(btrim(t.e #>> ARRAY[]::text[], E' \t\r\n')) IS NOT NULL
                                                  THEN to_jsonb(v_map ->> lower(btrim(t.e #>> ARRAY[]::text[], E' \t\r\n')))
                                                  ELSE t.e
                                              END AS item,
                                              t.ord
                                       FROM jsonb_array_elements(i."CategoriesJson") WITH ORDINALITY AS t(e, ord)
                                   ) AS m
                                   GROUP BY m.item
                               ) AS d
                           ), '[]'::jsonb) AS normalized
                    FROM "SupplierInviteRecords" AS i
                    WHERE jsonb_typeof(i."CategoriesJson") = 'array'
                ) AS n
                WHERE si."Id" = n.id AND si."CategoriesJson" IS DISTINCT FROM n.normalized;
                GET DIAGNOSTICS v_invites = ROW_COUNT;

                UPDATE "ServiceRequests"
                SET "Category" = v_map ->> lower(btrim("Category", E' \t\r\n'))
                WHERE v_map ->> lower(btrim("Category", E' \t\r\n')) IS NOT NULL
                  AND "Category" IS DISTINCT FROM v_map ->> lower(btrim("Category", E' \t\r\n'));
                GET DIAGNOSTICS v_requests = ROW_COUNT;

                -- Values that are still not codes are kept: log each one (never deleted).
                FOR r IN
                    SELECT 'SupplierProfiles' AS tbl, p."OrgId"::text AS id,
                           CASE WHEN jsonb_typeof(t.e) = 'string' THEN t.e #>> ARRAY[]::text[] ELSE t.e::text END AS val
                    FROM "SupplierProfiles" AS p
                    CROSS JOIN LATERAL jsonb_array_elements(
                        CASE WHEN jsonb_typeof(p."CategoriesJson") = 'array' THEN p."CategoriesJson" ELSE '[]'::jsonb END) AS t(e)
                    WHERE jsonb_typeof(t.e) <> 'string' OR NOT (t.e #>> ARRAY[]::text[]) = ANY (v_codes)
                    UNION ALL
                    SELECT 'SupplierInviteRecords', i."Id"::text,
                           CASE WHEN jsonb_typeof(t.e) = 'string' THEN t.e #>> ARRAY[]::text[] ELSE t.e::text END
                    FROM "SupplierInviteRecords" AS i
                    CROSS JOIN LATERAL jsonb_array_elements(
                        CASE WHEN jsonb_typeof(i."CategoriesJson") = 'array' THEN i."CategoriesJson" ELSE '[]'::jsonb END) AS t(e)
                    WHERE jsonb_typeof(t.e) <> 'string' OR NOT (t.e #>> ARRAY[]::text[]) = ANY (v_codes)
                    UNION ALL
                    SELECT 'ServiceRequests', s."Id"::text, s."Category"
                    FROM "ServiceRequests" AS s
                    WHERE NOT s."Category" = ANY (v_codes)
                LOOP
                    v_unmapped := v_unmapped + 1;
                    RAISE WARNING 'NormalizeServiceCategories: unmapped category kept: table=%, id=%, value=%', r.tbl, r.id, r.val;
                END LOOP;

                IF v_unmapped > 0 THEN
                    RAISE WARNING 'NormalizeServiceCategories: supplier_profiles=%, supplier_invites=%, service_requests=% converted; % unmapped values kept, listed by GET /api/admin/suppliers/unmapped-categories',
                        v_profiles, v_invites, v_requests, v_unmapped;
                ELSE
                    RAISE NOTICE 'NormalizeServiceCategories: supplier_profiles=%, supplier_invites=%, service_requests=% converted; no unmapped values',
                        v_profiles, v_invites, v_requests;
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NormalizeSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the Italian labels are not restored; codes are valid values for the previous code too.
        }
    }
}
