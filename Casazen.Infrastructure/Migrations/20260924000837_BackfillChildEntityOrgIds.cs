using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillChildEntityOrgIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TN-2 data migration (step 2 of 3, between AddChildEntityOrgIdNullable and
            // MakeChildEntityOrgIdsRequired). Child rows that controllers expose get the OrgId of their
            // parent, so the global tenant query filter can scope them like the parent:
            //   PropertyDocuments, OtaIntegrations, PricingAdapterConfigs, PricingHistories -> Properties
            //   AlloggiatiWebReports, GuestCheckInSessions -> Bookings
            // GuestCheckInSessions already had an OrgId (set from the booking on creation, no FK): rows
            // whose OrgId differs from their booking's are realigned before the FK is added.
            // Idempotent: only rows whose OrgId differs from the parent's are updated. The parents'
            // OrgId is NOT NULL (MakeOrgIdRequired) and every child has a parent (required FKs), so no
            // row can stay NULL. Counts are logged with RAISE NOTICE.
            // Runbook: docs/runbooks/tenant-child-orgid-migration.md.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    v_documents        bigint;
    v_ota_integrations bigint;
    v_pricing_configs  bigint;
    v_pricing_history  bigint;
    v_reports          bigint;
    v_sessions         bigint;
BEGIN
    UPDATE ""PropertyDocuments"" d
    SET ""OrgId"" = p.""OrgId""
    FROM ""Properties"" p
    WHERE p.""Id"" = d.""PropertyId"" AND d.""OrgId"" IS DISTINCT FROM p.""OrgId"";
    GET DIAGNOSTICS v_documents = ROW_COUNT;

    UPDATE ""OtaIntegrations"" o
    SET ""OrgId"" = p.""OrgId""
    FROM ""Properties"" p
    WHERE p.""Id"" = o.""PropertyId"" AND o.""OrgId"" IS DISTINCT FROM p.""OrgId"";
    GET DIAGNOSTICS v_ota_integrations = ROW_COUNT;

    UPDATE ""PricingAdapterConfigs"" c
    SET ""OrgId"" = p.""OrgId""
    FROM ""Properties"" p
    WHERE p.""Id"" = c.""PropertyId"" AND c.""OrgId"" IS DISTINCT FROM p.""OrgId"";
    GET DIAGNOSTICS v_pricing_configs = ROW_COUNT;

    UPDATE ""PricingHistories"" h
    SET ""OrgId"" = p.""OrgId""
    FROM ""Properties"" p
    WHERE p.""Id"" = h.""PropertyId"" AND h.""OrgId"" IS DISTINCT FROM p.""OrgId"";
    GET DIAGNOSTICS v_pricing_history = ROW_COUNT;

    UPDATE ""AlloggiatiWebReports"" r
    SET ""OrgId"" = b.""OrgId""
    FROM ""Bookings"" b
    WHERE b.""Id"" = r.""BookingId"" AND r.""OrgId"" IS DISTINCT FROM b.""OrgId"";
    GET DIAGNOSTICS v_reports = ROW_COUNT;

    UPDATE ""GuestCheckInSessions"" s
    SET ""OrgId"" = b.""OrgId""
    FROM ""Bookings"" b
    WHERE b.""Id"" = s.""BookingId"" AND s.""OrgId"" IS DISTINCT FROM b.""OrgId"";
    GET DIAGNOSTICS v_sessions = ROW_COUNT;

    RAISE NOTICE 'BackfillChildEntityOrgIds: property_documents=%, ota_integrations=%, pricing_adapter_configs=%, pricing_histories=%, alloggiati_web_reports=%, guest_checkin_sessions_realigned=%',
        v_documents, v_ota_integrations, v_pricing_configs, v_pricing_history, v_reports, v_sessions;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only step: AddChildEntityOrgIdNullable.Down drops the backfilled columns. The realigned
            // GuestCheckInSessions.OrgId values stay, they already matched what the code writes.
        }
    }
}
