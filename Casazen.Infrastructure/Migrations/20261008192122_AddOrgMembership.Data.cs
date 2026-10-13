using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Data of the AM-01 migration: the backfill of the owners (same convention as <c>AddLtrReferenceDataAdmin.Data.cs</c>:
    /// inline values, never reads the application's code, so the migration does the same thing whatever the code becomes).
    /// </summary>
    /// <remarks>
    /// <para>Every <b>host</b> org that has exactly one owner candidate gets an <c>OrgMembers</c> row for it (Owner, active,
    /// all properties) and the <c>account/org_owner</c> membership that projects it, so every owner has them from the first
    /// minute. An owner candidate is a user of the org with a user role of an owner (1 <c>PropertyOwner</c>, 5
    /// <c>LongTermLandlord</c>) or an owner's host membership (<c>short-rent/property_owner</c>,
    /// <c>long-rent/long_term_landlord</c>): the latter is how a platform admin that set up its own org shows.</para>
    /// <para><b>Nothing is guessed.</b> An org with several candidates (it should not exist: one org per user, created at the
    /// onboarding) is logged with a warning and left without an org member; so are the users of an org that are not its
    /// owner. Supplier orgs (<c>OrgType</c> 1) are not org teams. The read-only queries below list all of that before the
    /// deploy (<c>docs/runbooks/org-team.md</c>); after it the reconcile command
    /// (<c>POST /api/admin/org-members/reconcile</c>) reports the same, with the same rule.</para>
    /// <para>Idempotent: <c>ON CONFLICT DO NOTHING</c> on the unique keys, so running the statements again changes nothing.
    /// Public so that a PostgreSQL test runs exactly these statements.</para>
    /// </remarks>
    public partial class AddOrgMembership
    {
        /// <summary>
        /// The owner candidates of the host orgs, one row per user: <c>UserId</c>, <c>OrgId</c>. Reads nothing that the
        /// migration creates, so it also runs <b>before</b> it (the pre-deploy dry run).
        /// </summary>
        public const string OwnerCandidatesSql = """
            SELECT u."Id" AS "UserId", u."OrgId" AS "OrgId"
            FROM "Users" AS u
            JOIN "Orgs" AS o ON o."Id" = u."OrgId"
            WHERE o."OrgType" = 0
              AND (u."Role" IN (1, 5)
                   OR EXISTS (
                       SELECT 1
                       FROM "UserContextMemberships" AS m
                       JOIN "Roles" AS r ON r."Id" = m."RoleId"
                       WHERE m."UserId" = u."Id"
                         AND ((m."ContextKey" = 'short-rent' AND r."RoleKey" = 'property_owner')
                              OR (m."ContextKey" = 'long-rent' AND r."RoleKey" = 'long_term_landlord'))))
            """;

        /// <summary>The backfill: owners and their account memberships, with a warning per ambiguous org.</summary>
        public const string BackfillSql = $$"""
            DO $$
            DECLARE
                v_owners    bigint;
                v_projected bigint;
                v_org       RECORD;
            BEGIN
                -- 1) The owner of every host org with exactly one candidate.
                INSERT INTO "OrgMembers" ("Id", "OrgId", "UserId", "Role", "Status", "PropertyScope", "CreatedAt", "CreatedByUserId", "DeactivatedAt")
                SELECT gen_random_uuid(), c."OrgId", c."UserId", 1, 1, 1, now(), NULL, NULL
                FROM ({{OwnerCandidatesSql}}) AS c
                WHERE c."OrgId" IN (
                    SELECT d."OrgId"
                    FROM ({{OwnerCandidatesSql}}) AS d
                    GROUP BY d."OrgId"
                    HAVING COUNT(*) = 1)
                ON CONFLICT ("UserId") DO NOTHING;
                GET DIAGNOSTICS v_owners = ROW_COUNT;

                -- 2) Their account membership (the projection of the Owner role into permissions).
                INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
                SELECT gen_random_uuid(), m."UserId", 'account', r."Id"
                FROM "OrgMembers" AS m
                JOIN "Roles" AS r ON r."ContextKey" = 'account' AND r."RoleKey" = 'org_owner'
                WHERE m."Role" = 1
                ON CONFLICT ("UserId", "ContextKey") DO NOTHING;
                GET DIAGNOSTICS v_projected = ROW_COUNT;

                -- 3) Several candidates in one org: nobody is made the owner, the org is logged.
                FOR v_org IN
                    SELECT d."OrgId" AS org_id,
                           COUNT(*) AS candidates,
                           string_agg(d."UserId", ', ' ORDER BY d."UserId") AS users
                    FROM ({{OwnerCandidatesSql}}) AS d
                    GROUP BY d."OrgId"
                    HAVING COUNT(*) > 1
                LOOP
                    RAISE WARNING 'AddOrgMembership: org % has % owner candidates (%): no org member created, see docs/runbooks/org-team.md',
                        v_org.org_id, v_org.candidates, v_org.users;
                END LOOP;

                RAISE NOTICE 'AddOrgMembership: owners=%, account_memberships=%', v_owners, v_projected;
            END $$;
            """;

        /// <summary>Pre-deploy dry run 1: the orgs the backfill leaves without owner (several candidates), with the users.</summary>
        public const string AmbiguousOrgsSql = $$"""
            SELECT d."OrgId", COUNT(*) AS "Candidates", string_agg(d."UserId", ', ' ORDER BY d."UserId") AS "Users"
            FROM ({{OwnerCandidatesSql}}) AS d
            GROUP BY d."OrgId"
            HAVING COUNT(*) > 1
            ORDER BY d."OrgId"
            """;

        /// <summary>Pre-deploy dry run 2: the owners the backfill creates (the orgs with exactly one candidate).</summary>
        public const string OwnersToCreateSql = $$"""
            SELECT c."OrgId", c."UserId"
            FROM ({{OwnerCandidatesSql}}) AS c
            WHERE c."OrgId" IN (
                SELECT d."OrgId"
                FROM ({{OwnerCandidatesSql}}) AS d
                GROUP BY d."OrgId"
                HAVING COUNT(*) = 1)
            ORDER BY c."OrgId"
            """;

        /// <summary>
        /// Pre-deploy dry run 3: the users of a host org that are not its owner candidates and get no org member (their
        /// role in the org is not guessed): a property manager, a collaborator, a user that never onboarded...
        /// </summary>
        public const string UsersWithoutMemberSql = $$"""
            SELECT u."OrgId", u."Id" AS "UserId", u."Role"
            FROM "Users" AS u
            JOIN "Orgs" AS o ON o."Id" = u."OrgId"
            WHERE o."OrgType" = 0
              AND u."Id" NOT IN (SELECT c."UserId" FROM ({{OwnerCandidatesSql}}) AS c)
            ORDER BY u."OrgId", u."Id"
            """;

        /// <summary>
        /// Pre-deploy dry run 4: the owner candidates with no short-rent or long-rent membership in the DB. The token of such
        /// an owner still completes its rental contexts after the deploy (the veto of the token starts with the first
        /// rental membership), but the DB does not hold them: give the owner its host membership (its onboarding does) so
        /// that the DB is the whole truth. The reconcile reports the same owners as <c>owner_without_host_membership</c>.
        /// </summary>
        public const string OwnersWithoutRentalMembershipSql = $$"""
            SELECT c."OrgId", c."UserId"
            FROM ({{OwnerCandidatesSql}}) AS c
            WHERE NOT EXISTS (
                SELECT 1
                FROM "UserContextMemberships" AS m
                WHERE m."UserId" = c."UserId"
                  AND m."ContextKey" IN ('short-rent', 'long-rent'))
            ORDER BY c."OrgId", c."UserId"
            """;

        /// <summary>
        /// Rollback of the data: the memberships of the seeded roles (4 to 12) go before the roles do. The EF statements of
        /// <c>Down</c> would cascade to them anyway; it is said here so that nobody has to know.
        /// </summary>
        public const string RevertSql = """
            DELETE FROM "UserContextMemberships" WHERE "RoleId" BETWEEN 4 AND 12;
            """;

        private static void ApplyData(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(BackfillSql);

        private static void RevertData(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RevertSql);
    }
}
