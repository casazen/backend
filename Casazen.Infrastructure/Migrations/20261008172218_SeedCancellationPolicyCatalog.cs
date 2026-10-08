using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// PC-02: adds the three catalog fields (<c>Slug</c>, <c>GraceBookingDaysBeforeCheckin</c>,
    /// <c>GraceWindowHours</c>) and seeds the 5 short-stay cancellation policies
    /// (Ampia, Intermedia, Contenuta, Anticipata, Non rimborsabile). Long-term (≥28 nights)
    /// policies are out of scope. Seed data lives in the companion <c>.Data.cs</c> file.
    /// </remarks>
    public partial class SeedCancellationPolicyCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GraceBookingDaysBeforeCheckin",
                table: "CancellationPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GraceWindowHours",
                table: "CancellationPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "CancellationPolicies",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UIX_CancellationPolicies_Slug",
                table: "CancellationPolicies",
                column: "Slug",
                unique: true);

            // PC-02: seed the 5 short-stay cancellation policies (SeedCancellationPolicyCatalog.Data.cs).
            ApplyData(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RevertData(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "UIX_CancellationPolicies_Slug",
                table: "CancellationPolicies");

            migrationBuilder.DropColumn(
                name: "GraceBookingDaysBeforeCheckin",
                table: "CancellationPolicies");

            migrationBuilder.DropColumn(
                name: "GraceWindowHours",
                table: "CancellationPolicies");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "CancellationPolicies");
        }
    }
}
