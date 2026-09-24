using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CO-14 (A5-30): the encrypted columns become <c>text</c> (a Data Protection payload is longer than the value):
    /// guest document number and place of issue (<c>Guests</c>, <c>StayGuests</c>) and the Alloggiati Web credentials.
    /// The credentials get their tenant (<c>OrgId</c>, from the property) and the "configured on" date; the password
    /// column is renamed, not dropped. Values already stored in clear are encrypted by the application at startup
    /// (<c>EncryptedColumns.EncryptLegacyPlaintextAsync</c>): SQL has no key.
    /// </summary>
    public partial class EncryptGuestDocumentAndQuesturaCredentials : Migration
    {
        /// <summary>Tenant and date of the credentials rows written before CO-14 (there was no writer: normally none).</summary>
        public const string BackfillQuesturaCredentialsSql = """
            UPDATE "PropertyQuesturaCredentials" AS c
            SET "OrgId" = p."OrgId", "UpdatedAt" = c."CreatedAt"
            FROM "Properties" AS p
            WHERE p."Id" = c."PropertyId";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PasswordEncrypted",
                table: "PropertyQuesturaCredentials",
                newName: "Password");

            migrationBuilder.AlterColumn<string>(
                name: "Password",
                table: "PropertyQuesturaCredentials",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentNumber",
                table: "StayGuests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentIssuePlaceName",
                table: "StayGuests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "WsKey",
                table: "PropertyQuesturaCredentials",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "PropertyQuesturaCredentials",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "PropertyQuesturaCredentials",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "PropertyQuesturaCredentials",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql(BackfillQuesturaCredentialsSql);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentNumber",
                table: "Guests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentIssuingCountry",
                table: "Guests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyQuesturaCredentials_OrgId",
                table: "PropertyQuesturaCredentials",
                column: "OrgId");

            migrationBuilder.AddForeignKey(
                name: "FK_PropertyQuesturaCredentials_Orgs_OrgId",
                table: "PropertyQuesturaCredentials",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PropertyQuesturaCredentials_Orgs_OrgId",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropIndex(
                name: "IX_PropertyQuesturaCredentials_OrgId",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "PropertyQuesturaCredentials");

            migrationBuilder.AlterColumn<string>(
                name: "Password",
                table: "PropertyQuesturaCredentials",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.RenameColumn(
                name: "Password",
                table: "PropertyQuesturaCredentials",
                newName: "PasswordEncrypted");

            migrationBuilder.AlterColumn<string>(
                name: "DocumentNumber",
                table: "StayGuests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "DocumentIssuePlaceName",
                table: "StayGuests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "WsKey",
                table: "PropertyQuesturaCredentials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "PropertyQuesturaCredentials",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "DocumentNumber",
                table: "Guests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "DocumentIssuingCountry",
                table: "Guests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
