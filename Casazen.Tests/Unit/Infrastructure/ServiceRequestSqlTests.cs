using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-04: the LINQ of the new queries (inbox filters and sorts, earnings, answers, the requests the auto-cancel job reads)
/// becomes SQL on the PostgreSQL provider without a server: <c>ToQueryString</c> compiles the query, so a construct that only
/// works in memory fails here instead of on the first request in production. The results on real rows are checked by the
/// in-memory tests and, on PostgreSQL, by the <c>[PostgresFact]</c> tests (CI).
/// </summary>
public class ServiceRequestSqlTests
{
    private static readonly Guid SupplierOrg = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly DateTimeOffset Instant = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    // ─── The inbox ───

    [Fact]
    public void InboxRows_AreScopedByTheSupplierAndNeverJoinTheGuest()
    {
        using var db = NewNpgsqlContext();

        var sql = Reader(db).Rows(SupplierOrg).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.DoesNotContain("Guests", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GuestId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FirstName\"", sql, StringComparison.Ordinal);
        // The filters of the host (tenant, soft delete) are off: the property and the stay belong to another tenant.
        Assert.DoesNotContain("\"DeletedAt\" IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void InboxFilter_EveryFilterAtOnce_TranslatesToPostgresql()
    {
        using var db = NewNpgsqlContext();
        var query = new SupplierInboxQuery(
            [ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico],
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            1,
            20,
            SupplierInboxSort.Urgency,
            Service: "cleaning",
            Comune: "Roma",
            When: SupplierInboxWhen.Week,
            ClientId: Guid.NewGuid());
        var reader = Reader(db);

        var sql = reader.Filter(reader.Rows(SupplierOrg), query).ToQueryString();

        Assert.Matches("\"Status\" = ANY|\"Status\" IN", sql);
        Assert.Contains("\"Category\" = ", sql);
        Assert.Matches("(?i)lower\\(", sql);
        Assert.Contains("\"ComuneIstatCode\"", sql);
        Assert.Matches("(?i)coalesce\\(", sql);
        Assert.Contains("\"ScheduledStartUtc\"", sql);
        Assert.Contains("\"CheckOutDate\"", sql);
        Assert.Matches("\"SupplierOrgId\" = @", sql);
    }

    [Fact]
    public void InboxFilter_ServiceGivenAsAnId_FiltersOnTheListing_AndAsACategoryOnTheCategory()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);
        var listing = Guid.NewGuid();
        SupplierInboxQuery Query(string service) => new([], null, null, 1, 20, Service: service);

        var byListing = reader.Filter(reader.Rows(SupplierOrg), Query(listing.ToString())).ToQueryString();
        var byCategory = reader.Filter(reader.Rows(SupplierOrg), Query("Cleaning")).ToQueryString();

        Assert.Contains("\"ServiceListingId\" = @", byListing);
        Assert.DoesNotContain("\"Category\" = @", byListing, StringComparison.Ordinal);
        Assert.Contains("\"Category\" = @", byCategory);
        Assert.DoesNotContain("\"ServiceListingId\" = @", byCategory, StringComparison.Ordinal);
    }

    [Fact]
    public void InboxFilter_ClientId_FiltersOnTheCustomerOrTheHostOrg()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var sql = reader.Filter(reader.Rows(SupplierOrg), new SupplierInboxQuery([], null, null, 1, 20, ClientId: Guid.NewGuid())).ToQueryString();

