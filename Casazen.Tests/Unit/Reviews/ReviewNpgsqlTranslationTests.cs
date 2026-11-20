using System.Data;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Tests.Unit.Messages;
using Casazen.Tests.Unit.Octorate;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// E05/F06/US01/T02 and T03: the queries of the reviews, run on the PostgreSQL provider with every kind of scope, without a server (see <see cref="NpgsqlTranslationProbe"/>). The in-memory provider
/// evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL; this fails here, before CI, when the list of the reviews of the hub (the reviews of the booking site and of a channel
/// in one statement), the detail, the read of a booking, the two sends or the selection of the job of the sync has no translation, and it checks the shape of the statements: one <c>UNION ALL</c> paged in
/// the database, the scope of a collaborator as an EXISTS on its grants in each source, and the words of a review in no statement of the list but the two that read the page.
/// </summary>
public class ReviewNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid BookingId = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "collaborator-one-area" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore", GrantedMode: RentalMode.Short),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    public static TheoryData<string> EveryScope() => new() { "collaborator", "collaborator-one-area", "account-in-no-team", "whole-org" };

    /// <summary>One row of each source of the reviews, in the order of the columns of the page statement, so the probe goes on to the statements that read the words of the page.</summary>
    private static DataTable? PageRowsOf(string sql)
    {
        if (!IsThePage(sql))
            return null;

        var table = new DataTable();
        table.Columns.Add("Source", typeof(int));
        table.Columns.Add("KeyId", typeof(Guid));
        table.Columns.Add("Channel", typeof(int));
        table.Columns.Add("Kind", typeof(int));
        table.Columns.Add("PropertyId", typeof(Guid));
        table.Columns.Add("PropertyName", typeof(string));
        table.Columns.Add("BookingId", typeof(Guid));
        table.Columns.Add("BookingCode", typeof(string));
        table.Columns.Add("GuestName", typeof(string));
        table.Columns.Add("CheckInDate", typeof(DateTime));
        table.Columns.Add("CheckOutDate", typeof(DateTime));
        table.Columns.Add("BookingStatus", typeof(int));
        table.Columns.Add("IsArchived", typeof(bool));
        table.Columns.Add("LastMessageAt", typeof(DateTime));
        table.Columns.Add("UnreadCount", typeof(int));
        table.Columns.Add("NeedsReply", typeof(bool));
        var at = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);
        table.Rows.Add((int)MessageThreadSource.Review, Guid.NewGuid(), 0, (int)MessageHubThreadKind.Review, PropertyId, "Trullo", BookingId, "7K3M9PQ2XV", "Anna Ross", at, at.AddDays(3), 3, false, at, 0, true);
        table.Rows.Add((int)MessageThreadSource.OtaReview, Guid.NewGuid(), 1, (int)MessageHubThreadKind.Review, PropertyId, "Trullo", BookingId, "7K3M9PQ2XV", "Anna Ross", at, at.AddDays(3), 3, false, at, 0, false);
        return table;
    }

    private static bool IsThePage(string sql) =>
        sql.TrimStart().StartsWith("SELECT ", StringComparison.Ordinal)
        && sql.Contains("\"Source\"", StringComparison.Ordinal)
        && sql.Contains("UNION ALL", StringComparison.Ordinal)
        && sql.Contains("OFFSET", StringComparison.Ordinal);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    private static OtaReviewService ReviewService(AppDbContext db, IOctorateOrgScope? scope = null) =>
        new(
            db,
            Mock.Of<IOctorateApiClient>(),
            scope ?? new OctorateOrgScope(NullTenantContext.Instance),
            Mock.Of<IChannelMappingService>(),
            new TestFeatureFlags(FeatureFlags.OctorateConnector),
            new DomainEventRecorder(db, NullTenantContext.Instance, TimeProvider.System),
            NullLogger<OtaReviewService>.Instance);

    // ─── The list and the detail of the hub ───

    [Theory]
    [MemberData(nameof(EveryScope))]
    public async Task TheListOfReviews_IsTranslatedToSqlByNpgsql_AsOneUnionPagedInTheDatabase(string scope)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, count: 2, rows: PageRowsOf);
        var service = MessageHubTestKit.Service(db);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.ListAsync(ScopeOf(scope), new MessageHubFilter(Kind: MessageHubThreadKind.Review), 1, 20));

        var page = Assert.Single(statements, IsThePage);
        Assert.Equal(1, CountOf(page, "UNION ALL"));
        Assert.Contains("ORDER BY", page, StringComparison.Ordinal);
        Assert.Contains("OFFSET", page, StringComparison.Ordinal);
        Assert.Contains("\"Reviews\"", page, StringComparison.Ordinal);
        Assert.Contains("\"OtaReviews\"", page, StringComparison.Ordinal);
        Assert.StartsWith("SELECT COUNT", statements.First().TrimStart(), StringComparison.OrdinalIgnoreCase);

        // The reviews are the whole statement: no conversation, no word of a review, in the count or in the page.
        Assert.All(new[] { statements.First(), page }, sql =>
        {
            Assert.DoesNotContain("\"GuestThreads\"", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("\"OtaMessageThreads\"", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("TextEncrypted", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Text\"", sql, StringComparison.Ordinal);
        });

        // The words are read for the page, after it: one statement for each source.
        Assert.Contains(statements, sql => sql.Contains("\"OtaReviews\"", StringComparison.Ordinal) && sql.Contains("GuestReviewTextEncrypted", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("FROM \"Reviews\"", StringComparison.Ordinal) && sql.Contains("\"Text\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARestrictedScope_IsAnExistsOnTheGrantsInBothSourcesOfTheReviews_AndAnOrgWideOneHasNone()
    {
        var restricted = new List<string>();
        await using (var db = NpgsqlTranslationProbe.NewContext(restricted, count: 1))
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(
                () => MessageHubTestKit.Service(db).ListAsync(ScopeOf("collaborator"), new MessageHubFilter(Kind: MessageHubThreadKind.Review), 1, 20));
        }

        var orgWide = new List<string>();
        await using (var db = NpgsqlTranslationProbe.NewContext(orgWide, count: 1))
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(
                () => MessageHubTestKit.Service(db).ListAsync(ScopeOf("whole-org"), new MessageHubFilter(Kind: MessageHubThreadKind.Review), 1, 20));
        }

        var restrictedPage = Assert.Single(restricted, IsThePage);
        var orgWidePage = Assert.Single(orgWide, IsThePage);

        Assert.True(CountOf(restrictedPage, "PropertyMemberAccesses") >= 2, "each source is narrowed to the grants of the person");
        Assert.Equal(0, CountOf(orgWidePage, "PropertyMemberAccesses"));
        Assert.True(CountOf(restrictedPage, "\"OrgId\" = @") >= 2, "each source names the org of the scope");
        Assert.True(CountOf(orgWidePage, "\"OrgId\" = @") >= 2, "each source names the org of the scope");
    }

    [Theory]
    [MemberData(nameof(EveryScope))]
    public async Task TheDetailOfAReview_IsTranslatedToSqlByNpgsql_ForBothSources(string scope)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var hub = new MessageHubService(
            db,
            Mock.Of<IGuestMessageService>(),
            Mock.Of<IChannelMappingService>(),
            Mock.Of<IOtaAttachmentFetcher>(),
            NullLogger<MessageHubService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero)),
            ReviewService(db));

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => hub.GetReviewThreadAsync(ScopeOf(scope), MessageThreadKey.ForReview(Guid.NewGuid()), includeAmounts: true));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => hub.GetReviewThreadAsync(ScopeOf(scope), MessageThreadKey.ForOtaReview(Guid.NewGuid()), includeAmounts: true));

        Assert.Contains(statements, sql => sql.Contains("FROM \"Reviews\"", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("FROM \"OtaReviews\"", StringComparison.Ordinal));
    }

    // ─── The service and the job of the channels ───

    [Theory]
    [MemberData(nameof(EveryScope))]
    public async Task TheQueriesOfTheServiceOfTheReviews_AreTranslatedToSqlByNpgsql(string scope)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var orgScope = new OctorateOrgScope(NullTenantContext.Instance);
        var service = ReviewService(db, orgScope);
        var host = ScopeOf(scope);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.GetAsync(host, Guid.NewGuid()));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListForBookingAsync(host, BookingId));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.RefreshAsync(host, BookingId));
        using (orgScope.Use(OrgId))
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.SyncBookingAsync(BookingId));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.FindPropertyIdAsync(Guid.NewGuid()));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ReplyAsync(Guid.NewGuid(), "Grazie!", "reply-key-0001", "auth0|titolare"));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.SendHostReviewAsync(
                Guid.NewGuid(),
                new OtaHostReviewCommand([new ReviewCategoryRating("cleanliness", 5m)], "Ottimi ospiti", null),
                "review-key-0001",
                "auth0|titolare"));
        }

        Assert.Contains(statements, sql => sql.Contains("\"OtaReviews\"", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("\"Bookings\"", StringComparison.Ordinal));
    }

    private static DataTable? ConnectedOrg(string sql)
    {
        if (!sql.Contains("\"OctorateConnections\"", StringComparison.Ordinal))
            return null;

        var table = new DataTable();
        table.Columns.Add("OrgId", typeof(Guid));
        table.Rows.Add(OrgId);
        return table;
    }

    [Fact]
    public async Task TheSelectionOfTheJobOfTheSync_IsTranslatedToSqlByNpgsql_TheBookingsNeverReadAndTheOnesWhoseTimeHasCome()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: ConnectedOrg);
        var mappings = new Mock<IChannelMappingService>();
        mappings
            .Setup(m => m.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Row("585001", OctorateMappingStatus.Confirmed, PropertyId)]);
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(e => e.EnvironmentName).Returns("Staging");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero));
        var job = new OtaReviewSyncJob(
            db,
            Mock.Of<IOtaReviewService>(),
            mappings.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            new TestFeatureFlags(FeatureFlags.OctorateConnector),
            host.Object,
            new OctorateRateLimiter(clock),
            clock,
            new OptionsWrapper<OctorateReviewsOptions>(new OctorateReviewsOptions()),
            NullLogger<OtaReviewSyncJob>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => job.ExecuteAsync(CancellationToken.None));

        // The org was found, then both selections were compiled and sent: the bookings with no row of the review yet, and the rows that are due.
        Assert.Contains(statements, sql => sql.Contains("\"Bookings\"", StringComparison.Ordinal) && sql.Contains("NOT EXISTS", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("\"OtaReviews\"", StringComparison.Ordinal) && sql.Contains("\"NextPollAt\"", StringComparison.Ordinal));
    }
}
