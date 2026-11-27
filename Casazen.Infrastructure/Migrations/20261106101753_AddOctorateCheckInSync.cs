using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOctorateCheckInSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DocumentExpiresOn",
                table: "StayGuests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DocumentIssuedOn",
                table: "StayGuests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EducationLevel",
                table: "StayGuests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceComuneCode",
                table: "StayGuests",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceComuneName",
                table: "StayGuests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceCountryCode",
                table: "StayGuests",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceCountryName",
                table: "StayGuests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResidenceInItaly",
                table: "StayGuests",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceProvince",
                table: "StayGuests",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxExemptionCode",
                table: "StayGuests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TourismType",
                table: "StayGuests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TravelWay",
                table: "StayGuests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "GuestCheckInSessions",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "GuestCheckInSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NoShowAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OctorateStayStateSyncs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OctorateEffectiveCheckin = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OctorateArrivalTime = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TodoActivityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctorateStayStateSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctorateStayStateSyncs_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OctorateStayStateSyncs_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrgTouristTaxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    IstatCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RatePerPersonPerNight = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxNights = table.Column<int>(type: "integer", nullable: true),
                    MinimumAge = table.Column<int>(type: "integer", nullable: false),
                    SeasonStart = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    SeasonEnd = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EnteredByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgTouristTaxRates", x => x.Id);
                    table.CheckConstraint("CK_OrgTouristTaxRates_MaxNights", "\"MaxNights\" IS NULL OR \"MaxNights\" >= 1");
                    table.CheckConstraint("CK_OrgTouristTaxRates_MinimumAge", "\"MinimumAge\" BETWEEN 0 AND 18");
                    table.CheckConstraint("CK_OrgTouristTaxRates_RatePositive", "\"RatePerPersonPerNight\" > 0");
                    table.CheckConstraint("CK_OrgTouristTaxRates_SeasonPair", "(\"SeasonStart\" IS NULL) = (\"SeasonEnd\" IS NULL)");
                    table.CheckConstraint("CK_OrgTouristTaxRates_SourceNoteRequired", "btrim(\"SourceNote\") <> ''");
                    table.ForeignKey(
                        name: "FK_OrgTouristTaxRates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StayGuestOctorateLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    OctorateGuestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PayloadHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SentAsDraft = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TodoActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayGuestOctorateLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayGuestOctorateLinks_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayGuestOctorateLinks_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TouristTaxChannelRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    IstatCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Collector = table.Column<int>(type: "integer", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TouristTaxChannelRules", x => x.Id);
                    table.CheckConstraint("CK_TouristTaxChannelRules_IstatCodeDigits", "\"IstatCode\" ~ '^[0-9]{6}$'");
                    table.CheckConstraint("CK_TouristTaxChannelRules_SourceUrlRequired", "btrim(\"SourceUrl\") <> ''");
                });

            migrationBuilder.CreateTable(
                name: "TouristTaxExemptionReasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LabelKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsAutomatic = table.Column<bool>(type: "boolean", nullable: false),
                    OctorateExemptionCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TouristTaxExemptionReasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StayTouristTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    TaxableNights = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountDue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Collector = table.Column<int>(type: "integer", nullable: true),
                    ChannelRuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CalculationHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DeclaredCollector = table.Column<int>(type: "integer", nullable: true),
                    DeclaredByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DeclaredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetainUntil = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayTouristTaxes", x => x.Id);
                    table.CheckConstraint("CK_StayTouristTaxes_AmountDueWithinTotal", "\"AmountDue\" >= 0 AND \"AmountDue\" <= \"Total\"");
                    table.CheckConstraint("CK_StayTouristTaxes_ConfirmedHasHash", "\"ConfirmedAt\" IS NULL OR (\"CalculationHash\" IS NOT NULL AND \"ConfirmedByUserId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_StayTouristTaxes_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayTouristTaxes_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayTouristTaxes_TouristTaxChannelRules_ChannelRuleId",
                        column: x => x.ChannelRuleId,
                        principalTable: "TouristTaxChannelRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StayTouristTaxGuests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StayTouristTaxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ExemptionKind = table.Column<int>(type: "integer", nullable: false),
                    ExemptionCode = table.Column<string>(type: "text", nullable: true),
                    TaxableNights = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayTouristTaxGuests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayTouristTaxGuests_StayTouristTaxes_StayTouristTaxId",
                        column: x => x.StayTouristTaxId,
                        principalTable: "StayTouristTaxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "TouristTaxExemptionReasons",
                columns: new[] { "Id", "Code", "IsActive", "IsAutomatic", "LabelKey", "OctorateExemptionCode", "SortOrder", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9001"), "minor_age", true, true, "touristTax.exemption.minor_age", null, 0, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9002"), "resident", true, false, "touristTax.exemption.resident", null, 10, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9003"), "health_care", true, false, "touristTax.exemption.health_care", null, 20, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9004"), "disability_companion", true, false, "touristTax.exemption.disability_companion", null, 30, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9005"), "public_service", true, false, "touristTax.exemption.public_service", null, 40, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9006"), "host_family", true, false, "touristTax.exemption.host_family", null, 50, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { new Guid("3f0e8a51-6c1d-4f6a-9a0b-1e5d2c7a9007"), "other", true, false, "touristTax.exemption.other", null, 60, new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_OctorateStayStateSyncs_OrgId_Status",
                table: "OctorateStayStateSyncs",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UIX_OctorateStayStateSyncs_BookingId_State",
                table: "OctorateStayStateSyncs",
                columns: new[] { "BookingId", "State" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrgTouristTaxRates_OrgId_City",
                table: "OrgTouristTaxRates",
                columns: new[] { "OrgId", "City" });

            migrationBuilder.CreateIndex(
                name: "IX_OrgTouristTaxRates_OrgId_IstatCode",
                table: "OrgTouristTaxRates",
                columns: new[] { "OrgId", "IstatCode" });

            migrationBuilder.CreateIndex(
                name: "IX_StayGuestOctorateLinks_OrgId_Status",
                table: "StayGuestOctorateLinks",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UIX_StayGuestOctorateLinks_BookingId_Position",
                table: "StayGuestOctorateLinks",
                columns: new[] { "BookingId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayTouristTaxes_ChannelRuleId",
                table: "StayTouristTaxes",
                column: "ChannelRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_StayTouristTaxes_OrgId",
                table: "StayTouristTaxes",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_StayTouristTaxes_BookingId",
                table: "StayTouristTaxes",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_StayTouristTaxGuests_StayTouristTaxId_Position",
                table: "StayTouristTaxGuests",
                columns: new[] { "StayTouristTaxId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_TouristTaxChannelRules_Channel_IstatCode",
                table: "TouristTaxChannelRules",
                columns: new[] { "Channel", "IstatCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_TouristTaxExemptionReasons_Code",
                table: "TouristTaxExemptionReasons",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OctorateStayStateSyncs");

            migrationBuilder.DropTable(
                name: "OrgTouristTaxRates");

            migrationBuilder.DropTable(
                name: "StayGuestOctorateLinks");

            migrationBuilder.DropTable(
                name: "StayTouristTaxGuests");

            migrationBuilder.DropTable(
                name: "TouristTaxExemptionReasons");

            migrationBuilder.DropTable(
                name: "StayTouristTaxes");

            migrationBuilder.DropTable(
                name: "TouristTaxChannelRules");

            migrationBuilder.DropColumn(
                name: "DocumentExpiresOn",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "DocumentIssuedOn",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "EducationLevel",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceComuneCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceComuneName",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceCountryCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceCountryName",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceInItaly",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "ResidenceProvince",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "TaxExemptionCode",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "TourismType",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "TravelWay",
                table: "StayGuests");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "GuestCheckInSessions");

            migrationBuilder.DropColumn(
                name: "NoShowAt",
                table: "Bookings");
        }
    }
}
