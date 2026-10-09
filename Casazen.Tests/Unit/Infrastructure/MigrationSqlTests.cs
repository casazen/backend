using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AC3/AC4/AC5/AC10b — migration correctness. Generates the REAL PostgreSQL migration script
/// from the Npgsql provider (no database connection required) and asserts the shipped SQL has
/// the safety properties the design mandates: nullable add → idempotent relationship-walk
/// backfill → pre-flight NULL guard before the NOT-NULL flip + restricted FKs, with a tested
/// down-migration. This is the Docker-free equivalent of running the migration end-to-end.
/// </summary>
public class MigrationSqlTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

    private static (string addNullable, string backfill, string makeRequired) MigrationIds(AppDbContext db)
    {
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        return (
            keys.Single(k => k.EndsWith("AddOrgIdNullable", StringComparison.Ordinal)),
            keys.Single(k => k.EndsWith("BackfillDefaultOrgs", StringComparison.Ordinal)),
            keys.Single(k => k.EndsWith("MakeOrgIdRequired", StringComparison.Ordinal)));
    }

    [Fact]
    public void Migrations_RecentOnes_LandInOrder()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        // Relative order only: later tasks append their own migrations after these.
        var ordered = new[]
        {
            "AddStrFiscalRegime2026", "AddTerritorialRentAgreements", "AddLeaseRegistrationAuthorization",
            "AddLongRentPropertyPermissions", "AddGuestOrgIdNullable", "BackfillGuestOrgIds", "MakeGuestOrgIdRequired",
            "AddDataProtectionKeys",
            "NormalizeCinCodes",
            "AddChildEntityOrgIdNullable", "BackfillChildEntityOrgIds", "MakeChildEntityOrgIdsRequired",
        }.Select(name => keys.FindIndex(k => k.EndsWith(name, StringComparison.Ordinal))).ToList();
        Assert.All(ordered, index => Assert.True(index >= 0));
        Assert.Equal(ordered.Order(), ordered);
        Assert.Contains(keys, k => k.EndsWith("AddLongRentPropertyPermissions", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddLeaseRegistrationAuthorization", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("RestrictCustomDomainUniquenessToVerified", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("NormalizeOrgPublicHostState", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddDeviceRegistrations", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddPropertySlug", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddPropertyComplianceStatus", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddGuestCheckInSession", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddCalendarBlocksAndICalFeeds", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("AddServiceRequest", StringComparison.Ordinal));
        // SP-04: after the agenda of SP-03 (the schedule of a request points at the catalog of SP-02).
        Assert.True(
            keys.FindIndex(k => k.EndsWith("AddServiceRequestSchedule", StringComparison.Ordinal))
            > keys.FindIndex(k => k.EndsWith("AddSupplierAgenda", StringComparison.Ordinal)));
        // SP-10: after the schedule of a request (the checked booking becomes a request with hours).
        Assert.True(
            keys.FindIndex(k => k.EndsWith("AddShowcaseBooking", StringComparison.Ordinal))
            > keys.FindIndex(k => k.EndsWith("AddServiceRequestSchedule", StringComparison.Ordinal)));
    }

    [Fact]
    public void AddShowcaseBooking_AddsTheTwoTablesTheColumnsTheChecksAndTheIndexes_WithoutRewritingARowThatExists()
    {
        using var db = NewNpgsqlContext();
        var (previous, up, _) = ScriptsOf(db, "AddShowcaseBooking");

        Assert.False(string.IsNullOrEmpty(previous));
        // A request from a showcase has no property: the column only gets looser, nothing is dropped.
        Assert.Contains("ALTER TABLE \"ServiceRequests\" ALTER COLUMN \"PropertyId\" DROP NOT NULL;", up);
        foreach (var column in new[]
                 {
                     "CustomerId", "LocationAccessNotes", "LocationAddress", "LocationCity", "LocationComuneIstat", "LocationFloor",
                     "LocationPostalCode", "PublicCode", "ReminderSentAt",
                 })
        {
            Assert.Contains($"ALTER TABLE \"ServiceRequests\" ADD \"{column}\" ", up);
        }

        // Every request that exists is a host's: it gets Source = Host (0) and so satisfies the context check without a backfill.
        Assert.Contains("ADD \"Source\" integer NOT NULL DEFAULT 0;", up);
        Assert.Contains("CREATE TABLE \"ServiceCustomers\"", up);
        Assert.Contains("CREATE TABLE \"ShowcaseBookingHolds\"", up);
        // The payload of a hold is encrypted text, and its column says so.
        Assert.Contains("\"PayloadEncrypted\" text,", up);
        Assert.Contains("\"FullName\" text NOT NULL,", up);

        // The customers and the holds are children of the supplier profile; a hold only remembers its request; a customer with a
        // request is never deleted by accident.
        Assert.Contains("FOREIGN KEY (\"OrgId\") REFERENCES \"SupplierProfiles\" (\"OrgId\") ON DELETE CASCADE", up);
        Assert.Contains("FOREIGN KEY (\"ServiceRequestId\") REFERENCES \"ServiceRequests\" (\"Id\") ON DELETE SET NULL", up);
        Assert.Contains("FOREIGN KEY (\"CustomerId\") REFERENCES \"ServiceCustomers\" (\"Id\") ON DELETE RESTRICT", up);

        foreach (var check in new[] { "CK_ShowcaseBookingHolds_Interval", "CK_ShowcaseBookingHolds_Expiry", "CK_ServiceRequests_Context" })
            Assert.Contains($"\"{check}\" CHECK", up);
        Assert.Contains("CHECK (\"StartUtc\" < \"EndUtc\")", up);
        Assert.Contains("CHECK (\"ExpiresAt\" > \"CreatedAt\")", up);
        // The two kinds of request apart: a showcase request has no property, no stay and a customer; a host's has a property
        // and none of the showcase's columns.
        Assert.Contains("(\"RentalContext\" = 2 AND \"PropertyId\" IS NULL AND \"BookingId\" IS NULL AND \"Source\" = 1 AND \"CustomerId\" IS NOT NULL", up);
        Assert.Contains("OR (\"RentalContext\" IN (0, 1) AND \"PropertyId\" IS NOT NULL AND \"Source\" = 0 AND \"CustomerId\" IS NULL", up);

        foreach (var index in new[]
                 {
                     "UIX_ServiceCustomers_OrgId_EmailHash",
                     "UIX_ShowcaseBookingHolds_OrgId_ClientRequestId",
                     "UIX_ShowcaseBookingHolds_OrgId_PublicCode",
                     "UIX_ServiceRequests_SupplierOrgId_PublicCode",
                 })
        {
            Assert.Contains($"CREATE UNIQUE INDEX \"{index}\"", up);
        }

        // The host requests have no code: the unique index of the codes covers only the rows that have one.
        Assert.Contains("(\"SupplierOrgId\", \"PublicCode\") WHERE \"PublicCode\" IS NOT NULL;", up);
        Assert.Contains("CREATE INDEX \"IX_ShowcaseBookingHolds_ExpiresAt\"", up);

        // The rows that exist are not rewritten, deleted or dropped.
        Assert.DoesNotContain("UPDATE ", up);
        Assert.DoesNotContain("DELETE FROM \"", up.Replace("DELETE FROM \"__EFMigrationsHistory\"", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("DROP ", up.Replace("DROP NOT NULL", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("SET NOT NULL", up);
    }

    [Fact]
    public void AddShowcaseBooking_TheRevertDropsWhatItAdded_AndRefusesWhileAShowcaseRequestExists()
    {
        using var db = NewNpgsqlContext();
        var (_, _, down) = ScriptsOf(db, "AddShowcaseBooking");

        Assert.Contains("DROP TABLE \"ServiceCustomers\";", down);
        Assert.Contains("DROP TABLE \"ShowcaseBookingHolds\";", down);
        Assert.Contains("DROP CONSTRAINT \"CK_ServiceRequests_Context\";", down);
        Assert.Contains("DROP INDEX \"UIX_ServiceRequests_SupplierOrgId_PublicCode\";", down);
        foreach (var column in new[] { "CustomerId", "LocationAddress", "LocationCity", "PublicCode", "ReminderSentAt", "Source" })
            Assert.Contains($"DROP COLUMN \"{column}\";", down);

        // The property is put back to NOT NULL through an update to a property that does not exist: a foreign key violation while
        // a showcase request exists, so the revert (one transaction) is undone and nothing is lost; the default is dropped after.
        Assert.Contains("UPDATE \"ServiceRequests\" SET \"PropertyId\" = '00000000-0000-0000-0000-000000000000' WHERE \"PropertyId\" IS NULL;", down);
        Assert.Contains("ALTER COLUMN \"PropertyId\" SET NOT NULL;", down);
        Assert.Contains("ALTER TABLE \"ServiceRequests\" ALTER COLUMN \"PropertyId\" DROP DEFAULT;", down);
        Assert.StartsWith("START TRANSACTION;", down.TrimStart());
    }

    private static (string Previous, string Up, string Down) ScriptsOf(AppDbContext db, string migrationName)
    {
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith(migrationName, StringComparison.Ordinal));
        Assert.True(index > 0, $"{migrationName} not found");
        var migrator = db.GetService<IMigrator>();
        return (
            keys[index - 1],
            migrator.GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]),
            migrator.GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]));
    }

    [Fact]
    public void AddServiceRequestSchedule_AddsTheColumnsOfTheSpecWithJsonDefaultsTheIndexTheChecksAndTheCatalogForeignKey()
    {
        using var db = NewNpgsqlContext();
        var (previous, script, down) = ScriptsOfAddServiceRequestSchedule(db);

        foreach (var column in new[]
                 {
                     "ScheduledStartUtc", "ScheduledEndUtc", "ServiceListingId", "ServiceNameSnapshot", "EstimatedAmountCents",
                     "QuotedAmountCents", "FinalAmountCents", "ResponseDueAt", "StartedAt", "CancelledAt", "CancelledBy",
                     "CancellationReason", "CompletionNotes", "LastRemindedAt", "ProposedStartUtc", "ProposedEndUtc", "ProposedAt",
                     "ProposedByUserId", "ProposalMessage",
                 })
        {
            Assert.Contains($"ALTER TABLE \"ServiceRequests\" ADD \"{column}\" ", script);
        }

        // The three lists are never null and start empty, so the requests that exist need no backfill.
        foreach (var column in new[] { "OptionsJson", "PriceLinesJson", "WorkPhotosJson" })
            Assert.Contains($"ADD \"{column}\" jsonb NOT NULL DEFAULT '[]';", script);
        Assert.Contains("ADD \"FinalAmountNeedsConfirmation\" boolean NOT NULL DEFAULT FALSE;", script);

        // The inbox reads the requests of a supplier by their time; the catalog is kept when a service is deleted.
        Assert.Contains("CREATE INDEX \"IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc\" ON \"ServiceRequests\" (\"SupplierOrgId\", \"ScheduledStartUtc\");", script);
        Assert.Contains("CREATE INDEX \"IX_ServiceRequests_ServiceListingId\"", script);
        Assert.Contains("FOREIGN KEY (\"ServiceListingId\") REFERENCES \"SupplierServiceListings\" (\"Id\") ON DELETE SET NULL", script);
        foreach (var check in new[] { "CK_ServiceRequests_ScheduledInterval", "CK_ServiceRequests_ProposedInterval", "CK_ServiceRequests_Amounts" })
            Assert.Contains($"ADD CONSTRAINT \"{check}\" CHECK", script);

        Assert.False(string.IsNullOrEmpty(previous));
        // Only the columns of SP-04: the old requests stay "to be agreed", nothing is rewritten, nothing is dropped or made nullable.
        Assert.DoesNotContain("UPDATE ", script);
        Assert.DoesNotContain("DELETE FROM", script);
        Assert.DoesNotContain("DROP ", script);
        Assert.DoesNotContain("DROP NOT NULL", script);
        Assert.DoesNotContain("SET NOT NULL", script);
        Assert.DoesNotContain("ALTER COLUMN", script);
        // The fields of the specs that come after (#466, #467) are not anticipated here.
        Assert.DoesNotContain("\"OpenedBy\"", script);
        Assert.DoesNotContain("\"LeaseContractId\"", script);

        // Down: everything the migration added goes away.
        Assert.Contains("DROP INDEX \"IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc\";", down);
        Assert.Contains("DROP COLUMN \"WorkPhotosJson\";", down);
        Assert.Contains("DROP COLUMN \"ScheduledStartUtc\";", down);
        Assert.Contains("DROP CONSTRAINT \"FK_ServiceRequests_SupplierServiceListings_ServiceListingId\";", down);
    }

    [Fact]
    public void AddServiceRequestSchedule_TheChecksMirrorTheRulesOfTheService()
    {
        using var db = NewNpgsqlContext();
        var (_, script, _) = ScriptsOfAddServiceRequestSchedule(db);

        // A time has a start and an end, in that order; a proposal too, with the moment it was made; amounts are positive and bounded.
        Assert.Matches(
            "CK_ServiceRequests_ScheduledInterval\" CHECK \\(\\(\"ScheduledStartUtc\" IS NULL AND \"ScheduledEndUtc\" IS NULL\\) OR",
            script);
        Assert.Contains("\"ScheduledEndUtc\" > \"ScheduledStartUtc\"", script);
        Assert.Contains("\"ProposedEndUtc\" > \"ProposedStartUtc\"", script);
        Assert.Contains("\"ProposedAt\" IS NOT NULL", script);
        Assert.Contains("\"FinalAmountCents\" BETWEEN 1 AND 10000000", script);
        Assert.Contains("\"QuotedAmountCents\" BETWEEN 1 AND 10000000", script);
        Assert.Contains("\"EstimatedAmountCents\" BETWEEN 1 AND 10000000", script);
    }

    private static (string Previous, string Up, string Down) ScriptsOfAddServiceRequestSchedule(AppDbContext db)
    {
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("AddServiceRequestSchedule", StringComparison.Ordinal));
        Assert.True(index > 0);
        var migrator = db.GetService<IMigrator>();
        return (
            keys[index - 1],
            migrator.GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]),
            migrator.GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]));
    }

    [Fact]
    public void RestrictCustomDomainUniquenessToVerified_ReplacesBroadUniqueIndex()
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: "20260717213540_AddDeviceRegistrations",
            toMigration: "20260730111500_RestrictCustomDomainUniquenessToVerified");

        Assert.Contains("DROP INDEX \"IX_Orgs_CustomDomain\"", script);
        Assert.Contains(
            "WHERE \"CustomDomain\" IS NOT NULL AND \"DomainVerificationStatus\" = 1",
            script);
    }

    [Fact]
    public void NormalizeOrgPublicHostState_CorrectsLegacyModeAndStaleSubdomains()
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: "20260717213540_AddDeviceRegistrations",
            toMigration: "20260725110112_NormalizeOrgPublicHostState");

        Assert.Contains("SET \"PublicHostMode\" = 1", script);
        Assert.Contains("AND \"Subdomain\" IS NULL", script);
        Assert.Contains("AND \"CustomDomain\" IS NULL", script);
        Assert.Contains("SET \"Subdomain\" = NULL", script);
        Assert.Contains("WHERE \"PublicHostMode\" <> 0", script);
    }

    [Fact]
    public void AddLongRentPropertyPermissions_GrantsPropertyReadWriteToLongRentRole()
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: "20260817084000_AddLeaseRegistrationAuthorization",
            toMigration: "20260831111000_AddLongRentPropertyPermissions");

        Assert.Contains("('property.read', 2)", script);
        Assert.Contains("('property.write', 2)", script);
    }

    [Fact]
    public void AddCheckInTokenExpiresAt_ExistsAfterAlloggiatiCheckInMvp()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        Assert.Contains(keys, k => k.EndsWith("AddCheckInTokenExpiresAt", StringComparison.Ordinal));
        var alloggiatiIdx = keys.FindIndex(k => k.EndsWith("AddAlloggiatiCheckInMvp", StringComparison.Ordinal));
        var checkInExpiryIdx = keys.FindIndex(k => k.EndsWith("AddCheckInTokenExpiresAt", StringComparison.Ordinal));
        Assert.True(checkInExpiryIdx > alloggiatiIdx);
    }

    [Fact]
    public void AddCheckInTokenExpiresAt_AddsNullableColumnOnBookings()
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: "20260610171014_AddAlloggiatiCheckInMvp",
            toMigration: "20260611120000_AddCheckInTokenExpiresAt");

        Assert.Contains("ADD \"CheckInTokenExpiresAt\"", script);
        Assert.Contains("timestamp with time zone", script);
        Assert.DoesNotContain("SET NOT NULL", script);
    }

    [Fact]
    public void RemoveLegacyBookingCheckInToken_DropsOnlyTheBookingTokenColumnsAndIndex()
    {
        // CO-16 (A5-29): the token of the removed /api/checkin portal was stored in clear on the booking.
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("RemoveLegacyBookingCheckInToken", StringComparison.Ordinal));
        Assert.True(index > 0);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]);

        Assert.Contains("DROP INDEX \"IX_Bookings_CheckInToken\"", script);
        Assert.Contains("ALTER TABLE \"Bookings\" DROP COLUMN \"CheckInToken\"", script);
        Assert.Contains("ALTER TABLE \"Bookings\" DROP COLUMN \"CheckInTokenExpiresAt\"", script);
        // The supplier job QR token is another feature (D12), not touched here.
        Assert.DoesNotContain("SupplierJobs", script);
    }

    [Fact]
    public void RemoveSupplierJobs_DropsOnlyTheSupplierJobsTable()
    {
        // SU-11 (A4-15, decision D12): the supplier jobs with QR check-in are removed; the service requests stay.
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("RemoveSupplierJobs", StringComparison.Ordinal));
        Assert.True(index > 0);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]);

        Assert.Contains("DROP TABLE \"SupplierJobs\"", script);
        Assert.DoesNotContain("ServiceRequests", script);
        Assert.DoesNotContain("ALTER TABLE", script);
        Assert.Equal(1, script.Split("DROP ", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void AddAlloggiatiCheckInMvp_ExistsAfterConnectFields()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        Assert.Contains(keys, k => k.EndsWith("AddAlloggiatiCheckInMvp", StringComparison.Ordinal));
        var connectIdx = keys.ToList().FindIndex(k => k.EndsWith("AddConnectStatusFields", StringComparison.Ordinal));
        var alloggiatiIdx = keys.ToList().FindIndex(k => k.EndsWith("AddAlloggiatiCheckInMvp", StringComparison.Ordinal));
        Assert.True(alloggiatiIdx > connectIdx);
    }

    [Fact]
    public void Step1_AddOrgIdNullable_CreatesOrgTableAndUniqueSlugIndex() // AC3
    {
        using var db = NewNpgsqlContext();
        var (_, backfill, _) = MigrationIds(db);
        var migrator = db.GetService<IMigrator>();

        // Up to (but excluding) the backfill == just Step 1.
        var script = migrator.GenerateScript(toMigration: backfill);

        Assert.Contains("CREATE TABLE \"Orgs\"", script);
        Assert.Contains("IX_Orgs_Slug", script);
        Assert.Contains("ADD \"OrgId\" uuid", script); // nullable add (no NOT NULL)
        Assert.Contains("\"StripeConnectedAccountId\"", script);
        Assert.DoesNotContain("SET NOT NULL", script); // Step 1 must not flip nullability
    }

    [Fact]
    public void Step2_BackfillDefaultOrgs_IsIdempotentRelationshipWalkWithFallbackAndLog() // AC4
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var fullScript = migrator.GenerateScript();

        Assert.Contains("ON CONFLICT (\"Slug\") DO NOTHING", fullScript); // idempotent org insert
        Assert.Contains("casazen-unassigned", fullScript);                // dedicated fallback Org
        Assert.Contains("RAISE NOTICE", fullScript);                      // verified row-count log
        // Relationship walk: payments derive their org from their booking.
        Assert.Contains("UPDATE \"Payments\"", fullScript);
        Assert.Contains("UPDATE \"Bookings\"", fullScript);
        Assert.Contains("UPDATE \"LeaseContracts\"", fullScript);
    }

    [Fact]
    public void Step3_MakeOrgIdRequired_GuardsThenFlipsNotNullAndAddsRestrictedFks() // AC5/AC10b
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var fullScript = migrator.GenerateScript();

        // Pre-flight NULL guard fires before any NOT-NULL flip (fail loud, never silent).
        Assert.Contains("RAISE EXCEPTION", fullScript);
        Assert.Contains("Pre-flight failed", fullScript);
        Assert.True(
            fullScript.IndexOf("Pre-flight failed", StringComparison.Ordinal)
            < fullScript.IndexOf("SET NOT NULL", StringComparison.Ordinal),
            "Pre-flight NULL guard must precede the NOT-NULL flip");

        Assert.Contains("SET NOT NULL", fullScript);
        foreach (var fk in new[]
        {
            "FK_Properties_Orgs_OrgId", "FK_Bookings_Orgs_OrgId",
            "FK_LeaseContracts_Orgs_OrgId", "FK_Payments_Orgs_OrgId", "FK_Users_Orgs_OrgId",
        })
        {
            Assert.Contains(fk, fullScript);
        }

        Assert.Contains("ON DELETE RESTRICT", fullScript);
    }

    [Fact]
    public void Step3_MakeOrgIdRequired_DownReverts_FksAndNullability() // AC10b (tested down)
    {
        using var db = NewNpgsqlContext();
        var (_, backfill, makeRequired) = MigrationIds(db);
        var migrator = db.GetService<IMigrator>();

        // Down of Step 3 only: from MakeOrgIdRequired back to BackfillDefaultOrgs.
        var down = migrator.GenerateScript(fromMigration: makeRequired, toMigration: backfill);

        Assert.Contains("DROP CONSTRAINT \"FK_Properties_Orgs_OrgId\"", down);
        Assert.Contains("DROP NOT NULL", down);
    }

    [Fact]
    public void GuestOrgMigrations_AddNullableThenBackfillThenGuardBeforeNotNullAndRestrictFk() // TN-1
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var addNullable = keys.Single(k => k.EndsWith("AddGuestOrgIdNullable", StringComparison.Ordinal));
        var backfill = keys.Single(k => k.EndsWith("BackfillGuestOrgIds", StringComparison.Ordinal));
        var makeRequired = keys.Single(k => k.EndsWith("MakeGuestOrgIdRequired", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        var previous = keys[keys.IndexOf(addNullable) - 1];

        var step1 = migrator.GenerateScript(fromMigration: previous, toMigration: addNullable);
        var step2 = migrator.GenerateScript(fromMigration: addNullable, toMigration: backfill);
        var step3 = migrator.GenerateScript(fromMigration: backfill, toMigration: makeRequired);

        Assert.Contains("ALTER TABLE \"Guests\" ADD \"OrgId\" uuid;", step1);
        Assert.DoesNotContain("SET NOT NULL", step1);

        Assert.Contains("UPDATE \"Bookings\" b", step2);
        Assert.Contains("UPDATE \"AlloggiatiWebReports\" r", step2);
        Assert.Contains("casazen-unassigned", step2);
        Assert.Contains("RAISE NOTICE 'BackfillGuestOrgIds", step2);

        var guard = step3.IndexOf("Pre-flight failed", StringComparison.Ordinal);
        var notNull = step3.IndexOf("ALTER COLUMN \"OrgId\" SET NOT NULL", StringComparison.Ordinal);
        Assert.True(guard >= 0 && notNull > guard, "The pre-flight guard must precede the NOT NULL flip");
        Assert.Contains("FK_Guests_Orgs_OrgId", step3);
        Assert.Contains("ON DELETE RESTRICT", step3);
    }

    [Fact]
    public void ChildEntityOrgMigrations_AddNullableThenBackfillFromParentThenGuardBeforeNotNullAndRestrictFk() // TN-2
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var addNullable = keys.Single(k => k.EndsWith("AddChildEntityOrgIdNullable", StringComparison.Ordinal));
        var backfill = keys.Single(k => k.EndsWith("BackfillChildEntityOrgIds", StringComparison.Ordinal));
        var makeRequired = keys.Single(k => k.EndsWith("MakeChildEntityOrgIdsRequired", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        var previous = keys[keys.IndexOf(addNullable) - 1];

        var step1 = migrator.GenerateScript(fromMigration: previous, toMigration: addNullable);
        var step2 = migrator.GenerateScript(fromMigration: addNullable, toMigration: backfill);
        var step3 = migrator.GenerateScript(fromMigration: backfill, toMigration: makeRequired);
        var down3 = migrator.GenerateScript(fromMigration: makeRequired, toMigration: backfill);

        string[] children = ["PropertyDocuments", "OtaIntegrations", "PricingAdapterConfigs", "PricingHistories", "AlloggiatiWebReports"];
        Assert.All(children, table => Assert.Contains($"ALTER TABLE \"{table}\" ADD \"OrgId\" uuid;", step1));
        Assert.DoesNotContain("SET NOT NULL", step1);

        Assert.Contains("FROM \"Properties\" p", step2);
        Assert.Contains("UPDATE \"AlloggiatiWebReports\" r", step2);
        Assert.Contains("UPDATE \"GuestCheckInSessions\" s", step2);
        Assert.Contains("IS DISTINCT FROM", step2);
        Assert.Contains("RAISE NOTICE 'BackfillChildEntityOrgIds", step2);

        var guard = step3.IndexOf("Pre-flight failed", StringComparison.Ordinal);
        var notNull = step3.IndexOf("ALTER COLUMN \"OrgId\" SET NOT NULL", StringComparison.Ordinal);
        Assert.True(guard >= 0 && notNull > guard, "The pre-flight guard must precede the NOT NULL flip");
        Assert.All(children.Append("GuestCheckInSessions"), table => Assert.Contains($"FK_{table}_Orgs_OrgId", step3));
        Assert.DoesNotContain("ON DELETE CASCADE", step3);

        Assert.Contains("DROP CONSTRAINT \"FK_PropertyDocuments_Orgs_OrgId\"", down3);
        Assert.Contains("DROP NOT NULL", down3);
    }
}
