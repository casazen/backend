using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInternalActivityTodo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "StaffUserId",
                table: "InternalActivities",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "InternalActivities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "InternalActivities",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedBy",
                table: "InternalActivities",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Deadline",
                table: "InternalActivities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EntityId",
                table: "InternalActivities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityType",
                table: "InternalActivities",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "InternalActivities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Urgency",
                table: "InternalActivities",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalActivities_OpenTodo",
                table: "InternalActivities",
                columns: new[] { "OrgId", "Urgency", "Deadline" },
                filter: "\"Action\" IS NOT NULL AND \"CompletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_InternalActivities_OpenTodo",
                table: "InternalActivities",
                columns: new[] { "OrgId", "ActivityType", "EntityType", "EntityId" },
                unique: true,
                filter: "\"Action\" IS NOT NULL AND \"CompletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InternalActivities_OpenTodo",
                table: "InternalActivities");

            migrationBuilder.DropIndex(
                name: "UIX_InternalActivities_OpenTodo",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "ClosedBy",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "Deadline",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "EntityType",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "InternalActivities");

            migrationBuilder.DropColumn(
                name: "Urgency",
                table: "InternalActivities");

            migrationBuilder.AlterColumn<string>(
                name: "StaffUserId",
                table: "InternalActivities",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "InternalActivities",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
