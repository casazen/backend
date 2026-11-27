using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BiometricConsentAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BiometricConsentRefusedAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BiometricConsentVersion",
                table: "GuestCheckInSessions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentCheckJson",
                table: "GuestCheckInSessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorRequestedAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "BiometricVerificationLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Method",
                table: "BiometricVerificationLogs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "Provisional",
                table: "BiometricVerificationLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReasonsJson",
                table: "BiometricVerificationLogs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTime>(
                name: "CalibratedAt",
                table: "BiometricSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[,]
                {
                    { "verification.decision", 1 },
                    { "verification.decision", 7 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "verification.decision", 1 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "verification.decision", 7 });

            migrationBuilder.DropColumn(
                name: "BiometricConsentAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "BiometricConsentRefusedAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "BiometricConsentVersion",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "DocumentCheckJson",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "OperatorRequestedAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "Provisional",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "ReasonsJson",
                table: "BiometricVerificationLogs");

            migrationBuilder.DropColumn(
                name: "CalibratedAt",
                table: "BiometricSettings");
        }
    }
}
