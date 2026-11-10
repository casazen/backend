using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalDecisionAndOriginAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "ApprovalQueueItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConfidenceScore",
                table: "ApprovalQueueItems",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAt",
                table: "ApprovalQueueItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditedByUserId",
                table: "ApprovalQueueItems",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditedContentText",
                table: "ApprovalQueueItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginAction",
                table: "ApprovalQueueItems",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginActionError",
                table: "ApprovalQueueItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OriginActionExecutedAt",
                table: "ApprovalQueueItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginActionStatus",
                table: "ApprovalQueueItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "ApprovalQueueItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "ApprovalQueueItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SituationCode",
                table: "ApprovalQueueItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadKey",
                table: "ApprovalQueueItems",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[,]
                {
                    { "approval.decide", 1 },
                    { "approval.decide", 7 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueueItems_OrgId_Status",
                table: "ApprovalQueueItems",
                columns: new[] { "OrgId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalQueueItems_OrgId_Status",
                table: "ApprovalQueueItems");

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "approval.decide", 1 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "approval.decide", 7 });

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "ConfidenceScore",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "EditedByUserId",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "EditedContentText",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "OriginAction",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "OriginActionError",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "OriginActionExecutedAt",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "OriginActionStatus",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "SituationCode",
                table: "ApprovalQueueItems");

            migrationBuilder.DropColumn(
                name: "ThreadKey",
                table: "ApprovalQueueItems");
        }
    }
}
