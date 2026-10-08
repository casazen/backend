using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierServiceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierServiceListings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Summary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PriceFromCents = table.Column<int>(type: "integer", nullable: true),
                    PriceUnit = table.Column<int>(type: "integer", nullable: false),
                    PricesIncludeVat = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresQuote = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    MinNoticeHours = table.Column<int>(type: "integer", nullable: true),
                    WeekdaysMask = table.Column<int>(type: "integer", nullable: false),
                    SupplementsJson = table.Column<string>(type: "jsonb", nullable: false),
                    IncludedJson = table.Column<string>(type: "jsonb", nullable: false),
                    ExcludedJson = table.Column<string>(type: "jsonb", nullable: false),
                    PhotoUrlsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierServiceListings", x => x.Id);
                    table.CheckConstraint("CK_SupplierServiceListings_DurationMinutes", "\"DurationMinutes\" IS NULL OR \"DurationMinutes\" > 0");
                    table.CheckConstraint("CK_SupplierServiceListings_MinNoticeHours", "\"MinNoticeHours\" IS NULL OR \"MinNoticeHours\" >= 0");
                    table.CheckConstraint("CK_SupplierServiceListings_PriceFromCents", "\"PriceFromCents\" IS NULL OR \"PriceFromCents\" > 0");
                    table.CheckConstraint("CK_SupplierServiceListings_WeekdaysMask", "\"WeekdaysMask\" BETWEEN 0 AND 127");
                    table.ForeignKey(
                        name: "FK_SupplierServiceListings_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UIX_SupplierServiceListings_OrgId_Slug",
                table: "SupplierServiceListings",
                columns: new[] { "OrgId", "Slug" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierServiceListings");
        }
    }
}
