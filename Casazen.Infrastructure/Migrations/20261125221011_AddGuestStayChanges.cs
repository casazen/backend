using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125221011_AddGuestStayChanges")]
public sealed class AddGuestStayChanges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "GuestStayChanges",
            table => new
            {
                Id = table.Column<Guid>("uuid", nullable: false),
                OrgId = table.Column<Guid>("uuid", nullable: false),
                BookingId = table.Column<Guid>("uuid", nullable: false),
                OriginalArrival = table.Column<DateTime>("timestamp with time zone", nullable: false),
                OriginalDeparture = table.Column<DateTime>("timestamp with time zone", nullable: false),
                OriginalGuests = table.Column<int>("integer", nullable: false),
                OriginalChildren = table.Column<int>("integer", nullable: false),
                Arrival = table.Column<DateTime>("timestamp with time zone", nullable: false),
                Departure = table.Column<DateTime>("timestamp with time zone", nullable: false),
                Adults = table.Column<int>("integer", nullable: false),
                ChildrenAges = table.Column<int[]>("integer[]", nullable: false),
                OriginalTotal = table.Column<decimal>("numeric", nullable: false),
                ProposedTotal = table.Column<decimal>("numeric", nullable: false),
                Currency = table.Column<string>("character varying(3)", maxLength: 3, nullable: false),
                CreatedAt = table.Column<DateTime>("timestamp with time zone", nullable: false),
                RejectedAt = table.Column<DateTime>("timestamp with time zone", nullable: true),
                ApprovedAt = table.Column<DateTime>("timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GuestStayChanges", change => change.Id);
                table.ForeignKey("FK_GuestStayChanges_Bookings_BookingId", change => change.BookingId,
                    "Bookings", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_GuestStayChanges_Orgs_OrgId", change => change.OrgId,
                    "Orgs", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_GuestStayChanges_OrgId", "GuestStayChanges", "OrgId");
        migrationBuilder.CreateIndex("UIX_GuestStayChanges_Pending", "GuestStayChanges", "BookingId", unique: true,
            filter: "\"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("GuestStayChanges");
}
