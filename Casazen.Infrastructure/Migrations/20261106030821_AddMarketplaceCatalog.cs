using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketplaceActivations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    TargetCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsDeclared = table.Column<bool>(type: "boolean", nullable: false),
                    DeclaredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OctorateConnectionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastErrorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TodoActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketplaceActivations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketplaceActivations_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketplacePartners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Job = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    WebsiteUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LogoKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    OctoratePortalCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InvoicedBy = table.Column<int>(type: "integer", nullable: false),
                    ServiceCategories = table.Column<List<string>>(type: "text[]", nullable: false),
                    VisiblePlanTiers = table.Column<int[]>(type: "integer[]", nullable: false),
                    VisibleCountries = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketplacePartners", x => x.Id);
                    table.CheckConstraint("CK_MarketplacePartners_CodeSlug", "\"Code\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("CK_MarketplacePartners_WebsiteHttps", "\"WebsiteUrl\" LIKE 'https://%'");
                });

            migrationBuilder.CreateTable(
                name: "MarketplacePortals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortalCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Creatable = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Preferred = table.Column<bool>(type: "boolean", nullable: false),
                    Website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequiresHotelId = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresPassword = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresTokenAuthentication = table.Column<bool>(type: "boolean", nullable: false),
                    FeatureContent = table.Column<bool>(type: "boolean", nullable: false),
                    OctorateManageable = table.Column<bool>(type: "boolean", nullable: false),
                    CommissionIncludedInPayments = table.Column<bool>(type: "boolean", nullable: false),
                    CommissionsInvoiced = table.Column<bool>(type: "boolean", nullable: true),
                    IsListed = table.Column<bool>(type: "boolean", nullable: false),
                    RefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketplacePortals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketplacePartnerTexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Benefit = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Audience = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Requirements = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CostText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DataShared = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Deactivation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ActivationGuide = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    FaqJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketplacePartnerTexts", x => x.Id);
                    table.CheckConstraint("CK_MarketplacePartnerTexts_Language", "\"Language\" IN ('it', 'en')");
                    table.ForeignKey(
                        name: "FK_MarketplacePartnerTexts_MarketplacePartners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "MarketplacePartners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketplaceActivations_OrgId_Status",
                table: "MarketplaceActivations",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketplaceActivations_PropertyId",
                table: "MarketplaceActivations",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_MarketplaceActivations_OrgId_Kind_Target_OrgLevel",
                table: "MarketplaceActivations",
                columns: new[] { "OrgId", "Kind", "TargetCode" },
                unique: true,
                filter: "\"PropertyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_MarketplaceActivations_OrgId_PropertyId_Kind_Target",
                table: "MarketplaceActivations",
                columns: new[] { "OrgId", "PropertyId", "Kind", "TargetCode" },
                unique: true,
                filter: "\"PropertyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MarketplacePartners_Job_SortOrder",
                table: "MarketplacePartners",
                columns: new[] { "Job", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "UIX_MarketplacePartners_Code",
                table: "MarketplacePartners",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_MarketplacePartnerTexts_PartnerId_Language",
                table: "MarketplacePartnerTexts",
                columns: new[] { "PartnerId", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_MarketplacePortals_PortalCode",
                table: "MarketplacePortals",
                column: "PortalCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketplaceActivations");

            migrationBuilder.DropTable(
                name: "MarketplacePartnerTexts");

            migrationBuilder.DropTable(
                name: "MarketplacePortals");

            migrationBuilder.DropTable(
                name: "MarketplacePartners");
        }
    }
}
