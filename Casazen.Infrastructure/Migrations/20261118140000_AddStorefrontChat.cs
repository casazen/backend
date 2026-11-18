using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261118140000_AddStorefrontChat")]
public sealed class AddStorefrontChat : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "StorefrontChatThreads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                AccessHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                VisitorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                VisitorEmail = table.Column<string>(type: "text", nullable: false),
                LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                NeedsReply = table.Column<bool>(type: "boolean", nullable: false),
                UnreadCount = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontChatThreads", x => x.Id);
                table.ForeignKey("FK_StorefrontChatThreads_Properties_PropertyId", x => x.PropertyId, "Properties", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "StorefrontChatMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Direction = table.Column<int>(type: "integer", nullable: false),
                Text = table.Column<string>(type: "text", nullable: false),
                SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontChatMessages", x => x.Id);
                table.ForeignKey("FK_StorefrontChatMessages_StorefrontChatThreads_ThreadId", x => x.ThreadId, "StorefrontChatThreads", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_StorefrontChatThreads_PropertyId", "StorefrontChatThreads", "PropertyId");
        migrationBuilder.CreateIndex("IX_StorefrontChatThreads_OrgId_LastMessageAt", "StorefrontChatThreads", new[] { "OrgId", "LastMessageAt" });
        migrationBuilder.CreateIndex("IX_StorefrontChatThreads_OrgId_PropertyId_AccessHash", "StorefrontChatThreads", new[] { "OrgId", "PropertyId", "AccessHash" }, unique: true);
        migrationBuilder.CreateIndex("IX_StorefrontChatMessages_ThreadId_SentAt", "StorefrontChatMessages", new[] { "ThreadId", "SentAt" });
        migrationBuilder.CreateIndex("IX_StorefrontChatMessages_ThreadId_Direction_IdempotencyKey", "StorefrontChatMessages", new[] { "ThreadId", "Direction", "IdempotencyKey" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("StorefrontChatMessages");
        migrationBuilder.DropTable("StorefrontChatThreads");
    }
}
