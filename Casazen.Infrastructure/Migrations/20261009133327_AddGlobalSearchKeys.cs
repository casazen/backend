using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// UI-13a, global search: a stored generated column <c>SearchKey</c> (the folded words of the main texts of a row) with a GIN
    /// full-text index on five tables, and the index of the beginning of the booking code. No extension; adding a stored generated
    /// column rewrites its table, so every row that exists gets its key. Reverting drops the indexes and the columns and keeps the rows.
    /// Runbook: <c>docs/runbooks/global-search.md</c> (section 6).
    /// </summary>
    public partial class AddGlobalSearchKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "SupplierProfiles",
                type: "text",
                nullable: true,
                computedColumnSql: "btrim(regexp_replace(lower(replace(replace(replace(replace(replace(replace(translate(\"LegalName\", U&'\\00C0\\00C1\\00C2\\00C3\\00C4\\00C5\\00E0\\00E1\\00E2\\00E3\\00E4\\00E5\\0100\\0101\\0102\\0103\\0104\\0105\\01CD\\01CE\\01FA\\01FB\\212B\\00C7\\00E7\\0106\\0107\\0108\\0109\\010A\\010B\\010C\\010D\\00D0\\00F0\\010E\\010F\\0110\\0111\\00C8\\00C9\\00CA\\00CB\\00E8\\00E9\\00EA\\00EB\\0112\\0113\\0114\\0115\\0116\\0117\\0118\\0119\\011A\\011B\\011C\\011D\\011E\\011F\\0120\\0121\\0122\\0123\\0124\\0125\\0126\\0127\\00CC\\00CD\\00CE\\00CF\\00EC\\00ED\\00EE\\00EF\\0128\\0129\\012A\\012B\\012C\\012D\\012E\\012F\\0130\\0131\\01CF\\01D0\\0134\\0135\\0136\\0137\\0138\\212A\\0139\\013A\\013B\\013C\\013D\\013E\\013F\\0140\\0141\\0142\\00D1\\00F1\\0143\\0144\\0145\\0146\\0147\\0148\\0149\\014A\\014B\\00D2\\00D3\\00D4\\00D5\\00D6\\00D8\\00F2\\00F3\\00F4\\00F5\\00F6\\00F8\\014C\\014D\\014E\\014F\\0150\\0151\\01D1\\01D2\\01FE\\01FF\\0154\\0155\\0156\\0157\\0158\\0159\\015A\\015B\\015C\\015D\\015E\\015F\\0160\\0161\\017F\\0218\\0219\\0162\\0163\\0164\\0165\\0166\\0167\\021A\\021B\\00D9\\00DA\\00DB\\00DC\\00F9\\00FA\\00FB\\00FC\\0168\\0169\\016A\\016B\\016C\\016D\\016E\\016F\\0170\\0171\\0172\\0173\\01D3\\01D4\\0174\\0175\\00DD\\00FD\\00FF\\0176\\0177\\0178\\0179\\017A\\017B\\017C\\017D\\017E', 'aaaaaaaaaaaaaaaaaaaaaaaccccccccccddddddeeeeeeeeeeeeeeeeeegggggggghhhhiiiiiiiiiiiiiiiiiiiijjkkkkllllllllllnnnnnnnnnnnoooooooooooooooooooooorrrrrrsssssssssssttttttttuuuuuuuuuuuuuuuuuuuuuuwwyyyyyyzzzzzz'), U&'\\00DF', 'ss'), U&'\\1E9E', 'ss'), U&'\\00C6', 'ae'), U&'\\00E6', 'ae'), U&'\\0152', 'oe'), U&'\\0153', 'oe')), '[^a-z0-9]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "ServiceRequests",
                type: "text",
                nullable: true,
                computedColumnSql: "btrim(regexp_replace(lower(replace(replace(replace(replace(replace(replace(translate(coalesce(\"ServiceNameSnapshot\", '') || ' ' || \"Category\" || ' ' || coalesce(\"PublicCode\", '') || ' ' || coalesce(\"LocationCity\", ''), U&'\\00C0\\00C1\\00C2\\00C3\\00C4\\00C5\\00E0\\00E1\\00E2\\00E3\\00E4\\00E5\\0100\\0101\\0102\\0103\\0104\\0105\\01CD\\01CE\\01FA\\01FB\\212B\\00C7\\00E7\\0106\\0107\\0108\\0109\\010A\\010B\\010C\\010D\\00D0\\00F0\\010E\\010F\\0110\\0111\\00C8\\00C9\\00CA\\00CB\\00E8\\00E9\\00EA\\00EB\\0112\\0113\\0114\\0115\\0116\\0117\\0118\\0119\\011A\\011B\\011C\\011D\\011E\\011F\\0120\\0121\\0122\\0123\\0124\\0125\\0126\\0127\\00CC\\00CD\\00CE\\00CF\\00EC\\00ED\\00EE\\00EF\\0128\\0129\\012A\\012B\\012C\\012D\\012E\\012F\\0130\\0131\\01CF\\01D0\\0134\\0135\\0136\\0137\\0138\\212A\\0139\\013A\\013B\\013C\\013D\\013E\\013F\\0140\\0141\\0142\\00D1\\00F1\\0143\\0144\\0145\\0146\\0147\\0148\\0149\\014A\\014B\\00D2\\00D3\\00D4\\00D5\\00D6\\00D8\\00F2\\00F3\\00F4\\00F5\\00F6\\00F8\\014C\\014D\\014E\\014F\\0150\\0151\\01D1\\01D2\\01FE\\01FF\\0154\\0155\\0156\\0157\\0158\\0159\\015A\\015B\\015C\\015D\\015E\\015F\\0160\\0161\\017F\\0218\\0219\\0162\\0163\\0164\\0165\\0166\\0167\\021A\\021B\\00D9\\00DA\\00DB\\00DC\\00F9\\00FA\\00FB\\00FC\\0168\\0169\\016A\\016B\\016C\\016D\\016E\\016F\\0170\\0171\\0172\\0173\\01D3\\01D4\\0174\\0175\\00DD\\00FD\\00FF\\0176\\0177\\0178\\0179\\017A\\017B\\017C\\017D\\017E', 'aaaaaaaaaaaaaaaaaaaaaaaccccccccccddddddeeeeeeeeeeeeeeeeeegggggggghhhhiiiiiiiiiiiiiiiiiiiijjkkkkllllllllllnnnnnnnnnnnoooooooooooooooooooooorrrrrrsssssssssssttttttttuuuuuuuuuuuuuuuuuuuuuuwwyyyyyyzzzzzz'), U&'\\00DF', 'ss'), U&'\\1E9E', 'ss'), U&'\\00C6', 'ae'), U&'\\00E6', 'ae'), U&'\\0152', 'oe'), U&'\\0153', 'oe')), '[^a-z0-9]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Properties",
                type: "text",
                nullable: true,
                computedColumnSql: "btrim(regexp_replace(lower(replace(replace(replace(replace(replace(replace(translate(\"Name\" || ' ' || \"City\" || ' ' || coalesce(\"CinCode\", ''), U&'\\00C0\\00C1\\00C2\\00C3\\00C4\\00C5\\00E0\\00E1\\00E2\\00E3\\00E4\\00E5\\0100\\0101\\0102\\0103\\0104\\0105\\01CD\\01CE\\01FA\\01FB\\212B\\00C7\\00E7\\0106\\0107\\0108\\0109\\010A\\010B\\010C\\010D\\00D0\\00F0\\010E\\010F\\0110\\0111\\00C8\\00C9\\00CA\\00CB\\00E8\\00E9\\00EA\\00EB\\0112\\0113\\0114\\0115\\0116\\0117\\0118\\0119\\011A\\011B\\011C\\011D\\011E\\011F\\0120\\0121\\0122\\0123\\0124\\0125\\0126\\0127\\00CC\\00CD\\00CE\\00CF\\00EC\\00ED\\00EE\\00EF\\0128\\0129\\012A\\012B\\012C\\012D\\012E\\012F\\0130\\0131\\01CF\\01D0\\0134\\0135\\0136\\0137\\0138\\212A\\0139\\013A\\013B\\013C\\013D\\013E\\013F\\0140\\0141\\0142\\00D1\\00F1\\0143\\0144\\0145\\0146\\0147\\0148\\0149\\014A\\014B\\00D2\\00D3\\00D4\\00D5\\00D6\\00D8\\00F2\\00F3\\00F4\\00F5\\00F6\\00F8\\014C\\014D\\014E\\014F\\0150\\0151\\01D1\\01D2\\01FE\\01FF\\0154\\0155\\0156\\0157\\0158\\0159\\015A\\015B\\015C\\015D\\015E\\015F\\0160\\0161\\017F\\0218\\0219\\0162\\0163\\0164\\0165\\0166\\0167\\021A\\021B\\00D9\\00DA\\00DB\\00DC\\00F9\\00FA\\00FB\\00FC\\0168\\0169\\016A\\016B\\016C\\016D\\016E\\016F\\0170\\0171\\0172\\0173\\01D3\\01D4\\0174\\0175\\00DD\\00FD\\00FF\\0176\\0177\\0178\\0179\\017A\\017B\\017C\\017D\\017E', 'aaaaaaaaaaaaaaaaaaaaaaaccccccccccddddddeeeeeeeeeeeeeeeeeegggggggghhhhiiiiiiiiiiiiiiiiiiiijjkkkkllllllllllnnnnnnnnnnnoooooooooooooooooooooorrrrrrsssssssssssttttttttuuuuuuuuuuuuuuuuuuuuuuwwyyyyyyzzzzzz'), U&'\\00DF', 'ss'), U&'\\1E9E', 'ss'), U&'\\00C6', 'ae'), U&'\\00E6', 'ae'), U&'\\0152', 'oe'), U&'\\0153', 'oe')), '[^a-z0-9]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Parties",
                type: "text",
                nullable: true,
                computedColumnSql: "btrim(regexp_replace(lower(replace(replace(replace(replace(replace(replace(translate(\"LastName\" || ' ' || \"FirstName\", U&'\\00C0\\00C1\\00C2\\00C3\\00C4\\00C5\\00E0\\00E1\\00E2\\00E3\\00E4\\00E5\\0100\\0101\\0102\\0103\\0104\\0105\\01CD\\01CE\\01FA\\01FB\\212B\\00C7\\00E7\\0106\\0107\\0108\\0109\\010A\\010B\\010C\\010D\\00D0\\00F0\\010E\\010F\\0110\\0111\\00C8\\00C9\\00CA\\00CB\\00E8\\00E9\\00EA\\00EB\\0112\\0113\\0114\\0115\\0116\\0117\\0118\\0119\\011A\\011B\\011C\\011D\\011E\\011F\\0120\\0121\\0122\\0123\\0124\\0125\\0126\\0127\\00CC\\00CD\\00CE\\00CF\\00EC\\00ED\\00EE\\00EF\\0128\\0129\\012A\\012B\\012C\\012D\\012E\\012F\\0130\\0131\\01CF\\01D0\\0134\\0135\\0136\\0137\\0138\\212A\\0139\\013A\\013B\\013C\\013D\\013E\\013F\\0140\\0141\\0142\\00D1\\00F1\\0143\\0144\\0145\\0146\\0147\\0148\\0149\\014A\\014B\\00D2\\00D3\\00D4\\00D5\\00D6\\00D8\\00F2\\00F3\\00F4\\00F5\\00F6\\00F8\\014C\\014D\\014E\\014F\\0150\\0151\\01D1\\01D2\\01FE\\01FF\\0154\\0155\\0156\\0157\\0158\\0159\\015A\\015B\\015C\\015D\\015E\\015F\\0160\\0161\\017F\\0218\\0219\\0162\\0163\\0164\\0165\\0166\\0167\\021A\\021B\\00D9\\00DA\\00DB\\00DC\\00F9\\00FA\\00FB\\00FC\\0168\\0169\\016A\\016B\\016C\\016D\\016E\\016F\\0170\\0171\\0172\\0173\\01D3\\01D4\\0174\\0175\\00DD\\00FD\\00FF\\0176\\0177\\0178\\0179\\017A\\017B\\017C\\017D\\017E', 'aaaaaaaaaaaaaaaaaaaaaaaccccccccccddddddeeeeeeeeeeeeeeeeeegggggggghhhhiiiiiiiiiiiiiiiiiiiijjkkkkllllllllllnnnnnnnnnnnoooooooooooooooooooooorrrrrrsssssssssssttttttttuuuuuuuuuuuuuuuuuuuuuuwwyyyyyyzzzzzz'), U&'\\00DF', 'ss'), U&'\\1E9E', 'ss'), U&'\\00C6', 'ae'), U&'\\00E6', 'ae'), U&'\\0152', 'oe'), U&'\\0153', 'oe')), '[^a-z0-9]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Guests",
                type: "text",
                nullable: true,
                computedColumnSql: "btrim(regexp_replace(lower(replace(replace(replace(replace(replace(replace(translate(\"LastName\" || ' ' || \"FirstName\" || ' ' || \"Email\", U&'\\00C0\\00C1\\00C2\\00C3\\00C4\\00C5\\00E0\\00E1\\00E2\\00E3\\00E4\\00E5\\0100\\0101\\0102\\0103\\0104\\0105\\01CD\\01CE\\01FA\\01FB\\212B\\00C7\\00E7\\0106\\0107\\0108\\0109\\010A\\010B\\010C\\010D\\00D0\\00F0\\010E\\010F\\0110\\0111\\00C8\\00C9\\00CA\\00CB\\00E8\\00E9\\00EA\\00EB\\0112\\0113\\0114\\0115\\0116\\0117\\0118\\0119\\011A\\011B\\011C\\011D\\011E\\011F\\0120\\0121\\0122\\0123\\0124\\0125\\0126\\0127\\00CC\\00CD\\00CE\\00CF\\00EC\\00ED\\00EE\\00EF\\0128\\0129\\012A\\012B\\012C\\012D\\012E\\012F\\0130\\0131\\01CF\\01D0\\0134\\0135\\0136\\0137\\0138\\212A\\0139\\013A\\013B\\013C\\013D\\013E\\013F\\0140\\0141\\0142\\00D1\\00F1\\0143\\0144\\0145\\0146\\0147\\0148\\0149\\014A\\014B\\00D2\\00D3\\00D4\\00D5\\00D6\\00D8\\00F2\\00F3\\00F4\\00F5\\00F6\\00F8\\014C\\014D\\014E\\014F\\0150\\0151\\01D1\\01D2\\01FE\\01FF\\0154\\0155\\0156\\0157\\0158\\0159\\015A\\015B\\015C\\015D\\015E\\015F\\0160\\0161\\017F\\0218\\0219\\0162\\0163\\0164\\0165\\0166\\0167\\021A\\021B\\00D9\\00DA\\00DB\\00DC\\00F9\\00FA\\00FB\\00FC\\0168\\0169\\016A\\016B\\016C\\016D\\016E\\016F\\0170\\0171\\0172\\0173\\01D3\\01D4\\0174\\0175\\00DD\\00FD\\00FF\\0176\\0177\\0178\\0179\\017A\\017B\\017C\\017D\\017E', 'aaaaaaaaaaaaaaaaaaaaaaaccccccccccddddddeeeeeeeeeeeeeeeeeegggggggghhhhiiiiiiiiiiiiiiiiiiiijjkkkkllllllllllnnnnnnnnnnnoooooooooooooooooooooorrrrrrsssssssssssttttttttuuuuuuuuuuuuuuuuuuuuuuwwyyyyyyzzzzzz'), U&'\\00DF', 'ss'), U&'\\1E9E', 'ss'), U&'\\00C6', 'ae'), U&'\\00E6', 'ae'), U&'\\0152', 'oe'), U&'\\0153', 'oe')), '[^a-z0-9]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierProfiles_SearchKey_Fts",
                table: "SupplierProfiles",
                column: "SearchKey")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_SearchKey_Fts",
                table: "ServiceRequests",
                column: "SearchKey")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_SearchKey_Fts",
                table: "Properties",
                column: "SearchKey")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_SearchKey_Fts",
                table: "Parties",
                column: "SearchKey")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "IX_Guests_SearchKey_Fts",
                table: "Guests",
                column: "SearchKey")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_BookingCode_Prefix",
                table: "Bookings",
                column: "BookingCode")
                .Annotation("Npgsql:IndexOperators", new[] { "varchar_pattern_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierProfiles_SearchKey_Fts",
                table: "SupplierProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_SearchKey_Fts",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_Properties_SearchKey_Fts",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Parties_SearchKey_Fts",
                table: "Parties");

            migrationBuilder.DropIndex(
                name: "IX_Guests_SearchKey_Fts",
                table: "Guests");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_BookingCode_Prefix",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "SupplierProfiles");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Guests");
        }
    }
}
