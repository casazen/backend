using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: what the supplier's console reads of a request (<see cref="Casazen.Infrastructure.Services.SupplierServiceRequestReader"/>):
/// the time, the price, the client, the proposal, the cancellation and the photos; and, until the supplier takes the request, only
/// the comune, the postal code, the day and time and the price (decision D9): never the name of the property, the host's notes,
/// the street address or the contact, and never the guest.
/// </summary>
public class SupplierServiceRequestReaderTests
{
    private static readonly SupplierInboxQuery Everything = new([], null, null, 1, 100);

    // ─── Privacy before the take (D9) ───

    [Fact]
    public async Task ListAsync_BeforeTheTake_ShowsComunePostalCodeTimeAndPriceButNothingThatIdentifiesTheHome()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var (items, total) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var view = Assert.Single(items);
        Assert.Equal(1, total);
        Assert.Equal(request.Id, view.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.Equal(ServiceRequestScenario.Comune, view.Location.City);
        Assert.Equal("00100", view.Location.PostalCode);
        Assert.Equal(ServiceRequestScenario.FridayAt10, view.Schedule.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(120), view.Schedule.EndUtc);
        Assert.Equal(new DateOnly(2026, 10, 9), view.ScheduledFor);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, view.Price.AmountCents);
        // What identifies the home or the host's words waits for the take.
        Assert.Null(view.Location.PropertyName);
        Assert.Null(view.Location.Address);
        Assert.Null(view.Notes);
        Assert.False(view.ContactDisclosed);
        Assert.Null(view.HostContact);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico, true)]
    [InlineData(ServiceRequestStatus.InCorso, true)]
    [InlineData(ServiceRequestStatus.Completato, true)]
    [InlineData(ServiceRequestStatus.Pagato, true)]
    [InlineData(ServiceRequestStatus.Richiesto, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public async Task ListAsync_ThePropertyTheNotesTheAddressAndTheContactComeOnlyWithATakenRequest(ServiceRequestStatus status, bool disclosed)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedAsync(status, r => r.TakenAt = status is ServiceRequestStatus.Richiesto or ServiceRequestStatus.Rifiutato ? null : r.CreatedAt);

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var view = Assert.Single(items);
        Assert.Equal(disclosed, view.ContactDisclosed);
        Assert.Equal(disclosed ? ServiceRequestScenario.PropertyName : null, view.Location.PropertyName);
        Assert.Equal(disclosed ? ServiceRequestScenario.PropertyAddress : null, view.Location.Address);
        Assert.Equal(disclosed ? ServiceRequestScenario.HostNotes : null, view.Notes);
        Assert.Equal(disclosed, view.HostContact is not null);
        Assert.Equal(disclosed, SupplierJobDisclosure.IsDisclosed(status));
    }

    [Fact]
    public async Task GetAsync_ACancelledRequestThatWasNeverTaken_StillHidesThePropertyAndTheNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Ospiti partiti prima");

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(view);
        Assert.Null(view.Location.PropertyName);
        Assert.Null(view.Notes);
        Assert.Null(view.Location.Address);
    }

    [Fact]
    public async Task ListAsync_NeverReadsTheGuestOfTheStay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var guest = new Guest { OrgId = s.HostOrgId, FirstName = "Romeo", LastName = "Montecchi", Email = "romeo@example.com", PhoneNumber = "+39 347 9998887" };
        s.Db.Guests.Add(guest);
        var booking = await s.Db.Bookings.FindAsync(s.BookingId);
        booking!.GuestId = guest.Id;
        await s.Db.SaveChangesAsync();
        await s.TakenAsync();

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);
        var one = await s.Reader.GetAsync(items.Single().Id, s.SupplierOrgId);

        var text = System.Text.Json.JsonSerializer.Serialize(new { items, one });
        Assert.DoesNotContain("Montecchi", text);
        Assert.DoesNotContain("romeo@example.com", text);
        Assert.DoesNotContain("9998887", text);
    }

    // ─── What the console shows ───

    [Fact]
    public async Task ListAsync_TheClientIsTheHostOrgWithItsDisplayName_AlsoBeforeTheTake()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.RequestAsync();

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var view = Assert.Single(items);
        Assert.Equal(s.HostOrgId, view.Client.Id);
        Assert.Equal("Casa Rossi", view.Client.Name);
        Assert.Equal(SupplierRequestSources.CasaZen, view.Source);
    }

    [Fact]
    public async Task ListAsync_TheDayOfARequest_IsTheRomeDayOfItsTimeElseTheCheckOutDayOfTheStay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // 23:30 UTC on the 9th is 01:30 on the 10th in Rome.
        var late = await s.SeedAsync(
            ServiceRequestStatus.PresoInCarico,
            r =>
            {
                r.TakenAt = r.CreatedAt;
                r.ScheduledStartUtc = new DateTime(2026, 10, 9, 23, 30, 0, DateTimeKind.Utc);
                r.ScheduledEndUtc = new DateTime(2026, 10, 10, 1, 30, 0, DateTimeKind.Utc);
            });
        var toAgree = await s.SeedAsync(ServiceRequestStatus.Richiesto);
        var longRent = await s.SeedAsync(ServiceRequestStatus.Richiesto, r =>
        {
            r.RentalContext = ServiceRequestRentalContext.LongRent;
            r.BookingId = null;
        });

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        Assert.Equal(new DateOnly(2026, 10, 10), items.Single(view => view.Id == late.Id).ScheduledFor);
        var turnover = items.Single(view => view.Id == toAgree.Id);
        Assert.Equal(new DateOnly(2026, 10, 12), turnover.ScheduledFor); // the check-out of the stay
        Assert.Equal(new DateOnly(2026, 10, 12), turnover.Stay!.CheckOut);
        Assert.Null(items.Single(view => view.Id == longRent.Id).ScheduledFor); // a property, no stay, no day
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(6000, null, null, 6000)]
    [InlineData(6000, 7500, null, 7500)]
    [InlineData(6000, 7500, 9000, 9000)]
    [InlineData(null, null, 9000, 9000)]
    public async Task ListAsync_ThePriceIsTheFinalAmountElseTheQuoteElseTheEstimate(
        int? estimate, int? quote, int? final, int? expectedAmount)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedAsync(ServiceRequestStatus.Completato, r =>
        {
            r.EstimatedAmountCents = estimate;
            r.QuotedAmountCents = quote;
            r.FinalAmountCents = final;
            r.FinalAmountNeedsConfirmation = true;
            r.PriceLinesJson = ServiceRequestJson.Serialize([new ServiceRequestPriceLine("base", "Pulizia", final ?? 0)]);
        });

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var price = Assert.Single(items).Price;
        Assert.Equal(expectedAmount, price.AmountCents);
        Assert.Equal(estimate, price.EstimatedAmountCents);
        Assert.Equal(quote, price.QuotedAmountCents);
        Assert.Equal(final, price.FinalAmountCents);
        Assert.True(price.FinalAmountNeedsConfirmation);
        Assert.Single(price.Lines);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public async Task ListAsync_TheDeadlineToAnswerIsShownOnlyWhileTheRequestIsNew(ServiceRequestStatus status, bool shown)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var due = ServiceRequestScenario.Instant.UtcDateTime.AddHours(2);
        await s.SeedAsync(status, r =>
        {
            r.ResponseDueAt = due;
            r.TakenAt = status == ServiceRequestStatus.PresoInCarico ? r.CreatedAt : null;
        });

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        Assert.Equal(shown ? due : null, Assert.Single(items).Schedule.RespondBy);
    }

    [Fact]
    public async Task ListAsync_TheProposalOfTheSupplier_IsMapped()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, Message: "Solo il pomeriggio"));

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var proposal = Assert.Single(items).Proposal;
        Assert.NotNull(proposal);
        Assert.Equal(ServiceRequestScenario.FridayAt14, proposal.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14.AddMinutes(120), proposal.EndUtc);
        Assert.Equal("Solo il pomeriggio", proposal.Message);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, proposal.ProposedAt);
    }

    [Fact]
    public async Task ListAsync_ACancelledRequest_SaysByWhomWhenAndWhy_AndFallsBackToTheHostWhenNothingWasRecorded()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var cancelledAt = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
        var recorded = await s.SeedAsync(ServiceRequestStatus.Annullato, r =>
        {
            r.CancelledAt = cancelledAt;
            r.CancelledBy = ServiceRequestActorParty.System;
            r.CancellationReason = ServiceRequestCancellationReasons.NoResponse;
        });
        var legacy = await s.SeedAsync(ServiceRequestStatus.Annullato, r => r.UpdatedAt = cancelledAt.AddHours(1));

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var byCasaZen = items.Single(view => view.Id == recorded.Id).Cancellation;
        Assert.NotNull(byCasaZen);
        Assert.Equal(ServiceRequestActorParty.System, byCasaZen.By);
        Assert.Equal(cancelledAt, byCasaZen.At);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, byCasaZen.Reason);
        var fallback = items.Single(view => view.Id == legacy.Id).Cancellation;
        Assert.NotNull(fallback);
        Assert.Equal(ServiceRequestActorParty.Host, fallback.By);
        Assert.Equal(cancelledAt.AddHours(1), fallback.At);
        Assert.Null(fallback.Reason);
        Assert.All(items.Where(view => view.Status != ServiceRequestStatus.Annullato), view => Assert.Null(view.Cancellation));
    }

    [Fact]
    public async Task ListAsync_ThePhotosOfTheWork_AreListedWithTheirIds()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var photo = new ServiceRequestPhoto(Guid.NewGuid(), "service-requests/x/photos/y.jpg", ServiceRequestScenario.Instant.UtcDateTime);
        await s.SeedAsync(ServiceRequestStatus.InCorso, r =>
        {
            r.TakenAt = r.CreatedAt;
            r.WorkPhotosJson = ServiceRequestJson.Serialize([photo]);
            r.CompletionNotes = "Quasi finito";
        });

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var view = Assert.Single(items);
        Assert.Equal([photo], view.WorkPhotos);
        Assert.Equal("Quasi finito", view.CompletionNotes);
    }

    [Fact]
    public async Task GetAsync_TheHistory_ShowsTheStartTheCancellationAndWhoTookTheRequest()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var started = await s.StartedAsync();
        await s.Service.CancelAsHostAsync(started.Id, s.HostOrgId, "Lavoro non necessario");

        var view = await s.Reader.GetAsync(started.Id, s.SupplierOrgId);

        Assert.NotNull(view?.History);
        Assert.Equal(
            new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso, ServiceRequestStatus.Annullato },
            view.History.Select(step => step.Status));
        Assert.Equal("Mario Fornitore", view.History[1].ActorName);
        Assert.Equal(ServiceRequestActorParty.Host, view.History[^1].Actor);
        Assert.Equal("Lavoro non necessario", view.History[^1].Reason);
    }

    // ─── Order and paging ───

    [Fact]
    public async Task ListAsync_SortedByUrgency_PutsTheEarliestDeadlineFirstAndTheOnesWithoutLast()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var now = ServiceRequestScenario.Instant.UtcDateTime;
        var noDeadline = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = null);
        var later = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = now.AddHours(3));
        var sooner = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = now.AddMinutes(20));

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything with { Sort = SupplierInboxSort.Urgency });

        Assert.Equal(new[] { sooner.Id, later.Id, noDeadline.Id }, items.Select(view => view.Id));
    }

    [Fact]
    public async Task ListAsync_SortedByWorkTime_PutsTheNextJobFirstAndTheOnesToAgreeLast()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var toAgree = await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);
        var later = await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => Timed(r, "2026-10-15T08:00:00Z"));
        var sooner = await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => Timed(r, "2026-10-09T08:00:00Z"));

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything with { Sort = SupplierInboxSort.WorkTime });

        // The request without a time of its own is a turnover on the check-out day of its stay (12 October).
        Assert.Equal(new[] { sooner.Id, toAgree.Id, later.Id }, items.Select(view => view.Id));
    }

    [Fact]
    public async Task ListAsync_SortedByActivity_PutsTheMostRecentFirst_AndPagesWithTheTotal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
            created.Add((await s.SeedAsync(ServiceRequestStatus.Richiesto, createdAt: ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-i))).Id);

        var (page1, total) = await s.Reader.ListAsync(s.SupplierOrgId, Everything with { Page = 1, PageSize = 2 });
        var (page3, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything with { Page = 3, PageSize = 2 });

        Assert.Equal(5, total);
        Assert.Equal(created.Take(2), page1.Select(view => view.Id));
        Assert.Equal(created.Skip(4), page3.Select(view => view.Id));
    }

    private static void Timed(ServiceRequest request, string startUtc)
    {
        var start = DateTime.Parse(startUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        request.TakenAt = request.CreatedAt;
        request.ScheduledStartUtc = start;
        request.ScheduledEndUtc = start.AddHours(2);
    }
}
