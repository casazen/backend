using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: the retention of the private customers of the suppliers (<c>Gdpr:Retention:SupplierCustomers</c>), applied by the
/// nightly <c>gdpr-data-retention</c> job and off until the product owner and legal decide a period and cite its source. A
/// customer is anonymized when none of its requests is open and the last one ended more than the period ago; the place of a
/// request that is over loses the street address, the floor and the access notes.
/// </summary>
public class ServiceCustomerPrivacyServiceTests
{
    private static readonly RetentionPeriodOptions TwoYears = new() { Years = 2, Source = "Art. 2220 c.c. (test)" };

    [Fact]
    public async Task ApplyRetention_WithoutAPeriodAndItsSource_AnonymizesNothing_AndSaysSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays(365 * 10));

        foreach (var period in new[]
                 {
                     new RetentionPeriodOptions(),
                     new RetentionPeriodOptions { Years = 2 },
                     new RetentionPeriodOptions { Source = "Art. 2220 c.c." },
                     new RetentionPeriodOptions { Years = -1, Source = "Art. 2220 c.c." },
                 })
        {
            var logger = new CapturingLogger<ServiceCustomerPrivacyService>();

            var run = await Service(s, period, logger).ApplyRetentionAsync();

            Assert.Equal(new ServiceCustomerRetentionRun(false, 0, 0), run);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Gdpr:Retention:SupplierCustomers"));
        }

        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Null(customer.AnonymizedAt);
        Assert.Equal(ShowcaseScenario.CustomerName, customer.FullName);
    }

    [Fact]
    public async Task ApplyRetention_ACustomerWhoseLastRequestEndedMoreThanThePeriodAgo_IsAnonymized()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await CompletedAsync(s);
        var before = Assert.Single(await s.CustomersAsync());
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) + 10));
        var now = s.Clock.GetUtcNow().UtcDateTime;

        var run = await Service(s, TwoYears).ApplyRetentionAsync();

        Assert.Equal(new ServiceCustomerRetentionRun(true, 1, 1), run);
        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Equal(ServiceCustomerPrivacyService.AnonymizedValue, customer.FullName);
        Assert.Equal(ServiceCustomerPrivacyService.AnonymizedEmail(customer.Id), customer.Email);
        Assert.Equal($"ANON-{customer.Id:N}@deleted.local", customer.Email);
        Assert.Null(customer.Phone);
        Assert.Equal(string.Empty, customer.ConsentIp);
        Assert.Equal(ServiceCustomerPrivacyService.AnonymizedEmailHash(customer.Id), customer.EmailHash);
        Assert.NotEqual(before.EmailHash, customer.EmailHash);
        Assert.Equal(now, customer.AnonymizedAt);
        // What proves the consent stays: the version of the notice and when it was accepted; and the language.
        Assert.Equal(before.PrivacyNoticeVersion, customer.PrivacyNoticeVersion);
        Assert.Equal(before.PrivacyAcceptedAt, customer.PrivacyAcceptedAt);
        Assert.Equal(before.Locale, customer.Locale);

        // The place of the work: the street, the floor and the notes go; the comune and the postal code stay.
        var place = await s.ReadAsync(request.Id);
        Assert.Null(place.LocationAddress);
        Assert.Null(place.LocationFloor);
        Assert.Null(place.LocationAccessNotes);
        Assert.Equal(ShowcaseScenario.City, place.LocationCity);
        Assert.Equal(ShowcaseScenario.PostalCode, place.LocationPostalCode);
        Assert.Equal(customer.Id, place.CustomerId);
    }

    [Fact]
    public async Task ApplyRetention_BeforeThePeriodHasEnded_KeepsEverything()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) - 30));

        var run = await Service(s, TwoYears).ApplyRetentionAsync();

        Assert.Equal(new ServiceCustomerRetentionRun(true, 0, 0), run);
        Assert.Null(Assert.Single(await s.CustomersAsync()).AnonymizedAt);
        Assert.Equal(ShowcaseScenario.Address, (await s.ReadAsync(request.Id)).LocationAddress);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    public async Task ApplyRetention_ACustomerWithARequestThatIsStillOpen_IsKept(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        var tracked = await s.Db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == request.Id);
        tracked.Status = status;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        s.Clock.Advance(TimeSpan.FromDays(365 * 5));

        var run = await Service(s, TwoYears).ApplyRetentionAsync();

        Assert.Equal(new ServiceCustomerRetentionRun(true, 0, 0), run);
        Assert.Null(Assert.Single(await s.CustomersAsync()).AnonymizedAt);
        Assert.Equal(ShowcaseScenario.Address, (await s.ReadAsync(request.Id)).LocationAddress);
    }

    [Fact]
    public async Task ApplyRetention_TheLastRequestCounts_NotTheFirst()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays(365 * 2));
        // A second request, over a year and a half later than the first, completed too.
        var second = await CompletedAsync(s, ServiceRequestScenario.FridayAt10.AddDays(365 * 2 + 3));
        s.Clock.Advance(TimeSpan.FromDays(40));

        var run = await Service(s, TwoYears).ApplyRetentionAsync();

        // The first request's period has ended, the second's has not: the customer is the same, so it is kept; the place of the
        // first request goes, the one of the second stays.
        Assert.Equal(new ServiceCustomerRetentionRun(true, 0, 1), run);
        Assert.Null(Assert.Single(await s.CustomersAsync()).AnonymizedAt);
        Assert.Equal(ShowcaseScenario.Address, (await s.ReadAsync(second.Id)).LocationAddress);
    }

    [Fact]
    public async Task ApplyRetention_ACustomerWithNoRequest_IsCountedFromItsCreation()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        s.Db.ServiceCustomers.Add(new ServiceCustomer
        {
            OrgId = s.SupplierOrgId,
            EmailHash = s.Kit.CustomerIndex.HashEmail("nessuna.richiesta@example.com"),
            FullName = "Nessuna Richiesta",
            Email = "nessuna.richiesta@example.com",
            Locale = "it",
            CreatedAt = s.Clock.GetUtcNow().UtcDateTime,
            UpdatedAt = s.Clock.GetUtcNow().UtcDateTime,
        });
        await s.Db.SaveChangesAsync();
        s.Clock.Advance(TimeSpan.FromDays(365 * 2 - 5));
        var early = await Service(s, TwoYears).ApplyRetentionAsync();
        s.Clock.Advance(TimeSpan.FromDays(10));

        var late = await Service(s, TwoYears).ApplyRetentionAsync();

        Assert.Equal(0, early.Customers);
        Assert.Equal(1, late.Customers);
    }

    [Fact]
    public async Task ApplyRetention_Twice_FindsNothingTheSecondTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) + 10));
        var first = await Service(s, TwoYears).ApplyRetentionAsync();
        var anonymizedAt = Assert.Single(await s.CustomersAsync()).AnonymizedAt;
        s.Clock.Advance(TimeSpan.FromDays(30));

        var second = await Service(s, TwoYears).ApplyRetentionAsync();

        Assert.Equal(new ServiceCustomerRetentionRun(true, 1, 1), first);
        Assert.Equal(new ServiceCustomerRetentionRun(true, 0, 0), second);
        Assert.Equal(anonymizedAt, Assert.Single(await s.CustomersAsync()).AnonymizedAt);
    }

    [Fact]
    public async Task ApplyRetention_AnAnonymizedCustomer_IsNotFoundByItsOldAddress_ANewBookingMakesANewCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) + 10));
        await Service(s, TwoYears).ApplyRetentionAsync();

        var (request, _) = await s.BookedAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(742)));

        var customers = await s.CustomersAsync();
        Assert.Equal(2, customers.Count);
        var fresh = Assert.Single(customers, c => c.AnonymizedAt is null);
        Assert.Equal(fresh.Id, request.CustomerId);
        Assert.Equal(ShowcaseScenario.CustomerName, fresh.FullName);
        Assert.Equal(s.Kit.CustomerIndex.HashEmail(ShowcaseScenario.CustomerEmail), fresh.EmailHash);
        Assert.Single(customers, c => c.AnonymizedAt is not null);
    }

    [Fact]
    public async Task ApplyRetention_WorksAcrossSuppliers_AndNeverTouchesTheRequestsOfHosts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var mine = await CompletedAsync(s);
        var (theirs, _) = await s.BookedAsync(await s.InputAsync(service: other.ServiceSlug), other.Profile);
        var host = await s.TakenAsync();
        // The other supplier took its request: it is open (PresoInCarico) for as long as the supplier does not complete it.
        await s.Service.TakeAsync(theirs.Id, other.OrgId, "auth0|someone-else");
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) + 10));

        var run = await Service(s, TwoYears).ApplyRetentionAsync();

        // The open request of the other supplier keeps its customer; the first supplier's customer goes.
        Assert.Equal(1, run.Customers);
        Assert.NotNull(Assert.Single(await s.CustomersAsync()).AnonymizedAt);
        Assert.Null(Assert.Single(await s.CustomersAsync(other.OrgId)).AnonymizedAt);
        Assert.Null((await s.ReadAsync(mine.Id)).LocationAddress);
        var hostRequest = await s.ReadAsync(host.Id);
        Assert.Equal(ServiceRequestRentalContext.ShortRent, hostRequest.RentalContext);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, hostRequest.Status);
        Assert.Null(hostRequest.LocationAddress);
    }

    [Fact]
    public async Task ApplyRetention_AnAnonymizedCustomer_HasNoContactToWriteTo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) + 10));
        await Service(s, TwoYears).ApplyRetentionAsync();

        var contact = await s.Kit.Customers.FindContactAsync(s.SupplierOrgId, request.CustomerId!.Value);

        Assert.NotNull(contact);
        Assert.True(contact.Anonymized);
        Assert.Equal(ServiceCustomerPrivacyService.AnonymizedValue, contact.FullName);
    }

    [Fact]
    public async Task ApplyRetention_TheLogsNameNoCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) + 10));
        var logger = new CapturingLogger<ServiceCustomerPrivacyService>();

        await Service(s, TwoYears, logger).ApplyRetentionAsync();

        Assert.NotEmpty(logger.Entries);
        foreach (var secret in new[] { "Mario", "Rossi", ShowcaseScenario.CustomerEmail, "Segretissima" })
            Assert.DoesNotContain(secret, logger.AllOutput);
    }

    [Fact]
    public void Anonymize_ReplacesEveryPersonalField_AndKeepsTheConsentProof()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var customer = new ServiceCustomer
        {
            OrgId = Guid.NewGuid(),
            EmailHash = "abc",
            FullName = "Mario Rossi",
            Email = "mario.rossi@example.com",
            Phone = "+393331234567",
            Locale = "en",
            PrivacyNoticeVersion = "2026-11-v1",
            PrivacyAcceptedAt = created,
            ConsentIp = "203.0.113.7",
        };
        var now = created.AddYears(3);

        ServiceCustomerPrivacyService.Anonymize(customer, now);

        Assert.Equal("ANONYMIZED", customer.FullName);
        Assert.Equal($"ANON-{customer.Id:N}@deleted.local", customer.Email);
        Assert.Null(customer.Phone);
        Assert.Equal(string.Empty, customer.ConsentIp);
        Assert.Equal($"anon-{customer.Id:N}", customer.EmailHash);
        Assert.Equal(now, customer.AnonymizedAt);
        Assert.Equal("2026-11-v1", customer.PrivacyNoticeVersion);
        Assert.Equal(created, customer.PrivacyAcceptedAt);
        Assert.Equal("en", customer.Locale);
        // Anonymizing twice does not move the date.
        ServiceCustomerPrivacyService.Anonymize(customer, now.AddYears(1));
        Assert.Equal(now, customer.AnonymizedAt);
    }

    // ─── helpers ───

    /// <summary>A showcase request that was taken, started and completed at its time (Friday 9 October by default).</summary>
    private static async Task<ServiceRequest> CompletedAsync(ServiceRequestScenario s, DateTime? start = null)
    {
        var (request, _) = await s.BookedAsync(await s.InputAsync(start));
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        await s.Service.StartAsync(request.Id, s.SupplierOrgId);
        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);
        return await s.ReadAsync(request.Id);
    }

    private static ServiceCustomerPrivacyService Service(
        ServiceRequestScenario s,
        RetentionPeriodOptions period,
        ILogger<ServiceCustomerPrivacyService>? logger = null) =>
        new(
            s.Db,
            Options.Create(new GdprOptions { Retention = new GdprRetentionOptions { SupplierCustomers = period } }),
            logger ?? new CapturingLogger<ServiceCustomerPrivacyService>(),
            s.Clock);
}