        // The supplier predicate and the client one: two different columns of the request. The client is the private customer of
        // a showcase request (SP-10) or, when it has none, the host org that owns the request.
        Assert.Contains("\"SupplierOrgId\" = @", sql);
        Assert.Matches("COALESCE\\([^)]*\"CustomerId\"[^)]*\"OrgId\"[^)]*\\) = @", sql);
    }

    [Theory]
    [InlineData(SupplierInboxSort.Activity, "DESC")]
    [InlineData(SupplierInboxSort.Urgency, "\"ResponseDueAt\"")]
    [InlineData(SupplierInboxSort.WorkTime, "\"ScheduledStartUtc\"")]
    public void InboxSort_EveryOrder_TranslatesToPostgresql(SupplierInboxSort sort, string expectedInOrderBy)
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var sql = SupplierServiceRequestReader.Sort(reader.Rows(SupplierOrg), sort).Skip(20).Take(20).ToQueryString();

        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Contains(expectedInOrderBy, orderBy);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
    }

    [Fact]
    public void InboxFilter_NoFilter_AddsNothingToTheSupplierPredicate()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var plain = reader.Rows(SupplierOrg).ToQueryString();
        var filtered = reader.Filter(reader.Rows(SupplierOrg), new SupplierInboxQuery([], null, null, 1, 20)).ToQueryString();

        Assert.Equal(plain, filtered);
    }

    // ─── The earnings, the answers, the auto-cancel ───

    [Fact]
    public void Earnings_TheMonthAndTheAmountToCollect_AreSummedByTheDatabaseForTheSupplierOnly()
    {
        using var db = NewNpgsqlContext();

        var month = SupplierKpiService.CompletedInOf(
            db, SupplierOrg, new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc)).ToQueryString();
        var toCollect = SupplierKpiService.ToCollectOf(db, SupplierOrg).ToQueryString();

        foreach (var sql in new[] { month, toCollect })
        {
            Assert.Matches("\"SupplierOrgId\" = @", sql);
            Assert.Matches("(?i)sum\\(", sql);
            Assert.Matches("(?i)count\\(\\*\\)", sql);
            Assert.Contains("\"FinalAmountCents\"", sql);
        }

        Assert.Contains("\"CompletedAt\" >= @", month);
        Assert.Contains("\"CompletedAt\" < @", month);
        Assert.Matches("\"Status\" = 3", toCollect);
    }

    [Fact]
    public void Answers_AreTheTakenRequestsOfTheSupplierSinceTheWindow()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierKpiService.AnswersOf(db, SupplierOrg, Instant.UtcDateTime.AddDays(-90)).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Contains("\"TakenAt\" IS NOT NULL", sql);
        Assert.Contains("\"TakenAt\" >= @", sql);
    }

    [Fact]
    public void AutoCancel_TheDueRequests_AreNewWithADeadlinePastAndNoProposalOldestFirstAndCapped()
    {
        using var db = NewNpgsqlContext();

        var sql = ServiceRequestAutoCancelService.DueIdsOf(db, Instant.UtcDateTime).ToQueryString();

        Assert.Matches("\"Status\" = 0", sql); // Richiesto
        Assert.Contains("\"ResponseDueAt\" IS NOT NULL", sql);
        Assert.Contains("\"ResponseDueAt\" <= @", sql);
        Assert.Contains("\"ProposedStartUtc\" IS NULL", sql);
        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.StartsWith("ORDER BY", orderBy);
        Assert.Contains("\"ResponseDueAt\"", orderBy);
        // The cap is a parameter of the statement (LIMIT @p) with the value of MaxPerRun.
        Assert.Matches(@"LIMIT @\w+", sql);
        Assert.Matches($@"@\w+='{ServiceRequestAutoCancelService.MaxPerRun}'", sql);
        // The job works across every org: no tenant predicate narrows it to one host.
        Assert.DoesNotContain("\"OrgId\" = @", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SupplierOrgId\" = @", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_TheRequestsOfASupplierAreIndexedByTheirTime_AndTheListingIsKeptWhenTheServiceIsDeleted()
    {
        using var db = NewNpgsqlContext();
        var entity = db.Model.FindEntityType(typeof(Casazen.Core.Entities.ServiceRequest))!;

        var index = Assert.Single(entity.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["SupplierOrgId", "ScheduledStartUtc"]));
        Assert.False(index.IsUnique);
        var foreignKey = Assert.Single(entity.GetForeignKeys(), fk => fk.Properties.Any(p => p.Name == "ServiceListingId"));
        Assert.Equal(DeleteBehavior.SetNull, foreignKey.DeleteBehavior);
        Assert.Equal(typeof(Casazen.Core.Entities.SupplierServiceListing), foreignKey.PrincipalEntityType.ClrType);
    }

    private static SupplierServiceRequestReader Reader(AppDbContext db) => new(db, new FixedTimeProvider(Instant));

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);
}
