using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmMarketingStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MarketingGranted",
                table: "OrgMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MarketingCategoryOverridesJson",
                table: "GuestCrmProfiles",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "Campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CampaignDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Opened = table.Column<bool>(type: "boolean", nullable: false),
                    Clicked = table.Column<bool>(type: "boolean", nullable: false),
                    Bounced = table.Column<bool>(type: "boolean", nullable: false),
                    UnsubscribedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketingAiUsage",
                columns: table => new
                {
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Requests = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingAiUsage", x => new { x.OrgId, x.Month });
                });

            migrationBuilder.CreateTable(
                name: "MarketingAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SegmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Occurrence = table.Column<string>(type: "text", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Keywords = table.Column<string>(type: "text", nullable: false),
                    Audience = table.Column<string>(type: "text", nullable: false),
                    Budget = table.Column<string>(type: "text", nullable: false),
                    Destination = table.Column<string>(type: "text", nullable: true),
                    ExternalDestination = table.Column<bool>(type: "boolean", nullable: false),
                    SourceEventJson = table.Column<string>(type: "jsonb", nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Clicks = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketingEventSearches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Locality = table.Column<string>(type: "text", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingEventSearches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketingStudioSettings",
                columns: table => new
                {
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonthlyEmailLimit = table.Column<int>(type: "integer", nullable: false),
                    LoyalStays = table.Column<int>(type: "integer", nullable: false),
                    LostMonths = table.Column<int>(type: "integer", nullable: false),
                    Voice = table.Column<string>(type: "text", nullable: false),
                    PropertyNotesJson = table.Column<string>(type: "jsonb", nullable: false),
                    PropertyUrlsJson = table.Column<string>(type: "jsonb", nullable: false),
                    NewsletterJson = table.Column<string>(type: "jsonb", nullable: false),
                    NextNewsletterAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextEventSearchAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingStudioSettings", x => x.OrgId);
                });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[] { "marketing.manage", 1 });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliveries_CampaignId_ProfileId",
                table: "CampaignDeliveries",
                columns: new[] { "CampaignId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliveries_OrgId_ProfileId_Month_Slot",
                table: "CampaignDeliveries",
                columns: new[] { "OrgId", "ProfileId", "Month", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketingAssets_OrgId_Occurrence",
                table: "MarketingAssets",
                columns: new[] { "OrgId", "Occurrence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketingAssets_OrgId_PropertyId_CreatedAt",
                table: "MarketingAssets",
                columns: new[] { "OrgId", "PropertyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketingEventSearches_Locality_From_To",
                table: "MarketingEventSearches",
                columns: new[] { "Locality", "From", "To" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignDeliveries");

            migrationBuilder.DropTable(
                name: "MarketingAiUsage");

            migrationBuilder.DropTable(
                name: "MarketingAssets");

            migrationBuilder.DropTable(
                name: "MarketingEventSearches");

            migrationBuilder.DropTable(
                name: "MarketingStudioSettings");

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "marketing.manage", 1 });

            migrationBuilder.DropColumn(
                name: "MarketingGranted",
                table: "OrgMembers");

            migrationBuilder.DropColumn(
                name: "MarketingCategoryOverridesJson",
                table: "GuestCrmProfiles");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "Campaigns");
        }
    }
}
