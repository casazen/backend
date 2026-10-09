using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-10: the two tables of the booking from a supplier's public showcase (<see cref="ServiceCustomer"/> and
/// <see cref="ShowcaseBookingHold"/>) are keyed by the supplier org and are not tenant-filtered (allow-list of
/// <see cref="TenantQueryFilterArchitectureTests"/>: the customer is anonymous, a supplier-only account has no
/// <c>User.OrgId</c>, and a host must never read them), so their isolation rests on an explicit <c>OrgId</c> predicate in every
/// statement. These guards keep it that way: the model, the SQL of the queries, and the only code allowed to use the tables. The
/// behavior is tested at the end of this class and in the services' tests; the race of two customers on PostgreSQL in
/// <c>ShowcaseBookingConcurrencyPostgresTests</c>.
/// </summary>
public class ShowcaseBookingTenancyTests
{
    /// <summary>The files that may read or write the tables for one supplier, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> TenantScopedFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Services/ShowcaseBookingService.cs"] = "The booking: HoldsOf and CustomerByEmailOf carry the OrgId predicate, the rest is an insert or a remove of a row it loaded that way.",
        ["Casazen.Infrastructure/Services/ShowcaseHoldReader.cs"] = "The holds the slot planner counts: LiveHoldsOf carries the OrgId predicate.",
        ["Casazen.Infrastructure/Services/ServiceCustomerReader.cs"] = "The contact of a customer for the e-mails: CustomerOf carries the OrgId predicate.",
        ["Casazen.Infrastructure/Services/SupplierService.Maintenance.cs"] = "The repair that merges duplicate supplier profiles moves the customers of the duplicate org; both orgs are explicit.",
    };

    /// <summary>The system jobs that work across every supplier on purpose: run only by Hangfire, reachable from no request.</summary>
    private static readonly IReadOnlyDictionary<string, string> SystemJobFiles = new Dictionary<string, string>
    {
        ["Casazen.Infrastructure/Services/ServiceRequestExpiryService.cs"] = "Deletes the holds whose time has passed, whoever they belong to (ExpiredHoldIdsOf reads ids only).",
        ["Casazen.Infrastructure/Services/ServiceCustomerPrivacyService.cs"] = "The nightly retention anonymizes the customers whose period has ended, whoever they belong to.",
    };

    private static readonly Regex AnyTableUse = new(
        @"\.(ServiceCustomers|ShowcaseBookingHolds)\b|Set<(ServiceCustomer|ShowcaseBookingHold)>\(",
        RegexOptions.Compiled);

    private static readonly Regex DbSetUse = new(
        @"db\.(?<set>ServiceCustomers|ShowcaseBookingHolds)(?<rest>[\s\S]{0,90})",
        RegexOptions.Compiled);

    // What may follow "db.<DbSet>" (whitespace removed): the org predicate, an insert, or the removal of a row loaded that way.
    private static readonly Regex AllowedContinuation = new(
        @"^(\.AsNoTracking\(\))?\.Where\((?<x>\w+)=>\k<x>\.OrgId==|^\.Add\(|^\.Remove\(",
        RegexOptions.Compiled);

    private static readonly Type[] Entities = [typeof(ServiceCustomer), typeof(ShowcaseBookingHold)];

    // ─── The model ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Entities_AreNotTenantOwned_AndHaveNoTenantFilter()
    {
        using var db = NewNpgsqlContext();

        Assert.All(Entities, entityType =>
        {
            Assert.False(typeof(ITenantOwned).IsAssignableFrom(entityType), entityType.Name);
            var entity = db.Model.FindEntityType(entityType);
            Assert.NotNull(entity);
            Assert.Null(entity.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter));
        });
    }

    [Fact]
    public void Model_TheyAreChildrenOfTheSupplierProfile_DeletedWithIt()
    {
        using var db = NewNpgsqlContext();

        Assert.All(Entities, entityType =>
        {
            var entity = db.Model.FindEntityType(entityType)!;
            var foreignKey = Assert.Single(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(SupplierProfile));
            Assert.Equal(nameof(SupplierProfile.OrgId), foreignKey.PrincipalKey.Properties.Single().Name);
            Assert.Equal("OrgId", Assert.Single(foreignKey.Properties).Name);
            Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        });
    }

    [Fact]
    public void Model_TheRequestKeepsItsCustomer_AndTheHoldOnlyRemembersTheRequest()
    {
        using var db = NewNpgsqlContext();

        var request = db.Model.FindEntityType(typeof(ServiceRequest))!;
        var customer = Assert.Single(request.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(ServiceCustomer));
        // A customer with a request is never deleted by accident: the retention anonymizes it, the repair moves it.
        Assert.Equal(DeleteBehavior.Restrict, customer.DeleteBehavior);

        var hold = db.Model.FindEntityType(typeof(ShowcaseBookingHold))!;
        var toRequest = Assert.Single(hold.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(ServiceRequest));
        Assert.Equal(DeleteBehavior.SetNull, toRequest.DeleteBehavior);
    }

    [Fact]
    public void Model_TheTablesHaveTheNamesTheIndexesAndTheChecksOfTheMigration()
    {
        using var db = NewNpgsqlContext();
        var customers = db.Model.FindEntityType(typeof(ServiceCustomer))!;
        var holds = db.Model.FindEntityType(typeof(ShowcaseBookingHold))!;

        Assert.Equal("ServiceCustomers", customers.GetTableName());
        Assert.Equal("ShowcaseBookingHolds", holds.GetTableName());

        var customerUnique = Assert.Single(customers.GetIndexes(), index => index.IsUnique);
        Assert.Equal(new[] { "OrgId", "EmailHash" }, customerUnique.Properties.Select(p => p.Name));
        Assert.Equal("UIX_ServiceCustomers_OrgId_EmailHash", customerUnique.GetDatabaseName());

        var holdUniques = holds.GetIndexes().Where(index => index.IsUnique).Select(index => index.GetDatabaseName()!).Order().ToArray();
        Assert.Equal(["UIX_ShowcaseBookingHolds_OrgId_ClientRequestId", "UIX_ShowcaseBookingHolds_OrgId_PublicCode"], holdUniques);
        Assert.Contains(holds.GetIndexes(), index => index.GetDatabaseName() == "IX_ShowcaseBookingHolds_ExpiresAt");
        Assert.Contains(holds.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { "OrgId", "StartUtc" }));

        string[] ChecksOf(Type type) => db.GetService<IDesignTimeModel>().Model
            .FindEntityType(type)!
            .GetCheckConstraints()
            .Select(c => c.Name!)
            .Order()
            .ToArray();

        Assert.Equal(["CK_ShowcaseBookingHolds_Expiry", "CK_ShowcaseBookingHolds_Interval"], ChecksOf(typeof(ShowcaseBookingHold)));
        Assert.Contains("CK_ServiceRequests_Context", ChecksOf(typeof(ServiceRequest)));

        var publicCodeIndex = Assert.Single(
            db.Model.FindEntityType(typeof(ServiceRequest))!.GetIndexes(),
            index => index.GetDatabaseName() == "UIX_ServiceRequests_SupplierOrgId_PublicCode");
        Assert.True(publicCodeIndex.IsUnique);
        Assert.Equal(new[] { "SupplierOrgId", "PublicCode" }, publicCodeIndex.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Model_ThePayloadOfAHold_IsStoredInAColumnThatSaysItIsEncrypted()
    {
        using var db = NewNpgsqlContext();

        var payload = db.Model.FindEntityType(typeof(ShowcaseBookingHold))!.FindProperty(nameof(ShowcaseBookingHold.Payload))!;

        Assert.Equal("PayloadEncrypted", payload.GetColumnName());
    }

    // ─── The SQL ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheQueriesOfOneSupplier_OnTheNpgsqlProvider_CarryTheSupplierOrgPredicateInSql()
    {
        using var db = NewNpgsqlContext();
        var org = Guid.NewGuid();
        var from = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

        var statements = new Dictionary<string, string>
        {
            ["holds of the booking"] = ShowcaseBookingService.HoldsOf(db, org).ToQueryString(),
            ["customer by e-mail"] = ShowcaseBookingService.CustomerByEmailOf(db, org, "0123abcd").ToQueryString(),
            ["live holds of the planner"] = ShowcaseHoldReader.LiveHoldsOf(db, org, from, from.AddDays(2), from).ToQueryString(),
            ["contact of a customer"] = ServiceCustomerReader.CustomerOf(db, org, Guid.NewGuid()).ToQueryString(),
        };

        Assert.All(statements, statement => Assert.Matches("\"OrgId\" = @", statement.Value));
    }

    [Fact]
    public void TheSystemStatements_OnTheNpgsqlProvider_AreAcrossSuppliersOnPurpose_AndReadNothingPersonalFromTheHolds()
    {
        using var db = NewNpgsqlContext();
        var now = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

        var expired = ServiceRequestExpiryService.ExpiredHoldIdsOf(db, now).ToQueryString();

        // Only the ids of the holds that lapsed, oldest first, a bounded batch: no payload, no e-mail index, no token, no tenant
        // predicate (the job works across every supplier).
        Assert.Matches("\"ExpiresAt\" <= @", expired);
        Assert.Matches("SELECT \\w+\\.\"Id\"\\s+FROM \"ShowcaseBookingHolds\"", expired);
        Assert.Contains("LIMIT @p", expired, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadEncrypted", expired, StringComparison.Ordinal);
        Assert.DoesNotContain("EmailHash", expired, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", expired, StringComparison.Ordinal);
        Assert.DoesNotMatch("\"OrgId\" = @", expired);
    }

    [Fact]
    public void ThePlannersHoldQuery_OnTheNpgsqlProvider_ReadsTheHoursAndTheExpiryOfOneSupplierAndNothingElse()
    {
        using var db = NewNpgsqlContext();
        var from = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

        var sql = ShowcaseHoldReader.LiveIntervalsOf(db, Guid.NewGuid(), from, from.AddDays(2), from).ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
        Assert.Matches("\"ConsumedAt\" IS NULL", sql);
        Assert.Matches("\"ExpiresAt\" > @", sql);
        foreach (var column in new[] { "PayloadEncrypted", "EmailHash", "TokenHash", "PublicCode", "ClientRequestId", "ServiceRequestId" })
            Assert.DoesNotContain(column, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReminderAndRetentionStatements_ReadOnlyShowcaseRequests_WithTheFiltersOfTheHostOff()
    {
        using var db = NewNpgsqlContext();
        var now = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

        var reminders = ServiceRequestReminderService.ReminderCandidatesOf(db, now, now.AddHours(48)).ToQueryString();
        var places = ServiceCustomerPrivacyService.CandidateRequestsOf(db, now).ToQueryString();

        foreach (var sql in new[] { reminders, places })
        {
            // The rental context of the showcase is in the statement itself, and the host's filters are not applied.
            Assert.Matches("\"RentalContext\" = 2", sql);
            Assert.DoesNotContain("PropertyId", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("PayloadEncrypted", sql, StringComparison.Ordinal);
        }

        // The reminder reads the id, the start and the take; it does not read the customer, nor the place.
        Assert.DoesNotContain("LocationAddress", reminders, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerId", reminders, StringComparison.Ordinal);
        // The retention looks for requests that still carry a place; it reads no name, no e-mail and no phone.
        Assert.Contains("LocationAddress", places, StringComparison.Ordinal);
        Assert.DoesNotContain("FullName", places, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetentionOfTheCustomers_OnTheNpgsqlProvider_CutsTheListInSql_AndReadsAgainOnlyWhatItChanges()
    {
        // With the encryption on, as in production: the converters of the encrypted columns are part of the translation.
        using var db = NewNpgsqlContext(encrypted: true);
        var cutoff = new DateTime(2024, 10, 9, 0, 0, 0, DateTimeKind.Utc);

        var list = Regex.Replace(ServiceCustomerPrivacyService.CandidateCustomersOf(db, cutoff).ToQueryString(), @"\s+", " ");
        var again = Regex.Replace(
            ServiceCustomerPrivacyService.StillDue(db, [Guid.NewGuid(), Guid.NewGuid()]).ToQueryString(), @"\s+", " ");

        // The cut is in the statement, so a night reads the customers whose period may have ended and not every customer there is;
        // the list names no one (ids, supplier and two dates: nothing to decrypt).
        Assert.Matches(@"\) < @", list);
        Assert.Contains("\"AnonymizedAt\" IS NULL", list, StringComparison.Ordinal);
        foreach (var column in new[] { "FullName", "Email", "Phone", "PayloadEncrypted" })
            Assert.DoesNotContain($"\"{column}\"", list, StringComparison.Ordinal);

        // Read again, just before the change, for the ids it is about: not anonymized and with no open request (looked for in SQL).
        Assert.Contains("= ANY (@", again, StringComparison.Ordinal);
        Assert.Contains("\"AnonymizedAt\" IS NULL", again, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", again, StringComparison.Ordinal);
        Assert.Contains("\"CustomerId\"", again, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuppliersInbox_OnTheNpgsqlProvider_SelectsThePlaceAndTheContactsOnlyForARequestTheSupplierTook()
    {
        // With the encryption on, as in production: a CASE takes its type mapping (and so the converter) from its first branch.
        using var db = NewNpgsqlContext(encrypted: true);
        var reader = new SupplierServiceRequestReader(db);

        var sql = reader.Rows(Guid.NewGuid()).ToQueryString();
        var normalized = Regex.Replace(sql, @"\s+", " ");

        // Scoped by the supplier, the host's filters off (the stay and the property belong to another tenant).
        Assert.Matches("\"SupplierOrgId\" = @", sql);

        // The street address, the floor, the notes, the e-mail and the phone are each selected exactly once, inside a CASE on the
        // statuses of a taken request (PresoInCarico, InCorso, Completato, Pagato): the database does not even return them for
        // a request that is not taken yet (and they are encrypted: they are not decrypted either).
        var disclosed = (int)ServiceRequestStatus.PresoInCarico + ", " + (int)ServiceRequestStatus.InCorso + ", "
                        + (int)ServiceRequestStatus.Completato + ", " + (int)ServiceRequestStatus.Pagato;
        foreach (var (column, table) in new[]
                 {
                     ("LocationAddress", "ServiceRequests"),
                     ("LocationFloor", "ServiceRequests"),
                     ("LocationAccessNotes", "ServiceRequests"),
                     ("Email", "ServiceCustomers"),
                     ("Phone", "ServiceCustomers"),
                 })
        {
            Assert.Single(Regex.Matches(normalized, $"\\w+\\.\"{column}\""));
            Assert.Matches($"\"Status\" IN \\({Regex.Escape(disclosed)}\\) THEN \\w+\\.\"{column}\"", normalized);
            Assert.Contains($"\"{table}\"", normalized, StringComparison.Ordinal);
        }
    }

    // ─── The code that may touch the tables ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTables_AreUsedOnlyByTheBookingTheReadersTheJobsTheRepairAndTheContext()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relative, text) in ReadSources(root))
        {
            if (TenantScopedFiles.ContainsKey(relative)
                || SystemJobFiles.ContainsKey(relative)
                || relative == "Casazen.Infrastructure/Data/AppDbContext.cs"
                || !AnyTableUse.IsMatch(CodeWithoutComments(text)))
            {
                continue;
            }

            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "The tables of the booking from the public showcase are not tenant-filtered (keyed by the supplier org): any other " +
            "reader must filter by the supplier OrgId itself. Use ShowcaseBookingService, ShowcaseHoldReader or ServiceCustomerReader, " +
            "or add the file to TenantScopedFiles with its reason and an explicit OrgId predicate: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/ShowcaseBookingService.cs")]
    [InlineData("Casazen.Infrastructure/Services/ShowcaseHoldReader.cs")]
    [InlineData("Casazen.Infrastructure/Services/ServiceCustomerReader.cs")]
    [InlineData("Casazen.Infrastructure/Services/SupplierService.Maintenance.cs")]
    public void EveryStatementOnTheTables_CarriesTheOrgPredicateOrIsAnInsertOrARemove(string relative)
    {
        var text = CodeWithoutComments(File.ReadAllText(Path.Combine(FindRepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar))));
        var statements = DbSetUse.Matches(text);
        Assert.NotEmpty(statements);

        var offenders = statements
            .Select(m => (Set: m.Groups["set"].Value, Remainder: Regex.Replace(m.Groups["rest"].Value, @"\s+", string.Empty)))
            .Where(statement => !AllowedContinuation.IsMatch(statement.Remainder))
            .Select(statement => $"{statement.Set}{statement.Remainder[..Math.Min(statement.Remainder.Length, 50)]}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{relative}: a statement on a table of the booking without the explicit OrgId predicate: {string.Join(" | ", offenders)}");
    }

    [Fact]
    public void TheBookingFiles_UseBothTables_AGuardOnTheGuard()
    {
        // If the regexes stopped matching, the checks above would pass for nothing.
        var root = FindRepositoryRoot();
        var used = TenantScopedFiles.Keys
            .SelectMany(relative => DbSetUse.Matches(CodeWithoutComments(File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))))
                .Select(m => m.Groups["set"].Value))
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(new[] { "ServiceCustomers", "ShowcaseBookingHolds" }, used);
    }

    [Fact]
    public void TheSystemJobs_AreReachedByNoRequest_OnlyByTheirHangfireJobAndTheRegistration()
    {
        var root = FindRepositoryRoot();
        var systemTypes = new[] { "IServiceRequestExpiryService", "IServiceRequestReminderService", "IServiceCustomerPrivacyService" };
        var offenders = new List<string>();

        foreach (var folder in new[] { "Controllers", "Middleware", "Filters", "Hubs" })
        {
            var directory = Path.Combine(root, "Casazen.Web", folder);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var text = CodeWithoutComments(File.ReadAllText(path));
                offenders.AddRange(systemTypes.Where(type => text.Contains(type, StringComparison.Ordinal)).Select(type => $"{Path.GetFileName(path)}: {type}"));
            }
        }

        Assert.True(offenders.Count == 0, "A system job of the booking is reachable from a request: " + string.Join(", ", offenders));
    }

    // ─── The behavior ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoSuppliers_NeverSeeEachOthersHoldsCustomersOrRequests()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (mine, _) = await s.BookedAsync();
        var (hold, _) = await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "anna.verdi@example.com"));
        var now = s.Clock.GetUtcNow().UtcDateTime;
        var day = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

        // The planner of the other supplier counts nothing of mine: its Friday is entirely free.
        var theirHolds = await s.Kit.Holds.ListLiveAsync(other.OrgId, day, day.AddDays(1), now);
        var myHolds = await s.Kit.Holds.ListLiveAsync(s.SupplierOrgId, day, day.AddDays(1), now);
        Assert.Empty(theirHolds);
        Assert.Equal(hold.Id, (await s.HoldsAsync()).Single(h => h.ConsumedAt is null).Id);
        Assert.Single(myHolds);
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync(other.OrgId));
        Assert.Contains(ServiceRequestScenario.FridayAt14, await s.FreeSlotsOfFridayAsync(other.OrgId));

        // The contact of a customer is found by its own supplier only.
        Assert.NotNull(await s.Kit.Customers.FindContactAsync(s.SupplierOrgId, mine.CustomerId!.Value));
        Assert.Null(await s.Kit.Customers.FindContactAsync(other.OrgId, mine.CustomerId!.Value));

        // And nothing of it is in the other supplier's tables or inbox.
        Assert.Empty(await s.HoldsAsync(other.OrgId));
        Assert.Empty(await s.CustomersAsync(other.OrgId));
        var (items, total) = await s.Reader.ListAsync(other.OrgId, new Casazen.Core.Suppliers.SupplierInboxQuery([], null, null, 1, 100));
        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task TheSameSlotAtTwoSuppliers_IsTwoBookings()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();

        var mine = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());
        var theirs = await s.Kit.Booking.CreateHoldAsync(other.Profile, await s.InputAsync(service: other.ServiceSlug));

        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Single(await s.HoldsAsync());
        Assert.Single(await s.HoldsAsync(other.OrgId));
    }

    [Fact]
    public async Task TheSupplierOrgThatIsAlsoAHostOrg_KeepsItsTwoKindsOfRequestsApart()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (showcase, _) = await s.BookedAsync();
        var org = s.SupplierOrgId;

        // The same org id is the host of its own requests and the supplier of its showcase's: a check on the org alone would mix them.
        var asHost = await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.OrgId == org && r.RentalContext != ServiceRequestRentalContext.Showcase)
            .ToListAsync();
        var asSupplier = await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.SupplierOrgId == org && r.RentalContext == ServiceRequestRentalContext.Showcase)
            .ToListAsync();

        Assert.Empty(asHost);
        Assert.Equal(showcase.Id, Assert.Single(asSupplier).Id);
        Assert.Equal(org, showcase.OrgId);
        Assert.Equal(org, showcase.SupplierOrgId);
    }

    // ─── helpers ───

    /// <summary>A context on the Npgsql provider that never connects; <paramref name="encrypted"/> gives it the encryption of the columns.</summary>
    private static AppDbContext NewNpgsqlContext(bool encrypted = false) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            tenantContext: null,
            encrypted ? new EphemeralDataProtectionProvider() : null);

    /// <summary>The source without the lines that are only a comment (documentation names the tables without using them).</summary>
    private static string CodeWithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static IEnumerable<(string Relative, string Text)> ReadSources(string root)
    {
        foreach (var project in new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal))
                    continue;

                yield return (relative, File.ReadAllText(path));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
