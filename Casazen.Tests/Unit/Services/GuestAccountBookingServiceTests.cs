using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The bookings of the guest who has an account (the mobile app): the direct bookings, of any org, whose guest has the account's email.
/// The real service over EF InMemory with the tenant filter ON for another org or for none (a signed-in guest's request), as it runs: the
/// tenant filter must not hide what the guest booked, and the predicates, not the filter, must keep every other person's booking out.
/// </summary>
public class GuestAccountBookingServiceTests
{
    // Monday 15 June 2026, 12:00 in Rome (10:00 UTC, summer time): "today" in Rome is 2026-06-15.
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The address as the token carries it: capitals and spaces around; the bookings hold it in lower case.</summary>
    private const string AccountEmail = "  Anna.Guest@Example.COM ";

    private const string GuestEmail = "anna.guest@example.com";

    private readonly string _database = $"guest-account-service-{Guid.NewGuid():N}";
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly DateOnly _today = DateOnly.FromDateTime(Today);

    // ─── The list: scope, order, paging ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_Upcoming_AcrossTwoOrgs_ByCheckInAscending_OnlyTheAccountsOwn()
    {
        var orgA = await AddOrgAsync("Villa Alfa");
        var orgB = await AddOrgAsync("Casa Beta");
        var houseA = await AddPropertyAsync(orgA, "Alfa Uno");
        var houseB = await AddPropertyAsync(orgB, "Beta Uno");
        var late = await AddBookingAsync(orgA, houseA, GuestEmail, checkInInDays: 30);
        var soon = await AddBookingAsync(orgB, houseB, GuestEmail, checkInInDays: 3);
        var middle = await AddBookingAsync(orgA, houseA, GuestEmail, checkInInDays: 10);
        // Somebody else's, in the same org and in the other one: never listed.
        await AddBookingAsync(orgA, houseA, "other.person@example.com", checkInInDays: 5);
        await AddBookingAsync(orgB, houseB, "anna.guest2@example.com", checkInInDays: 6);

        var page = await ListAsync(AccountEmail, GuestAccountBookingScope.Upcoming, 1, 20, tenantOrg: orgA.Id);

        Assert.Equal([soon.Id, middle.Id, late.Id], page.Items.Select(i => i.Id));
        Assert.Equal(3, page.Total);
        Assert.Equal((1, 20), (page.Page, page.PageSize));
        Assert.Equal([orgB.Slug, orgA.Slug, orgA.Slug], page.Items.Select(i => i.OrgSlug));
    }

    [Fact]
    public async Task ListAsync_Past_ByCheckInDescending_AndACheckOutTodayIsStillUpcoming()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Passata");
        var yesterdayOut = await AddBookingAsync(org, house, GuestEmail, checkInInDays: -4, nights: 3, status: BookingStatus.CheckedOut);
        var longAgo = await AddBookingAsync(org, house, GuestEmail, checkInInDays: -60, nights: 3, status: BookingStatus.CheckedOut);
        var outToday = await AddBookingAsync(org, house, GuestEmail, checkInInDays: -2, nights: 2, status: BookingStatus.CheckedIn);
        var olderStill = await AddBookingAsync(org, house, GuestEmail, checkInInDays: -200, nights: 5, status: BookingStatus.CheckedOut);

