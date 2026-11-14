using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWireBatchPayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DebitAccount",
                table: "WireBatches",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(34)",
                oldMaxLength: 34);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExecutionDate",
                table: "WireBatches",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageFormat",
                table: "WireBatches",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WireBatchPayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    WireBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerPayoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EndToEndId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WireBatchPayouts", x => x.Id);
                    table.CheckConstraint("CK_WireBatchPayouts_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_WireBatchPayouts_OwnerPayouts_OwnerPayoutId",
                        column: x => x.OwnerPayoutId,
                        principalTable: "OwnerPayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WireBatchPayouts_PropertyOwners_PropertyOwnerId",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WireBatchPayouts_WireBatches_WireBatchId",
                        column: x => x.WireBatchId,
                        principalTable: "WireBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WireBatchPayouts_OrgId_WireBatchId",
                table: "WireBatchPayouts",
                columns: new[] { "OrgId", "WireBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_WireBatchPayouts_PropertyOwnerId",
                table: "WireBatchPayouts",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_WireBatchPayouts_WireBatchId_EndToEndId",
                table: "WireBatchPayouts",
                columns: new[] { "WireBatchId", "EndToEndId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_WireBatchPayouts_OwnerPayoutId",
                table: "WireBatchPayouts",
                column: "OwnerPayoutId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WireBatchPayouts");

            migrationBuilder.DropColumn(
                name: "ExecutionDate",
                table: "WireBatches");

            migrationBuilder.DropColumn(
                name: "MessageFormat",
                table: "WireBatches");

            migrationBuilder.AlterColumn<string>(
                name: "DebitAccount",
                table: "WireBatches",
                type: "character varying(34)",
                maxLength: 34,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
