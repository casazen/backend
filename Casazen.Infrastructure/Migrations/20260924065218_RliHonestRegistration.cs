using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// LT-01 (A7-01, A7-21): honest RLI registration. Until LT-01 the only provider was a stub that returned
    /// <c>RLI-STUB-{id}</c> and never filed anything, so no existing submission is real.
    /// </summary>
    /// <remarks>
    /// <para>Schema: <c>Channel</c> (0 provider, 1 manual), <c>RegistrationDate</c>, <c>FailureCode</c>,
    /// <c>RequestedAt</c>, <c>DeclaredByUserId</c>, and the check constraint
    /// <c>CK_LeaseRegistrations_RegisteredRequiresReceipt</c>: status Registered (2) only with a stored receipt.</para>
    /// <para>Data, before the constraint (<see cref="FailSimulatedSubmissionsSql"/>, <see cref="ReopenLeasesSql"/>): stub
    /// submissions become Failed (3) with <c>simulated_submission</c>; reservations left Pending (0) by an exception and
    /// Registered rows without a receipt become Failed with <c>provider_outcome_unknown</c>; each gets a
    /// <c>RegistrationFailed</c> (6) timeline event. Leases "in progress" or "registered" (4, 5, 6) without a live or
    /// registered registration go back to Signed (3), so the landlord sees "to register" and can register manually.</para>
    /// <para>Down drops the columns and the constraint; the data changes are not reverted (the old statuses were false).
    /// Runbook: <c>docs/runbooks/rli.md</c>.</para>
    /// </remarks>
    public partial class RliHonestRegistration : Migration
    {
        /// <summary>Stub, orphan and receipt-less registrations to Failed, with a timeline event.</summary>
        public const string FailSimulatedSubmissionsSql = """
            WITH failed AS (
                UPDATE "LeaseRegistrations"
                SET "Status" = 3,
                    "FailureCode" = CASE
                        WHEN "ExternalRegistrationId" LIKE 'RLI-STUB-%' THEN 'simulated_submission'
                        ELSE 'provider_outcome_unknown'
                    END,
                    "RegistrationCode" = NULL,
                    "ConfirmedAt" = NULL
                WHERE "ExternalRegistrationId" LIKE 'RLI-STUB-%'
                   OR "Status" = 0
                   OR ("Status" = 2 AND btrim(coalesce("ReceiptStoragePath", '')) = '')
                RETURNING "LeaseContractId", "FailureCode"
            )
            INSERT INTO "LeaseEvents" ("Id", "LeaseContractId", "EventType", "OccurredAt", "Payload")
            SELECT gen_random_uuid(), "LeaseContractId", 6, now(), "FailureCode"
            FROM failed;
            """;

        /// <summary>Leases shown as in progress or registered without a registration that supports it: back to Signed.</summary>
        public const string ReopenLeasesSql = """
            UPDATE "LeaseContracts" AS l
            SET "Status" = 3, "UpdatedAt" = now()
            WHERE l."Status" IN (4, 5, 6)
              AND NOT EXISTS (
                  SELECT 1 FROM "LeaseRegistrations" AS r
                  WHERE r."LeaseContractId" = l."Id" AND r."Status" IN (1, 2));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "LeaseRegistrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeclaredByUserId",
                table: "LeaseRegistrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "LeaseRegistrations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RegistrationDate",
                table: "LeaseRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequestedAt",
                table: "LeaseRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(FailSimulatedSubmissionsSql);
            migrationBuilder.Sql(ReopenLeasesSql);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LeaseRegistrations_RegisteredRequiresReceipt",
                table: "LeaseRegistrations",
                sql: "\"Status\" <> 2 OR btrim(coalesce(\"ReceiptStoragePath\", '')) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LeaseRegistrations_RegisteredRequiresReceipt",
                table: "LeaseRegistrations");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "LeaseRegistrations");

            migrationBuilder.DropColumn(
                name: "DeclaredByUserId",
                table: "LeaseRegistrations");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "LeaseRegistrations");

            migrationBuilder.DropColumn(
                name: "RegistrationDate",
                table: "LeaseRegistrations");

            migrationBuilder.DropColumn(
                name: "RequestedAt",
                table: "LeaseRegistrations");
        }
    }
}
