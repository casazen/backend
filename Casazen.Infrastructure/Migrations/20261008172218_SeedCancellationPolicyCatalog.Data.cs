using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Seed data for the CasaZen cancellation policy catalog (PC-02, PO decision 2026-10-08).
    /// Five short-stay policies (≤27 nights): Ampia, Intermedia, Contenuta, Anticipata, Non rimborsabile.
    /// Long-term (≥28 nights) policies are excluded from the catalog.
    /// Grace rule common to all: if booking confirmed ≥7 days before check-in and cancelled within 24 h
    /// of booking confirmation → 100 % refund.
    /// Source: Airbnb public host policy page consulted 2026-10-08 (brand not mentioned in product).
    /// </summary>
    public partial class SeedCancellationPolicyCatalog
    {
        private static void ApplyData(MigrationBuilder migrationBuilder)
        {
            // ── Ampia (Flexible) ──────────────────────────────────────────────────────
            // Full refund until 24 h before check-in; after that host keeps nights stayed + 1 night.
            migrationBuilder.InsertData(
                table: "CancellationPolicies",
                columns: ["Id", "Slug", "Name", "Description",
                          "FullRefundHours", "PartialRefundPercent", "PartialRefundHours",
                          "GraceBookingDaysBeforeCheckin", "GraceWindowHours",
                          "CreatedAt", "UpdatedAt"],
                values: [
                    new object[] {
                        "a1000000-0000-0000-0000-000000000001",
                        "ampia",
                        "Ampia",
                        "Rimborso completo fino a 24 ore prima del check-in. Dopo tale termine l'host trattiene le notti già trascorse più 1 notte aggiuntiva (PC-02).",
                        24,   // FullRefundHours
                        0m,   // PartialRefundPercent (no partial tier)
                        0,    // PartialRefundHours
                        7,    // GraceBookingDaysBeforeCheckin
                        24,   // GraceWindowHours
                        "2026-10-08T00:00:00Z",
                        "2026-10-08T00:00:00Z"
                    }
                ]);

            // ── Intermedia (Moderate) ─────────────────────────────────────────────────
            // Full refund until 5 days (120 h) before check-in; after: host keeps 1 night + 50 % remaining.
            migrationBuilder.InsertData(
                table: "CancellationPolicies",
                columns: ["Id", "Slug", "Name", "Description",
                          "FullRefundHours", "PartialRefundPercent", "PartialRefundHours",
                          "GraceBookingDaysBeforeCheckin", "GraceWindowHours",
                          "CreatedAt", "UpdatedAt"],
                values: [
                    new object[] {
                        "a1000000-0000-0000-0000-000000000002",
                        "intermedia",
                        "Intermedia",
                        "Rimborso completo fino a 5 giorni prima del check-in. Dopo: l'host trattiene 1 notte più il 50% delle notti non godute (PC-02).",
                        120,  // FullRefundHours = 5 days × 24
                        50m,  // PartialRefundPercent (50 % of remaining nights – applied by application logic)
                        0,    // PartialRefundHours (partial applies until check-in)
                        7,
                        24,
                        "2026-10-08T00:00:00Z",
                        "2026-10-08T00:00:00Z"
                    }
                ]);

            // ── Contenuta (Limited) ───────────────────────────────────────────────────
            // Full until 14 days; 50 % between 7–14 days; 0 % within 7 days or after check-in.
            migrationBuilder.InsertData(
                table: "CancellationPolicies",
                columns: ["Id", "Slug", "Name", "Description",
                          "FullRefundHours", "PartialRefundPercent", "PartialRefundHours",
                          "GraceBookingDaysBeforeCheckin", "GraceWindowHours",
                          "CreatedAt", "UpdatedAt"],
                values: [
                    new object[] {
                        "a1000000-0000-0000-0000-000000000003",
                        "contenuta",
                        "Contenuta",
                        "Rimborso completo fino a 14 giorni prima; 50% tra 7 e 14 giorni prima; nessun rimborso entro 7 giorni o dopo il check-in (PC-02).",
                        336,  // FullRefundHours = 14 days × 24
                        50m,  // PartialRefundPercent
                        168,  // PartialRefundHours = 7 days × 24
                        7,
                        24,
                        "2026-10-08T00:00:00Z",
                        "2026-10-08T00:00:00Z"
                    }
                ]);

            // ── Anticipata (Firm) ─────────────────────────────────────────────────────
            // Full until 30 days; 50 % between 7–30 days; 0 % within 7 days or after check-in.
            migrationBuilder.InsertData(
                table: "CancellationPolicies",
                columns: ["Id", "Slug", "Name", "Description",
                          "FullRefundHours", "PartialRefundPercent", "PartialRefundHours",
                          "GraceBookingDaysBeforeCheckin", "GraceWindowHours",
                          "CreatedAt", "UpdatedAt"],
                values: [
                    new object[] {
                        "a1000000-0000-0000-0000-000000000004",
                        "anticipata",
                        "Anticipata",
                        "Rimborso completo fino a 30 giorni prima; 50% tra 7 e 30 giorni prima; nessun rimborso entro 7 giorni o dopo il check-in (PC-02).",
                        720,  // FullRefundHours = 30 days × 24
                        50m,  // PartialRefundPercent
                        168,  // PartialRefundHours = 7 days × 24
                        7,
                        24,
                        "2026-10-08T00:00:00Z",
                        "2026-10-08T00:00:00Z"
                    }
                ]);

            // ── Non rimborsabile ──────────────────────────────────────────────────────
            // Only grace window (7 d + 24 h); 0 % afterwards. Discount ~10 % on base price.
            // Max checkout: 60 days from booking. Bookable until: Ampia 1 d, Intermedia 5 d,
            // Contenuta/Anticipata 14 d before check-in.
            migrationBuilder.InsertData(
                table: "CancellationPolicies",
                columns: ["Id", "Slug", "Name", "Description",
                          "FullRefundHours", "PartialRefundPercent", "PartialRefundHours",
                          "GraceBookingDaysBeforeCheckin", "GraceWindowHours",
                          "CreatedAt", "UpdatedAt"],
                values: [
                    new object[] {
                        "a1000000-0000-0000-0000-000000000005",
                        "non_rimborsabile",
                        "Non rimborsabile",
                        "Nessun rimborso oltre la finestra di grazia (≥7 giorni prima, entro 24 h dalla conferma). Sconto base ~10% sul prezzo. Solo se il checkout è entro 60 giorni (PC-02).",
                        0,    // FullRefundHours (grace is handled separately by GraceBookingDaysBeforeCheckin/GraceWindowHours)
                        0m,   // PartialRefundPercent
                        0,    // PartialRefundHours
                        7,
                        24,
                        "2026-10-08T00:00:00Z",
                        "2026-10-08T00:00:00Z"
                    }
                ]);
        }

        private static void RevertData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "CancellationPolicies",
                keyColumn: "Id",
                keyValues: [
                    "a1000000-0000-0000-0000-000000000001",
                    "a1000000-0000-0000-0000-000000000002",
                    "a1000000-0000-0000-0000-000000000003",
                    "a1000000-0000-0000-0000-000000000004",
                    "a1000000-0000-0000-0000-000000000005"
                ]);
        }
    }
}
