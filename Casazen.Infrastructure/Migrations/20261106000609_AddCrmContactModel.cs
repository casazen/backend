using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmContactModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoProfileId",
                table: "GuestCrmProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameBirthKeyHash",
                table: "GuestCrmProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneKeyHash",
                table: "GuestCrmProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryGuestId",
                table: "GuestCrmProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GuestContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestCrmProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    ValueHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    Confirmation = table.Column<int>(type: "integer", nullable: false),
                    ConfirmationSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmationSentCount = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LegalBasis = table.Column<int>(type: "integer", nullable: false),
                    BasisVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BasisRecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsentSource = table.Column<int>(type: "integer", nullable: true),
                    ConsentIpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsSuppressed = table.Column<bool>(type: "boolean", nullable: false),
                    SuppressedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SuppressedSource = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuestContacts_GuestCrmProfiles_GuestCrmProfileId",
                        column: x => x.GuestCrmProfileId,
                        principalTable: "GuestCrmProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuestContacts_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GuestContacts_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GuestMergeCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileAId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileBId = table.Column<Guid>(type: "uuid", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    ReasonsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestMergeCandidates", x => x.Id);
                    table.CheckConstraint("CK_GuestMergeCandidates_DistinctProfiles", "\"ProfileAId\" <> \"ProfileBId\"");
                    table.CheckConstraint("CK_GuestMergeCandidates_Score", "\"Score\" >= 0 AND \"Score\" <= 100");
                    table.ForeignKey(
                        name: "FK_GuestMergeCandidates_GuestCrmProfiles_ProfileAId",
                        column: x => x.ProfileAId,
                        principalTable: "GuestCrmProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuestMergeCandidates_GuestCrmProfiles_ProfileBId",
                        column: x => x.ProfileBId,
                        principalTable: "GuestCrmProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuestMergeCandidates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PropertyWelcomeLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyWelcomeLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyWelcomeLinks_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyWelcomeLinks_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuestCrmProfiles_OrgId_NameBirthKeyHash",
                table: "GuestCrmProfiles",
                columns: new[] { "OrgId", "NameBirthKeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestCrmProfiles_OrgId_PhoneKeyHash",
                table: "GuestCrmProfiles",
                columns: new[] { "OrgId", "PhoneKeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestContacts_GuestCrmProfileId",
                table: "GuestContacts",
                column: "GuestCrmProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestContacts_GuestId",
                table: "GuestContacts",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestContacts_OrgId_GuestCrmProfileId",
                table: "GuestContacts",
                columns: new[] { "OrgId", "GuestCrmProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestContacts_OrgId_IsSuppressed",
                table: "GuestContacts",
                columns: new[] { "OrgId", "IsSuppressed" });

            migrationBuilder.CreateIndex(
                name: "UIX_GuestContacts_OrgId_Channel_ValueHash",
                table: "GuestContacts",
                columns: new[] { "OrgId", "Channel", "ValueHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuestMergeCandidates_ProfileAId",
                table: "GuestMergeCandidates",
                column: "ProfileAId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestMergeCandidates_ProfileBId",
                table: "GuestMergeCandidates",
                column: "ProfileBId");

            migrationBuilder.CreateIndex(
                name: "UIX_GuestMergeCandidates_OrgId_ProfileAId_ProfileBId",
                table: "GuestMergeCandidates",
                columns: new[] { "OrgId", "ProfileAId", "ProfileBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyWelcomeLinks_OrgId",
                table: "PropertyWelcomeLinks",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyWelcomeLinks_PropertyId_Active",
                table: "PropertyWelcomeLinks",
                column: "PropertyId",
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyWelcomeLinks_Token",
                table: "PropertyWelcomeLinks",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuestContacts");

            migrationBuilder.DropTable(
                name: "GuestMergeCandidates");

            migrationBuilder.DropTable(
                name: "PropertyWelcomeLinks");

            migrationBuilder.DropIndex(
                name: "IX_GuestCrmProfiles_OrgId_NameBirthKeyHash",
                table: "GuestCrmProfiles");

            migrationBuilder.DropIndex(
                name: "IX_GuestCrmProfiles_OrgId_PhoneKeyHash",
                table: "GuestCrmProfiles");

            migrationBuilder.DropColumn(
                name: "MergedIntoProfileId",
                table: "GuestCrmProfiles");

            migrationBuilder.DropColumn(
                name: "NameBirthKeyHash",
                table: "GuestCrmProfiles");

            migrationBuilder.DropColumn(
                name: "PhoneKeyHash",
                table: "GuestCrmProfiles");

            migrationBuilder.DropColumn(
                name: "PrimaryGuestId",
                table: "GuestCrmProfiles");
        }
    }
}
