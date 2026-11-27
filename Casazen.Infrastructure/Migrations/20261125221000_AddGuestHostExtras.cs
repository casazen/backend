using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125221000_AddGuestHostExtras")]
public sealed class AddGuestHostExtras : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GuestHostExtras",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                PropertyIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                Price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                Pricing = table.Column<int>(type: "integer", nullable: false),
                Inventory = table.Column<int>(type: "integer", nullable: true),
                CutoffHours = table.Column<int>(type: "integer", nullable: false),
                DuringStay = table.Column<bool>(type: "boolean", nullable: false),
                RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                Calendar = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table => table.PrimaryKey("GuestHostExtras_pkey", x => x.Id));

        migrationBuilder.CreateTable(
            name: "GuestHostExtraOrders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                ExtraId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                StripeIntentId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                StripeAccountId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
            },
            constraints: table => table.PrimaryKey("GuestHostExtraOrders_pkey", x => x.Id));

        migrationBuilder.CreateIndex("IX_GuestHostExtras_OrgId", "GuestHostExtras", "OrgId");
        migrationBuilder.CreateIndex("IX_GuestHostExtraOrders_OrgId_BookingId", "GuestHostExtraOrders", new[] { "OrgId", "BookingId" });
        migrationBuilder.CreateIndex("IX_GuestHostExtraOrders_OrgId_ExtraId", "GuestHostExtraOrders", new[] { "OrgId", "ExtraId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GuestHostExtraOrders");
        migrationBuilder.DropTable("GuestHostExtras");
    }
}
