using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExistingInventoryConnectAndAssistChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartAndProperty",
                table: "OnboardingAssistRequests");

            migrationBuilder.AddColumn<string>(
                name: "ChannelCode",
                table: "OnboardingAssistRequests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectedAccommodationIdsJson",
                table: "OctorateInventoryImportChannels",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel",
                table: "OnboardingAssistRequests",
                columns: new[] { "OrgId", "PartKey", "PropertyId", "ChannelCode" },
                unique: true,
                filter: "\"Status\" IN (1, 2)")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel",
                table: "OnboardingAssistRequests");

            migrationBuilder.DropColumn(
                name: "ChannelCode",
                table: "OnboardingAssistRequests");

            migrationBuilder.DropColumn(
                name: "ConnectedAccommodationIdsJson",
                table: "OctorateInventoryImportChannels");

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartAndProperty",
                table: "OnboardingAssistRequests",
                columns: new[] { "OrgId", "PartKey", "PropertyId" },
                unique: true,
                filter: "\"Status\" IN (1, 2)")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
