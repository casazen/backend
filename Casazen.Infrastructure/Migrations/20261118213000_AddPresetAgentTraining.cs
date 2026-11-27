using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261118213000_AddPresetAgentTraining")]
public sealed class AddPresetAgentTraining : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("PresetAgentTraining", table => new
        {
            Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
            Revision = table.Column<int>(type: "integer", nullable: false),
            StateJson = table.Column<string>(type: "jsonb", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_PresetAgentTraining", x => x.Key));

        migrationBuilder.Sql("""
            REVOKE ALL ON "PresetAgentTraining" FROM PUBLIC;
            ALTER TABLE "PresetAgentTraining" ENABLE ROW LEVEL SECURITY;
            INSERT INTO "PresetAgentTraining" ("Key", "Revision", "StateJson") VALUES
                ('cleaning', 0, '{}'), ('arrival-instructions', 0, '{}'), ('review', 0, '{}'), ('document-reminder', 0, '{}');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PresetAgentTraining");
}
