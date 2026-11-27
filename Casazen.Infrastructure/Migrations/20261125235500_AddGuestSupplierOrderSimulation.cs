using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125235500_AddGuestSupplierOrderSimulation")]
public sealed class AddGuestSupplierOrderSimulation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GuestSupplierOffers",
            columns: table => new
            {
                Id = table.Column<Guid>("uuid", nullable: false),
                HostOrgId = table.Column<Guid>("uuid", nullable: false),
                PropertyId = table.Column<Guid>("uuid", nullable: false),
                ServiceId = table.Column<Guid>("uuid", nullable: false),
                SupplierOrgId = table.Column<Guid>("uuid", nullable: false),
                HostOrigin = table.Column<bool>("boolean", nullable: false),
                CommissionBasisPoints = table.Column<int>("integer", nullable: false),
                Accepted = table.Column<bool>("boolean", nullable: false),
                Enabled = table.Column<bool>("boolean", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GuestSupplierOffers", x => x.Id);
                table.ForeignKey(
                    name: "FK_GuestSupplierOffers_Orgs_HostOrgId",
                    column: x => x.HostOrgId,
                    principalTable: "Orgs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GuestSupplierOffers_Properties_PropertyId",
                    column: x => x.PropertyId,
                    principalTable: "Properties",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GuestSupplierOffers_SupplierServiceListings_ServiceId",
                    column: x => x.ServiceId,
                    principalTable: "SupplierServiceListings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GuestSupplierOffers_Orgs_SupplierOrgId",
                    column: x => x.SupplierOrgId,
                    principalTable: "Orgs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOffers_HostOrgId", table: "GuestSupplierOffers", column: "HostOrgId");
        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOffers_ServiceId", table: "GuestSupplierOffers", column: "ServiceId");
        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOffers_SupplierOrgId", table: "GuestSupplierOffers", column: "SupplierOrgId");
        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOffers_PropertyId_ServiceId", table: "GuestSupplierOffers", columns: new[] { "PropertyId", "ServiceId" }, unique: true);

        migrationBuilder.CreateTable(
            name: "GuestSupplierOrders",
            columns: table => new
            {
                Id = table.Column<Guid>("uuid", nullable: false),
                HostOrgId = table.Column<Guid>("uuid", nullable: false),
                BookingId = table.Column<Guid>("uuid", nullable: false),
                PropertyId = table.Column<Guid>("uuid", nullable: false),
                OfferId = table.Column<Guid>("uuid", nullable: false),
                SupplierOrgId = table.Column<Guid>("uuid", nullable: false),
                RequestId = table.Column<Guid>("uuid", nullable: false),
                HostOrigin = table.Column<bool>("boolean", nullable: false),
                ServiceName = table.Column<string>("character varying(250)", maxLength: 250, nullable: false),
                SupplierName = table.Column<string>("character varying(250)", maxLength: 250, nullable: false),
                CommissionBasisPoints = table.Column<int>("integer", nullable: false),
                SupplierPriceCents = table.Column<int>("integer", nullable: false),
                CommissionCents = table.Column<int>("integer", nullable: false),
                RefundCents = table.Column<int>("integer", nullable: false),
                State = table.Column<int>("integer", nullable: false),
                RequestedFor = table.Column<DateTime>("timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTime>("timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>("timestamp with time zone", nullable: true),
                RequestNote = table.Column<string>("character varying(2000)", maxLength: 2000, nullable: true),
                Complaint = table.Column<string>("character varying(2000)", maxLength: 2000, nullable: true),
                SupplierReply = table.Column<string>("character varying(2000)", maxLength: 2000, nullable: true),
                Decision = table.Column<string>("character varying(2000)", maxLength: 2000, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GuestSupplierOrders", x => x.Id);
                table.ForeignKey(
                    name: "FK_GuestSupplierOrders_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GuestSupplierOrders_Orgs_HostOrgId",
                    column: x => x.HostOrgId,
                    principalTable: "Orgs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GuestSupplierOrders_Orgs_SupplierOrgId",
                    column: x => x.SupplierOrgId,
                    principalTable: "Orgs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_GuestSupplierOrders_Properties_PropertyId",
                    column: x => x.PropertyId,
                    principalTable: "Properties",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOrders_PropertyId", table: "GuestSupplierOrders", column: "PropertyId");
        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOrders_BookingId_RequestId", table: "GuestSupplierOrders", columns: new[] { "BookingId", "RequestId" }, unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOrders_HostOrgId_PropertyId", table: "GuestSupplierOrders", columns: new[] { "HostOrgId", "PropertyId" });
        migrationBuilder.CreateIndex(
            name: "IX_GuestSupplierOrders_SupplierOrgId_State", table: "GuestSupplierOrders", columns: new[] { "SupplierOrgId", "State" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GuestSupplierOrders");
        migrationBuilder.DropTable("GuestSupplierOffers");
    }
}
