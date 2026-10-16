using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-03b: what the supplier's calendar shows of each request, as decision D9 lets the supplier know it before taking the request:
/// the name of the service, who asks ("Nome C." for a private customer until the take), the comune, where the request comes from and
/// its phase. The old fields do not change, a cancelled or rejected request is not listed, another supplier's requests are never
/// here, and no property name, address, contact or note ever reaches the answer. Today is Thursday 8 October 2026 (the scenario).
/// </summary>
public class SupplierCalendarTitlesTests
{
    private static readonly DateOnly From = new(2026, 10, 8);
    private static readonly DateOnly To = new(2026, 10, 14);

    /// <summary>What must never be in what the calendar returns for a request of a host or of a customer, before or after the take.</summary>
    private static readonly string[] NeverInTheCalendar =
    [
        ServiceRequestScenario.PropertyName,
        ServiceRequestScenario.PropertyAddress,
        ServiceRequestScenario.HostNotes,
        ShowcaseScenario.CustomerEmail,
        "3331234567",
        "Segretissima",
        "Piano 3",
        "Citofono",
        "cassetta",
        "host@test.com",
    ];

    [Fact]
    public async Task Calendar_ARequestOfAHost_ShowsTheServiceTheHostTheComuneTheSourceAndThePhase_AndTheOldFieldsAsBefore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        var item = Assert.Single((await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests);

        // The titles.
        Assert.Equal(ServiceRequestScenario.ServiceName, item.ServiceName);
        Assert.Equal("Casa Rossi", item.ClientLabel); // the host's org, as the inbox says it before the take
        Assert.Equal(ServiceRequestScenario.Comune, item.Comune);
        Assert.Equal("casazen", item.Source);
        Assert.Equal("to_answer", item.Phase);
        // What was there before.
        Assert.Equal(request.Id, item.Id);
        Assert.Equal(new DateOnly(2026, 10, 9), item.Date);
        Assert.Equal(ServiceRequestScenario.FridayAt10, item.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), item.EndUtc);
        Assert.Equal(ServiceRequestStatus.Richiesto, item.Status);
        Assert.Equal("cleaning", item.Category);
    }

    [Fact]
    public async Task Calendar_ARequestForACategoryOnly_HasNoServiceName_TheCategoryIsInItsOwnField()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.RequestAsync(withService: false);

        var item = Assert.Single((await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests);

        Assert.Null(item.ServiceName);
        Assert.Equal("cleaning", item.Category);
        Assert.Equal("Casa Rossi", item.ClientLabel);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, false, "to_answer")]
    [InlineData(ServiceRequestStatus.Richiesto, true, "proposal_pending")]
    [InlineData(ServiceRequestStatus.PresoInCarico, false, "scheduled")]
    [InlineData(ServiceRequestStatus.PresoInCarico, true, "scheduled")] // a proposal only matters while the request waits
    [InlineData(ServiceRequestStatus.InCorso, false, "in_progress")]
    [InlineData(ServiceRequestStatus.Completato, false, "completed")]
    [InlineData(ServiceRequestStatus.Pagato, false, "paid")]
    public async Task Calendar_ThePhaseOfARequest_IsDerivedByTheServerFromItsStatusAndAProposalThatWaits(
        ServiceRequestStatus status,
        bool proposal,
        string phase)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedAsync(status, request =>
        {
            request.ScheduledStartUtc = ServiceRequestScenario.FridayAt10;
            request.ScheduledEndUtc = ServiceRequestScenario.FridayAt10.AddHours(2);
            if (!proposal)
                return;

            request.ProposedStartUtc = ServiceRequestScenario.FridayAt14;
            request.ProposedEndUtc = ServiceRequestScenario.FridayAt14.AddHours(2);
            request.ProposedAt = ServiceRequestScenario.Instant.UtcDateTime;
        });

        var item = Assert.Single((await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests);

        Assert.Equal(phase, item.Phase);
        Assert.Equal(status, item.Status);
    }

    [Fact]
    public async Task Calendar_APrivateCustomerOfTheShowcase_IsNomeCUntilTheTake_TheFullNameAfter()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();

        var before = Assert.Single((await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests);

        Assert.Equal("Mario R.", before.ClientLabel);
        Assert.Equal(ShowcaseScenario.City, before.Comune);
        Assert.Equal("showcase", before.Source);
        Assert.Equal("to_answer", before.Phase);
        Assert.Equal(ServiceRequestScenario.ServiceName, before.ServiceName);
        Assert.DoesNotContain("Rossi", JsonSerializer.Serialize(before));
        AssertNothingPrivate(before);

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        var after = Assert.Single((await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests);

        Assert.Equal("Mario Rossi", after.ClientLabel); // the name, once it is the supplier's job: nothing else of the customer
        Assert.Equal("scheduled", after.Phase);
        AssertNothingPrivate(after);
    }

    [Fact]
    public async Task Calendar_NothingOfThePropertyTheAddressTheNotesOrAContact_IsInTheAnswer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var host = await s.RequestAsync(start: ServiceRequestScenario.FridayAt14);
        var (showcase, _) = await s.BookedAsync();
        await s.Service.TakeAsync(host.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        await s.Service.TakeAsync(showcase.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var calendar = await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To);

        Assert.Equal(2, calendar.Requests.Count);
        foreach (var item in calendar.Requests)
            AssertNothingPrivate(item);
    }

    [Fact]
    public async Task Calendar_ARejectedOrCancelledRequest_IsNotListed_TheDecisionOfSp03b()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var timed = (Action<Casazen.Core.Entities.ServiceRequest>)(request =>
        {
            request.ScheduledStartUtc = ServiceRequestScenario.FridayAt10;
            request.ScheduledEndUtc = ServiceRequestScenario.FridayAt10.AddHours(2);
        });
        await s.SeedAsync(ServiceRequestStatus.Rifiutato, timed);
        await s.SeedAsync(ServiceRequestStatus.Annullato, timed);
        var kept = await s.SeedAsync(ServiceRequestStatus.PresoInCarico, timed);

        var requests = (await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests;

        Assert.Equal([kept.Id], requests.Select(request => request.Id));
    }

    [Fact]
    public async Task Calendar_TheRequestsOfAnotherSupplier_AreNeverHere()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto, request =>
        {
            request.ScheduledStartUtc = ServiceRequestScenario.FridayAt10;
            request.ScheduledEndUtc = ServiceRequestScenario.FridayAt10.AddHours(2);
        }, other);

        var mine = (await s.Agenda.GetCalendarAsync(s.SupplierOrgId, From, To)).Requests;
        var theirs = (await s.Agenda.GetCalendarAsync(other, From, To)).Requests;

        Assert.Empty(mine);
        Assert.Single(theirs);
    }

    [Fact]
    public async Task ListForAgendaAsync_TheLightReadOfThePlanner_HasNoTitles_ButCarriesTheService()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.RequestAsync();

        var item = Assert.Single(await s.Reader.ListForAgendaAsync(s.SupplierOrgId, From, To));

        Assert.Equal(s.ListingId, item.ServiceListingId);
        Assert.Null(item.ServiceName);
        Assert.Null(item.ClientLabel);
        Assert.Null(item.Comune);
    }

    [Fact]
    public void CalendarRowsQuery_OnTheNpgsqlProvider_IsScopedByTheSupplier_AndReadsNoAddressContactNoteOrGuest()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres")
                .Options);

        var sql = SupplierServiceRequestReader
            .CalendarRowsOf(db, Guid.NewGuid(), new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc))
            .ToQueryString();

        // The supplier is an explicit predicate (a request has two parties and no tenant filter); the titles come from the columns
        // they need and from no other.
        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Contains("\"ServiceNameSnapshot\"", sql);
        Assert.Contains("\"City\"", sql);
        Assert.Contains("\"FullName\"", sql);
        Assert.Contains("\"DisplayName\"", sql);
        foreach (var column in new[]
                 {
                     "\"Address\"", "\"LocationAddress\"", "\"LocationFloor\"", "\"LocationAccessNotes\"", "\"Notes\"", "\"GuestId\"",
                     "\"FirstName\"", "\"LastName\"", "\"Email\"", "\"Phone\"", "\"ContactEmail\"", "\"PostalCode\"", "\"LocationPostalCode\"",
                 })
        {
            Assert.DoesNotContain(column, sql, StringComparison.Ordinal);
        }
    }

    private static void AssertNothingPrivate(SupplierAgendaRequest item)
    {
        var text = JsonSerializer.Serialize(item);
        foreach (var secret in NeverInTheCalendar)
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }
}