        var past = await ListAsync(GuestEmail, GuestAccountBookingScope.Past, 1, 20);
        var upcoming = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 20);

        Assert.Equal([yesterdayOut.Id, longAgo.Id, olderStill.Id], past.Items.Select(i => i.Id));
        Assert.Equal(3, past.Total);
        Assert.All(past.Items, item => Assert.Equal(GuestBookingStatus.StayCompleted, item.Status));
        // Check-out today (Europe/Rome): the stay is not over yet.
        Assert.Equal([outToday.Id], upcoming.Items.Select(i => i.Id));
        Assert.Equal(GuestBookingStatus.StayInProgress, Assert.Single(upcoming.Items).Status);
    }

    [Fact]
    public async Task ListAsync_TheRomeDayDecidesTheScope_NotTheUtcDay()
    {
        // 00:30 on 16 June in Rome is still 22:30 UTC on 15 June: today is the 16th in Rome.
        _clock.SetUtcNow(new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero));
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Mezzanotte");
        var leftOnThe15th = await AddBookingAsync(org, house, GuestEmail, checkInInDays: -3, nights: 3, status: BookingStatus.CheckedOut);
        var leavesOnThe16th = await AddBookingAsync(org, house, GuestEmail, checkInInDays: -2, nights: 3, status: BookingStatus.CheckedIn);

        var past = await ListAsync(GuestEmail, GuestAccountBookingScope.Past, 1, 20);
        var upcoming = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 20);

        Assert.Equal([leftOnThe15th.Id], past.Items.Select(i => i.Id));
        Assert.Equal([leavesOnThe16th.Id], upcoming.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ListAsync_Paging_PageSizeIsClampedAndTheTotalCountsTheWholeScope()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Pagine");
        var ids = new List<Guid>();
        for (var i = 1; i <= 5; i++)
            ids.Add((await AddBookingAsync(org, house, GuestEmail, checkInInDays: i * 7)).Id);

        var first = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 2);
        var second = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 2, 2);
        var last = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 3, 2);
        var beyond = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 4, 2);
        var belowOne = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 0, 0);
        var huge = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, int.MaxValue, 1000);

        Assert.Equal(ids.Take(2), first.Items.Select(i => i.Id));
        Assert.Equal(ids.Skip(2).Take(2), second.Items.Select(i => i.Id));
        Assert.Equal(ids.Skip(4), last.Items.Select(i => i.Id));
        Assert.All(new[] { first, second, last, beyond }, page => Assert.Equal(5, page.Total));
        Assert.Empty(beyond.Items);
        // A page below 1 counts as 1 and a size below 1 as 1; a size above the cap as the cap.
        Assert.Equal((1, 1), (belowOne.Page, belowOne.PageSize));
        Assert.Equal([ids[0]], belowOne.Items.Select(i => i.Id));
        Assert.Equal(GuestAccountBookings.MaxPageSize, huge.PageSize);
        Assert.Empty(huge.Items);
        Assert.Equal(5, huge.Total);
    }

    // ─── The list: which bookings count ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_DeadEndsAreNotListedOrCounted_EveryOtherStateIs()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Stati");
        var recent = Now.UtcDateTime.AddMinutes(-5);

        // The states a guest can still act on or wants to read about.
        var expected = new Dictionary<GuestBookingStatus, Guid>
        {
            [GuestBookingStatus.Confirmed] = (await AddBookingAsync(org, house, GuestEmail, 10)).Id,
            // A fresh hold, nothing paid yet.
            [GuestBookingStatus.AwaitingPayment] = (await AddBookingAsync(org, house, GuestEmail, 11, shape: b =>
            {
                b.Status = BookingStatus.Pending;
                b.CreatedAt = recent;
            })).Id,
            // The last attempt failed and the hold is still valid.
            [GuestBookingStatus.PaymentFailed] = (await AddBookingAsync(
                org,
                house,
                GuestEmail,
                12,
                shape: b =>
                {
                    b.Status = BookingStatus.Pending;
                    b.CreatedAt = recent;
                },
                payment: p =>
                {
                    p.Status = PaymentStatus.Failed;
                    p.StripePaymentIntentId = "pi_failed";
                })).Id,
            // A SEPA debit in flight: the dates stay taken even past the hold.
            [GuestBookingStatus.PaymentProcessing] = (await AddBookingAsync(
                org,
                house,
                GuestEmail,
                13,
                shape: b =>
                {
                    b.Status = BookingStatus.Pending;
                    b.CreatedAt = Now.UtcDateTime.AddMinutes(-90);
                },
                payment: p =>
                {
                    p.Status = PaymentStatus.Processing;
                    p.StripePaymentIntentId = "pi_sepa";
                })).Id,
            // "Pay at the property": email confirmed, the host has not answered; and email not confirmed yet.
            [GuestBookingStatus.AwaitingHostApproval] = (await AddBookingAsync(org, house, GuestEmail, 14, shape: b =>
            {
                b.Status = BookingStatus.Pending;
                b.PaymentOption = PaymentOption.OnSite;
                b.GuestEmailVerifiedAt = Now.UtcDateTime.AddHours(-1);
                b.RequestExpiresAt = Now.UtcDateTime.AddHours(20);
            })).Id,
            [GuestBookingStatus.AwaitingGuestEmail] = (await AddBookingAsync(org, house, GuestEmail, 15, shape: b =>
            {
                b.Status = BookingStatus.Pending;
                b.PaymentOption = PaymentOption.OnSite;
                b.RequestExpiresAt = Now.UtcDateTime.AddMinutes(20);
            })).Id,
            [GuestBookingStatus.Declined] = (await AddBookingAsync(org, house, GuestEmail, 16, shape: b =>
            {
                b.Status = BookingStatus.Cancelled;
                b.PaymentOption = PaymentOption.OnSite;
                b.CancellationReason = BookingCancellationReason.OnSiteRequestDeclined;
            })).Id,
            [GuestBookingStatus.Cancelled] = (await AddBookingAsync(org, house, GuestEmail, 17, shape: b =>
            {
                b.Status = BookingStatus.Cancelled;
                b.CancellationReason = BookingCancellationReason.GuestRequested;
            })).Id,
        };

        // The dead ends: a hold that lapsed (its PaymentIntent exists, nothing was paid), the same cancelled by the job, a request nobody
        // answered, and a payment that came when the dates were gone.
        var lapsedHold = await AddBookingAsync(
            org,
            house,
            GuestEmail,
            18,
            shape: b =>
            {
                b.Status = BookingStatus.Pending;
                b.CreatedAt = Now.UtcDateTime.AddMinutes(-120);
            },
            payment: p =>
            {
                p.Status = PaymentStatus.Pending;
                p.StripePaymentIntentId = "pi_lapsed";
            });
        var cancelledAsExpired = await AddBookingAsync(org, house, GuestEmail, 19, shape: b =>
        {
            b.Status = BookingStatus.Cancelled;
            b.CancellationReason = BookingCancellationReason.CheckoutHoldExpired;
        });
        var notAnswered = await AddBookingAsync(org, house, GuestEmail, 20, shape: b =>
        {
            b.Status = BookingStatus.Cancelled;
            b.PaymentOption = PaymentOption.OnSite;
            b.CancellationReason = BookingCancellationReason.OnSiteRequestExpired;
        });
        var datesGone = await AddBookingAsync(org, house, GuestEmail, 21, shape: b =>
        {
            b.Status = BookingStatus.Cancelled;
            b.CancellationReason = BookingCancellationReason.DatesUnavailableAtPayment;
        });

        var page = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 50);

        Assert.Equal(expected.Count, page.Total);
        Assert.Equal(expected.OrderBy(e => e.Key), page.Items.ToDictionary(i => i.Status, i => i.Id).OrderBy(e => e.Key));
        var hidden = new[] { lapsedHold.Id, cancelledAsExpired.Id, notAnswered.Id, datesGone.Id };
        Assert.DoesNotContain(page.Items, i => hidden.Contains(i.Id));
        Assert.DoesNotContain(page.Items, i => i.Status is GuestBookingStatus.Expired or GuestBookingStatus.DatesUnavailable);
    }

    [Fact]
    public async Task ListAsync_NeverListsAnotherChannelAnInactiveOrgOrAHouseOfAnotherPersonsEmail()
    {
        var org = await AddOrgAsync();
        var gone = await AddOrgAsync(active: false);
        var house = await AddPropertyAsync(org, "Casa Canali");
        var goneHouse = await AddPropertyAsync(gone, "Casa Spenta");
        var own = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 8);
        // Same address, but not a booking of the public checkout: the host's own entries and the channels' stays.
        foreach (var source in new[] { BookingSource.Manual, BookingSource.Airbnb, BookingSource.BookingCom, BookingSource.Local })
            await AddBookingAsync(org, house, GuestEmail, checkInInDays: 9, source: source);
        // Same address, but the org of the site is switched off.
        await AddBookingAsync(gone, goneHouse, GuestEmail, checkInInDays: 12);

        var page = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 20);

        Assert.Equal([own.Id], page.Items.Select(i => i.Id));
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task ListAsync_AStayAtAHouseTheHostHasSinceDeleted_IsStillTheGuests()
    {
        var org = await AddOrgAsync();
        var deleted = await AddPropertyAsync(org, "Casa Venduta", deleted: true);
        var stay = await AddBookingAsync(org, deleted, GuestEmail, checkInInDays: -40, nights: 4, status: BookingStatus.CheckedOut);

        var past = await ListAsync(GuestEmail, GuestAccountBookingScope.Past, 1, 20, tenantOrg: org.Id);

        var item = Assert.Single(past.Items);
        Assert.Equal(stay.Id, item.Id);
        Assert.Equal("Casa Venduta", item.PropertyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task ListAsync_TheTenantFilterOfTheRequestDoesNotHideTheBookings(bool? ownHostOrg)
    {
        var org = await AddOrgAsync();
        var otherOrg = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Filtro");
        var stay = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 20);

        // A guest with no org at all (the tenant is null), and a guest who is also a host of an org that is not the site's.
        var page = await ListAsync(
            GuestEmail,
            GuestAccountBookingScope.Upcoming,
            1,
            20,
            tenantOrg: ownHostOrg is true ? otherOrg.Id : null);

        Assert.Equal([stay.Id], page.Items.Select(i => i.Id));
    }

    // ─── The list item ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_Item_CarriesTheFieldsOfTheContract()
    {
        var org = await AddOrgAsync("  Villa dei Pini  ");
        var house = await AddPropertyAsync(
            org,
            "Casa dei Pini",
            city: "Cernobbio",
            currency: "chf",
            photos: ["/uploads/properties/old/1.jpg", "  ", "http://insecure.example.com/2.jpg", " https://cdn.example.com/3.jpg ", "https://cdn.example.com/4.jpg"]);
        var stay = await AddBookingAsync(
            org,
            house,
            GuestEmail,
            checkInInDays: 12,
            nights: 4,
            adults: 2,
            children: 1,
            paid: 250m,
            refunded: 50m,
            shape: b =>
            {
                b.TotalPrice = 480.50m;
                b.PaymentOption = PaymentOption.OnCancellationDeadline;
            });

        var page = await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 20);

        var item = Assert.Single(page.Items);
        Assert.Equal(stay.Id, item.Id);
        Assert.Equal(org.Slug, item.OrgSlug);
        Assert.Equal(BookingCodes.Format(stay.BookingCode), item.BookingCode);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", item.BookingCode);
        Assert.Equal(GuestBookingStatus.Confirmed, item.Status);
        Assert.Equal(house.Id, item.PropertyId);
        Assert.Equal(house.Slug, item.PropertySlug);
        Assert.Equal("Casa dei Pini", item.PropertyName);
        Assert.Equal("Cernobbio", item.City);
        // The first photo that is a full https address: not an old uploads path, not a blank, not http.
        Assert.Equal("https://cdn.example.com/3.jpg", item.PhotoUrl);
        Assert.Equal(_today.AddDays(12), item.CheckInDate);
        Assert.Equal(_today.AddDays(16), item.CheckOutDate);
        Assert.Equal(4, item.Nights);
        Assert.Equal((2, 1), (item.Adults, item.Children));
        Assert.Equal("CHF", item.Currency);
        Assert.Equal(480.50m, item.TotalPrice);
        Assert.Equal((250m, 50m), (item.PaidAmount, item.RefundedAmount));
        Assert.Equal(PaymentOption.OnCancellationDeadline, item.PaymentOption);
        Assert.Equal("Villa dei Pini", item.HostName);
    }

    [Fact]
    public async Task ListAsync_Item_WithoutPhotosOrHostName_HasNullsAndAnEmptyCity()
    {
        var org = await AddOrgAsync(displayName: "   ");
        var house = await AddPropertyAsync(org, "Casa Spoglia", city: string.Empty, photos: ["/uploads/properties/old/1.jpg"]);
        await AddBookingAsync(org, house, GuestEmail, checkInInDays: 2);

        var item = Assert.Single((await ListAsync(GuestEmail, GuestAccountBookingScope.Upcoming, 1, 20)).Items);

        Assert.Null(item.PhotoUrl);
        Assert.Null(item.HostName);
        Assert.Equal(string.Empty, item.City);
        Assert.Equal("EUR", item.Currency);
        Assert.Equal((0m, 0m), (item.PaidAmount, item.RefundedAmount));
    }

    // ─── One booking ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_OwnBooking_ReturnsTheViewOfTheAnonymousLookupWithTheIdAndTheSlug()
    {
        var org = await AddOrgAsync("Villa Dettaglio", contactEmail: "host@example.com");
        var house = await AddPropertyAsync(org, "Casa Dettaglio");
        var stay = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 40, paid: 362m);

        var detail = await GetAsync(AccountEmail, stay.Id, tenantOrg: Guid.NewGuid());

        Assert.Equal((stay.Id, org.Slug), (detail.Id, detail.OrgSlug));
        var view = detail.View;
        Assert.Equal(BookingCodes.Format(stay.BookingCode), view.BookingCode);
        Assert.Equal(GuestBookingStatus.Confirmed, view.Status);
        Assert.Equal("Casa Dettaglio", view.PropertyName);
        Assert.Equal(362m, view.PaidAmount);
        Assert.Equal(new GuestBookingHostContact("Villa Dettaglio", "host@example.com"), view.Host);
        // Arrival in 40 days: the link of the online check-in is emailed three days before.
        Assert.Equal(GuestCheckInAccessStatus.NotYetOpen, view.CheckIn.Status);
        Assert.Equal(_today.AddDays(37), view.CheckIn.OpensOn);
    }

    [Fact]
    public async Task GetAsync_TheOnlineCheckInIsReadByTheBookingNotByTheTenantOfTheRequest()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Check-in");
        var stay = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 1);

        var before = await GetAsync(GuestEmail, stay.Id, tenantOrg: null);
        await using (var db = NewDb())
        {
            db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                BookingId = stay.Id,
                OrgId = org.Id,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(5),
                Status = GuestCheckInSessionStatus.Completo,
            });
            await db.SaveChangesAsync();
        }

        // The guest has no org (or one of their own): the session of the booking's org is still the booking's.
        var withoutOrg = await GetAsync(GuestEmail, stay.Id, tenantOrg: null);
        var otherOrg = await GetAsync(GuestEmail, stay.Id, tenantOrg: Guid.NewGuid());

        Assert.Equal(GuestCheckInAccessStatus.Open, before.View.CheckIn.Status);
        Assert.Equal(GuestCheckInAccessStatus.Completed, withoutOrg.View.CheckIn.Status);
        Assert.Equal(GuestCheckInAccessStatus.Completed, otherOrg.View.CheckIn.Status);
    }

    [Fact]
    public async Task GetAsync_ADeadEndStateOfTheAccountsOwnBooking_IsStillReadable()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Scaduta");
        var lapsed = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 18, shape: b =>
        {
            b.Status = BookingStatus.Cancelled;
            b.CancellationReason = BookingCancellationReason.CheckoutHoldExpired;
        });

        var detail = await GetAsync(GuestEmail, lapsed.Id);

        Assert.Equal(GuestBookingStatus.Expired, detail.View.Status);
    }

    [Fact]
    public async Task GetAsync_AnotherPersonsBookingOrAnUnknownId_TheSameNotFound()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Altrui");
        var someoneElses = await AddBookingAsync(org, house, "someone.else@example.com", checkInInDays: 9);
        var notDirect = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 9, source: BookingSource.Manual);

        var refusals = new[]
        {
            await Assert.ThrowsAsync<NotFoundException>(() => GetAsync(GuestEmail, someoneElses.Id)),
            await Assert.ThrowsAsync<NotFoundException>(() => GetAsync(GuestEmail, Guid.NewGuid())),
            await Assert.ThrowsAsync<NotFoundException>(() => GetAsync(GuestEmail, notDirect.Id)),
        };

        Assert.All(refusals, ex =>
        {
            Assert.Equal(GuestBookingLookupErrorCodes.NotFound, ex.Code);
            Assert.Equal("GuestAccountBookingNotFound", ex.MessageKey);
        });
    }

    // ─── The session ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenSessionAsync_OwnBooking_MintsTheGuestAreaTokenOfTheSiteAndOfTheAccountsEmail()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Sessione");
        var stay = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 6);
        var tokens = Tokens();

        var opened = await OpenSessionAsync(AccountEmail, stay.Id, tokens, tenantOrg: Guid.NewGuid());

        Assert.StartsWith(GuestSessionTokens.Prefix, opened.AccessToken, StringComparison.Ordinal);
        Assert.Equal(BookingCodes.Format(stay.BookingCode), opened.BookingCode);
        Assert.Equal(org.Slug, opened.OrgSlug);
        // The default length of a guest-area session: two hours.
        Assert.Equal(Now.UtcDateTime.AddMinutes(GuestSessionService.DefaultSessionMinutes), opened.ExpiresAt);
        var ticket = tokens.Read(opened.AccessToken);
        Assert.NotNull(ticket);
        Assert.Equal(org.Id, ticket.OrgId);
        Assert.True(ticket.IsForEmail(GuestEmail));
        Assert.False(ticket.IsForEmail("someone.else@example.com"));
        // The address is in the token only as a hash, inside the sealed payload.
        Assert.DoesNotContain(GuestEmail, opened.AccessToken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenSessionAsync_TheLengthIsTheOneOfTheGuestAreaSessions()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Durata");
        var stay = await AddBookingAsync(org, house, GuestEmail, checkInInDays: 6);

        var opened = await OpenSessionAsync(
            GuestEmail,
            stay.Id,
            Tokens(),
            settings: new() { [GuestSessionService.SessionMinutesKey] = "9999" });

        // Never more than 24 hours, whatever the configuration says.
        Assert.Equal(Now.UtcDateTime.AddMinutes(GuestSessionService.MaxSessionMinutes), opened.ExpiresAt);
    }

    [Fact]
    public async Task OpenSessionAsync_AnotherPersonsBookingOrAnUnknownId_NotFoundAndNoToken()
    {
        var org = await AddOrgAsync();
        var house = await AddPropertyAsync(org, "Casa Sessione Altrui");
        var someoneElses = await AddBookingAsync(org, house, "someone.else@example.com", checkInInDays: 6);

        await Assert.ThrowsAsync<NotFoundException>(() => OpenSessionAsync(GuestEmail, someoneElses.Id, Tokens()));
        await Assert.ThrowsAsync<NotFoundException>(() => OpenSessionAsync(GuestEmail, Guid.NewGuid(), Tokens()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EveryRead_ABlankAccountEmail_IsRefused(string email)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ListAsync(email, GuestAccountBookingScope.Upcoming, 1, 20));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => GetAsync(email, Guid.NewGuid()));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => OpenSessionAsync(email, Guid.NewGuid(), Tokens()));
    }

    // ─── Harness ────────────────────────────────────────────────────────────────────────────────────────

    private AppDbContext NewDb(Guid? tenantOrg = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedTenantContext(tenantOrg, filterEnabled: true));

    private GuestSessionTokens Tokens() => new(new EphemeralDataProtectionProvider(), _clock);

    private static IConfiguration Settings(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    /// <summary>The service as the request of a signed-in guest sees it: the tenant filter on, for no org or for another one.</summary>
    private (GuestAccountBookingService Service, AppDbContext Db) NewService(
        Guid? tenantOrg,
        GuestSessionTokens? tokens = null,
        Dictionary<string, string?>? settings = null)
    {
        var db = NewDb(tenantOrg);
        var configuration = Settings(settings);
        var lookup = new GuestBookingLookupService(
            db,
            Mock.Of<IGuestCheckInService>(),
            Mock.Of<IGuestCheckInLinkEmailQueue>(),
            new PublicSiteLinks(Options.Create(new PublicSiteOptions())),
            configuration,
            NullLogger<GuestBookingLookupService>.Instance,
            _clock);
        return (new GuestAccountBookingService(db, lookup, tokens ?? Tokens(), configuration, _clock), db);
    }

    private async Task<GuestAccountBookingPage> ListAsync(
        string email,
        GuestAccountBookingScope scope,
        int page,
        int pageSize,
        Guid? tenantOrg = null)
    {
        var (service, db) = NewService(tenantOrg);
        await using (db)
            return await service.ListAsync(email, scope, page, pageSize);
    }

    private async Task<GuestAccountBookingDetail> GetAsync(string email, Guid bookingId, Guid? tenantOrg = null)
    {
        var (service, db) = NewService(tenantOrg);
        await using (db)
            return await service.GetAsync(email, bookingId);
    }

    private async Task<GuestAccountSessionOpened> OpenSessionAsync(
        string email,
        Guid bookingId,
        GuestSessionTokens tokens,
        Guid? tenantOrg = null,
        Dictionary<string, string?>? settings = null)
    {
        var (service, db) = NewService(tenantOrg, tokens, settings);
        await using (db)
            return await service.OpenSessionAsync(email, bookingId);
    }

    private async Task<OrgEntity> AddOrgAsync(
        string displayName = "Villa Prova",
        bool active = true,
        string contactEmail = "host@example.com")
    {
        var org = new OrgEntity
        {
            Name = displayName,
            Slug = $"site-{Guid.NewGuid():N}",
            DisplayName = displayName,
            ContactEmail = contactEmail,
            IsActive = active,
        };
        await using var db = NewDb();
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private async Task<Property> AddPropertyAsync(
        OrgEntity org,
        string name,
        string city = "Como",
        string currency = "EUR",
        List<string>? photos = null,
        bool deleted = false)
    {
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = name,
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Guest account test",
            Address = "Via Prova 1",
            City = city,
            PostalCode = "22100",
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 50m,
            Currency = currency,
            PhotoUrls = photos ?? [],
            IsActive = true,
            IsDeleted = deleted,
        };
        await using var db = NewDb();
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    /// <summary>A booking of the public checkout (3 nights, 350 + 12 tourist tax = 362 unless <paramref name="shape"/> changes it).</summary>
    private async Task<Booking> AddBookingAsync(
        OrgEntity org,
        Property property,
        string guestEmail,
        int checkInInDays,
        int nights = 3,
        BookingStatus status = BookingStatus.Confirmed,
        BookingSource source = BookingSource.Direct,
        int adults = 2,
        int children = 0,
        decimal paid = 0m,
        decimal refunded = 0m,
        Action<Booking>? shape = null,
        Action<Payment>? payment = null)
    {
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Guest",
            Email = guestEmail,
            PhoneNumber = "+393331112222",
        };
        var checkIn = Today.AddDays(checkInInDays);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = adults + children,
            NumberOfAdults = adults,
            NumberOfChildren = children,
            Status = status,
            Source = source,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TouristTaxAmount = 12m,
            TotalPrice = 362m,
            CreatedAt = Now.UtcDateTime.AddDays(-1),
            UpdatedAt = Now.UtcDateTime.AddDays(-1),
        };
        shape?.Invoke(booking);

        await using var db = NewDb();
        db.AddRange(guest, booking);
        if (paid > 0m)
        {
            db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = org.Id,
                Amount = paid,
                RefundedAmount = refunded,
                Status = refunded >= paid ? PaymentStatus.Refunded : refunded > 0m ? PaymentStatus.PartiallyRefunded : PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                TransactionId = $"pi_test_{Guid.NewGuid():N}",
                StripePaymentIntentId = $"pi_test_{Guid.NewGuid():N}",
                ProcessedAt = booking.CreatedAt,
            });
        }

        if (payment is not null)
        {
            // A payment attempt in the state the test needs (a failed one, one in flight, a hold's intent not paid yet).
            var attempt = new Payment
            {
                BookingId = booking.Id,
                OrgId = org.Id,
                Amount = booking.TotalPrice,
                Method = PaymentMethod.CreditCard,
                TransactionId = $"pi_test_{Guid.NewGuid():N}",
                CreatedAt = booking.CreatedAt,
                UpdatedAt = booking.CreatedAt,
            };
            payment(attempt);
            db.Payments.Add(attempt);
        }

        await db.SaveChangesAsync();
        return booking;
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
