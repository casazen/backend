using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126023000_GuestSupplierOrderDocuments")]
public sealed class GuestSupplierOrderDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("GuestSupplierOrderDocuments", table => new
        {
            Id = table.Column<Guid>("uuid", nullable: false),
            OrderId = table.Column<Guid>("uuid", nullable: false),
            ContentHash = table.Column<string>("character varying(64)", maxLength: 64, nullable: false),
            FileName = table.Column<string>("character varying(180)", maxLength: 180, nullable: false),
            UploadedAt = table.Column<DateTime>("timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_GuestSupplierOrderDocuments", d => d.Id);
            table.ForeignKey("FK_GuestSupplierOrderDocuments_GuestSupplierOrders_OrderId", d => d.OrderId,
                "GuestSupplierOrders", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_GuestSupplierOrderDocuments_OrderId_ContentHash", "GuestSupplierOrderDocuments",
            new[] { "OrderId", "ContentHash" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("GuestSupplierOrderDocuments");
}
