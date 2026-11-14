using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerMandateAndChannelRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OwnerMandates_OrgId",
                table: "OwnerMandates");

            // The instant a mandate of before E13 started becomes its CreatedAt (no other trace of when it was written); its Rome day becomes its ValidFrom
            // below, before EffectiveUntil is dropped.
            migrationBuilder.RenameColumn(
                name: "EffectiveFrom",
                table: "OwnerMandates",
                newName: "CreatedAt");

            migrationBuilder.AlterColumn<string>(
                name: "BillingStreet",
                table: "PropertyOwners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingProvince",
                table: "PropertyOwners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingPostalCode",
                table: "PropertyOwners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingCountry",
                table: "PropertyOwners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingCivic",
                table: "PropertyOwners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingCity",
                table: "PropertyOwners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyOwnershipId",
                table: "OwnerMandates",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "CommissionBase",
                table: "OwnerMandates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "CommissionIncludesVat",
                table: "OwnerMandates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "OwnerMandates",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyOwnerId",
                table: "OwnerMandates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidFrom",
                table: "OwnerMandates",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidTo",
                table: "OwnerMandates",
                type: "date",
                nullable: true);

            // The mandates of before E13 (one ownership each; nothing wrote them, so normally none): their owner from the ownership, their days in Rome from
            // the instants they had (the end included, never before the start), the base of the default (⚖ R3), the percent and the flat fees brought within
            // the new checks. Run before EffectiveUntil goes and before the foreign key and the checks are added.
            migrationBuilder.Sql(
                """
                UPDATE "OwnerMandates" AS m
                SET "PropertyOwnerId" = o."PropertyOwnerId"
                FROM "PropertyOwnerships" AS o
                WHERE m."PropertyOwnershipId" = o."Id";

                UPDATE "OwnerMandates"
                SET "ValidFrom" = ("CreatedAt" AT TIME ZONE 'Europe/Rome')::date,
                    "ValidTo" = CASE
                        WHEN "EffectiveUntil" IS NULL THEN NULL
                        ELSE GREATEST(("EffectiveUntil" AT TIME ZONE 'Europe/Rome')::date, ("CreatedAt" AT TIME ZONE 'Europe/Rome')::date)
                    END,
                    "CommissionBase" = 1,
                    "CommissionPercent" = LEAST(GREATEST("CommissionPercent", 0), 100),
                    "CleaningFlatRate" = CASE WHEN "CleaningFlatRate" < 0 THEN 0 ELSE "CleaningFlatRate" END,
                    "LinenFlatRate" = CASE WHEN "LinenFlatRate" < 0 THEN 0 ELSE "LinenFlatRate" END;
                """);

            migrationBuilder.DropColumn(
                name: "EffectiveUntil",
                table: "OwnerMandates");

            migrationBuilder.CreateTable(
                name: "OrgChannelCommissionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Percent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    Base = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgChannelCommissionRules", x => x.Id);
                    table.CheckConstraint("CK_OrgChannelCommissionRules_Percent", "\"Percent\" >= 0 AND \"Percent\" <= 100");
                    table.CheckConstraint("CK_OrgChannelCommissionRules_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");
                    table.ForeignKey(
                        name: "FK_OrgChannelCommissionRules_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerMandates_PropertyOwnerId",
                table: "OwnerMandates",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerMandates_OrgId_PropertyOwnerId_ValidFrom",
                table: "OwnerMandates",
                columns: new[] { "OrgId", "PropertyOwnerId", "ValidFrom" },
                unique: true,
                filter: "\"PropertyOwnershipId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OwnerMandates_CommissionPercent",
                table: "OwnerMandates",
                sql: "\"CommissionPercent\" >= 0 AND \"CommissionPercent\" <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OwnerMandates_FlatRates",
                table: "OwnerMandates",
                sql: "(\"CleaningFlatRate\" IS NULL OR \"CleaningFlatRate\" >= 0) AND (\"LinenFlatRate\" IS NULL OR \"LinenFlatRate\" >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OwnerMandates_Validity",
                table: "OwnerMandates",
                sql: "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");

            migrationBuilder.CreateIndex(
                name: "UIX_OrgChannelCommissionRules_OrgId_Channel_ValidFrom",
                table: "OrgChannelCommissionRules",
                columns: new[] { "OrgId", "Channel", "ValidFrom" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OwnerMandates_PropertyOwners_PropertyOwnerId",
                table: "OwnerMandates",
                column: "PropertyOwnerId",
                principalTable: "PropertyOwners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OwnerMandates_PropertyOwners_PropertyOwnerId",
                table: "OwnerMandates");

            migrationBuilder.DropTable(
                name: "OrgChannelCommissionRules");

            migrationBuilder.DropIndex(
                name: "IX_OwnerMandates_PropertyOwnerId",
                table: "OwnerMandates");

            migrationBuilder.DropIndex(
                name: "UIX_OwnerMandates_OrgId_PropertyOwnerId_ValidFrom",
                table: "OwnerMandates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OwnerMandates_CommissionPercent",
                table: "OwnerMandates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OwnerMandates_FlatRates",
                table: "OwnerMandates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OwnerMandates_Validity",
                table: "OwnerMandates");

            // The schema of before has no mandate of an owner (only of one ownership): those rows cannot be kept. The others get back their instants (the
            // first moment of the first day and the last moment of the last day, in Rome).
            migrationBuilder.Sql("""DELETE FROM "OwnerMandates" WHERE "PropertyOwnershipId" IS NULL;""");

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveUntil",
                table: "OwnerMandates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "OwnerMandates"
                SET "CreatedAt" = ("ValidFrom"::timestamp AT TIME ZONE 'Europe/Rome'),
                    "EffectiveUntil" = CASE
                        WHEN "ValidTo" IS NULL THEN NULL
                        ELSE (("ValidTo" + 1)::timestamp AT TIME ZONE 'Europe/Rome') - INTERVAL '1 microsecond'
                    END;
                """);

            migrationBuilder.DropColumn(
                name: "CommissionBase",
                table: "OwnerMandates");

            migrationBuilder.DropColumn(
                name: "CommissionIncludesVat",
                table: "OwnerMandates");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "OwnerMandates");

            migrationBuilder.DropColumn(
                name: "PropertyOwnerId",
                table: "OwnerMandates");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "OwnerMandates");

            migrationBuilder.DropColumn(
                name: "ValidTo",
                table: "OwnerMandates");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "OwnerMandates",
                newName: "EffectiveFrom");

            // An encrypted address cannot be decrypted in SQL and does not fit the columns of before: it is cleared (the owner import or the agency enters it
            // again). The values still in clear are kept.
            migrationBuilder.Sql(
                """
                UPDATE "PropertyOwners"
                SET "BillingStreet" = CASE WHEN "BillingStreet" LIKE 'CfDJ8%' THEN NULL ELSE "BillingStreet" END,
                    "BillingCivic" = CASE WHEN "BillingCivic" LIKE 'CfDJ8%' THEN NULL ELSE "BillingCivic" END,
                    "BillingPostalCode" = CASE WHEN "BillingPostalCode" LIKE 'CfDJ8%' THEN NULL ELSE "BillingPostalCode" END,
                    "BillingCity" = CASE WHEN "BillingCity" LIKE 'CfDJ8%' THEN NULL ELSE "BillingCity" END,
                    "BillingProvince" = CASE WHEN "BillingProvince" LIKE 'CfDJ8%' THEN NULL ELSE "BillingProvince" END,
                    "BillingCountry" = CASE WHEN "BillingCountry" LIKE 'CfDJ8%' THEN NULL ELSE "BillingCountry" END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "BillingStreet",
                table: "PropertyOwners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingProvince",
                table: "PropertyOwners",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingPostalCode",
                table: "PropertyOwners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingCountry",
                table: "PropertyOwners",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingCivic",
                table: "PropertyOwners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BillingCity",
                table: "PropertyOwners",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyOwnershipId",
                table: "OwnerMandates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerMandates_OrgId",
                table: "OwnerMandates",
                column: "OrgId");
        }
    }
}
