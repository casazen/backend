using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PL-02 (A1-05) data migration: <c>UserRole.None</c> (7, appended to the enum, existing values unchanged) for the
    /// users who never started using the platform. Until now every new user was stored as <c>PropertyOwner</c> (1) by
    /// default; that default is what granted the host context without onboarding and consents.
    /// </summary>
    /// <remarks>
    /// <para>Prudent on purpose: only a <c>PropertyOwner</c> row with no trace of use at all moves to <c>None</c>: no
    /// completed onboarding, no rental type, no org, no supplier link, no consent, no property. Everybody else keeps the
    /// role: admins, suppliers, landlords, users who completed the onboarding, and users who already work in an org (org
    /// auto-provisioned before PL-02). The latter lose nothing, but the host onboarding gate asks them to complete the
    /// onboarding and accept the current consents before the host features open again. Context memberships are not
    /// touched.</para>
    /// <para>Users who completed the onboarding before <c>OnboardingCompletedAt</c> existed (16 June 2026) have a rental
    /// type, written only by the onboarding, and no timestamp: they get one (their first consent, else the account
    /// creation, an approximation), so that the gate asks them only for the current consents instead of the whole
    /// onboarding. The timestamp grants nothing by itself. See docs/runbooks/onboarding-consents.md.</para>
    /// </remarks>
    public partial class AssignNoneRoleToUsersWithoutOnboarding : Migration
    {
        /// <summary>The backfill and the reclassification. Public so that a PostgreSQL test runs exactly these statements.</summary>
        public const string BackfillSql = """
            UPDATE "Users" AS u
            SET "OnboardingCompletedAt" = COALESCE(
                (SELECT min(c."RecordedAt") FROM "ConsentRecords" AS c WHERE c."UserId" = u."Id"),
                u."CreatedAt")
            WHERE u."OnboardingCompletedAt" IS NULL
              AND u."RentalType" IS NOT NULL;

            UPDATE "Users" AS u
            SET "Role" = 7
            WHERE u."Role" = 1
              AND u."OnboardingCompletedAt" IS NULL
              AND u."RentalType" IS NULL
              AND u."OrgId" IS NULL
              AND u."SupplierOrgId" IS NULL
              AND NOT EXISTS (SELECT 1 FROM "ConsentRecords" AS c WHERE c."UserId" = u."Id")
              AND NOT EXISTS (SELECT 1 FROM "Properties" AS p WHERE p."OwnerId" = u."Id");
            """;

        /// <summary>
        /// Reverts to the previous default: code without <c>UserRole.None</c> stores every user without a role as
        /// <c>PropertyOwner</c>. The backfilled onboarding timestamps stay: the previous code treats those users as
        /// onboarded anyway (they have a rental type).
        /// </summary>
        public const string RevertSql = """
            UPDATE "Users" SET "Role" = 1 WHERE "Role" = 7;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RevertSql);
        }
    }
}
