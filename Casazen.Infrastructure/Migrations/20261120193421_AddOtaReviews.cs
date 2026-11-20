using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOtaReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OtaReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    OctorateRefer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GuestReviewPublished = table.Column<bool>(type: "boolean", nullable: false),
                    GuestReviewPublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GuestRating = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    GuestCategoryRatingsJson = table.Column<string>(type: "jsonb", nullable: true),
                    GuestReviewTextEncrypted = table.Column<string>(type: "text", nullable: true),
                    HostReplyTextEncrypted = table.Column<string>(type: "text", nullable: true),
                    HostReplyIdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HostReplyState = table.Column<int>(type: "integer", nullable: false),
                    HostReplyRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostReplySentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostReplyError = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HostReviewWindow = table.Column<int>(type: "integer", nullable: false),
                    HostReviewState = table.Column<int>(type: "integer", nullable: false),
                    HostReviewRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostReviewSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostReviewJsonEncrypted = table.Column<string>(type: "text", nullable: true),
                    HostReviewIdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HostReviewError = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncError = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NextPollAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PollCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtaReviews", x => x.Id);
                    table.CheckConstraint("CK_OtaReviews_Channel", "\"Channel\" IN (1, 2, 3, 4)");
                    table.CheckConstraint("CK_OtaReviews_GuestRating", "\"GuestRating\" IS NULL OR \"GuestRating\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_OtaReviews_HostReplyState", "\"HostReplyState\" IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_OtaReviews_HostReviewState", "\"HostReviewState\" IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_OtaReviews_HostReviewWindow", "\"HostReviewWindow\" IN (0, 1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_OtaReviews_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OtaReviews_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OtaReviews_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OtaReviews_BookingId",
                table: "OtaReviews",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_OtaReviews_NextPollAt",
                table: "OtaReviews",
                column: "NextPollAt",
                filter: "\"NextPollAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OtaReviews_OrgId_PropertyId_GuestReviewPublishedAt",
                table: "OtaReviews",
                columns: new[] { "OrgId", "PropertyId", "GuestReviewPublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OtaReviews_PropertyId",
                table: "OtaReviews",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OtaReviews_OrgId_BookingId_Channel",
                table: "OtaReviews",
                columns: new[] { "OrgId", "BookingId", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OtaReviews");
        }
    }
}
