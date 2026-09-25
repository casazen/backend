using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PL-02 (A1-40) data migration: separates the host link from the supplier link on <c>Users</c>. Before this
    /// fix, a supplier-only account (<c>OrgId</c> = its own Supplier org, set by <c>SupplierService</c> when
    /// <c>OrgId</c> was still null) that then completed the host onboarding kept using that same Supplier org as
    /// its host tenant: <c>OrgService.EnsureOrgForUserAsync</c> reused whatever <c>OrgId</c> was already there
    /// without checking <c>Org.OrgType</c>. The resolvers (<c>TenantContext</c>, <c>OrgContextResolver</c>,
    /// <c>OrgService</c>) no longer do that; this migration repairs the rows they leave behind.
    /// </summary>
    /// <remarks>
    /// <para>Prudent on purpose, like <c>BackfillGuestOrgIds</c>: for every <c>User.OrgId</c> that points at a
    /// Supplier-type org, it first backfills <c>SupplierOrgId</c> (in case it was somehow still unset), then
    /// clears <c>OrgId</c> to <c>null</c> — but <b>only</b> when that Supplier org holds no host business data
    /// (no <c>Properties</c>, <c>Bookings</c>, <c>LeaseContracts</c>, <c>Payments</c> or <c>Guests</c> reference
    /// it). That is the common case: the account registered as a supplier and never actually reached the host
    /// onboarding through it. A Supplier org that already accumulated host data (the bug ran to completion for
    /// that user before this fix shipped) is left untouched: moving that data to a fresh Host org is a product
    /// decision (which org keeps the Stripe customer, the plan, the bookings) this migration does not make on its
    /// own. See docs/runbooks/suppliers.md §13 for the post-deploy check and what to do with a non-zero count.
    /// </para>
    /// </remarks>
    public partial class SeparateSupplierOrgFromHostOrgId : Migration
    {
        /// <summary>The repair. Public so that a PostgreSQL test runs exactly this statement.</summary>
        public const string BackfillSql = """
            DO $$
            DECLARE
                v_backfilled_supplier_link bigint;
                v_host_link_cleared bigint;
                v_needs_manual_repair bigint;
            BEGIN
                -- Preserve the supplier link before touching OrgId: a user whose OrgId already points to a
                -- Supplier-type org but never got SupplierOrgId set (pre-SU-08 accounts) keeps that link explicitly.
                UPDATE "Users" u
                SET "SupplierOrgId" = u."OrgId"
                FROM "Orgs" o
                WHERE o."Id" = u."OrgId"
                  AND o."OrgType" = 1
                  AND u."SupplierOrgId" IS NULL;
                GET DIAGNOSTICS v_backfilled_supplier_link = ROW_COUNT;

                -- Clear the mistaken host link only when the Supplier org carries no host business data: the fixed
                -- resolvers would otherwise orphan real properties/bookings by pointing this user at a brand new,
                -- empty Host org on their next request instead of the org that actually holds their data.
                UPDATE "Users" u
                SET "OrgId" = NULL
                FROM "Orgs" o
                WHERE o."Id" = u."OrgId"
                  AND o."OrgType" = 1
                  AND NOT EXISTS (SELECT 1 FROM "Properties" p WHERE p."OrgId" = o."Id")
                  AND NOT EXISTS (SELECT 1 FROM "Bookings" b WHERE b."OrgId" = o."Id")
                  AND NOT EXISTS (SELECT 1 FROM "LeaseContracts" l WHERE l."OrgId" = o."Id")
                  AND NOT EXISTS (SELECT 1 FROM "Payments" pay WHERE pay."OrgId" = o."Id")
                  AND NOT EXISTS (SELECT 1 FROM "Guests" g WHERE g."OrgId" = o."Id");
                GET DIAGNOSTICS v_host_link_cleared = ROW_COUNT;

                -- Left untouched on purpose: a Supplier org that already holds host business data. Flagged here
                -- for manual follow-up, never auto-repaired (docs/runbooks/suppliers.md §13).
                SELECT count(*) INTO v_needs_manual_repair
                FROM "Users" u
                JOIN "Orgs" o ON o."Id" = u."OrgId"
                WHERE o."OrgType" = 1;

                RAISE NOTICE 'SeparateSupplierOrgFromHostOrgId: supplier_link_backfilled=%, host_link_cleared=%, needs_manual_repair=%',
                    v_backfilled_supplier_link, v_host_link_cleared, v_needs_manual_repair;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Logical no-op, like BackfillGuestOrgIds / AssignNoneRoleToUsersWithoutOnboarding: the cleared
            // OrgId values were never recorded anywhere, so they cannot be restored, and restoring the old
            // OrgId = Supplier-org link would simply reintroduce the A1-40 bug for every affected user.
        }
    }
}
