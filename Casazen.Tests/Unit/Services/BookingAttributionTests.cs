using Casazen.Core.Bookings;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Octorate;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The attribution and the origin of a booking (E27/F01/US01/T01, NF-123). Privacy rule: what a link or a referrer carries is kept only as a marketing
/// token or a host name, never as an e-mail address, a phone number (with or without separators), a fiscal code, an address of a page or a query, and never
/// longer than its column. Data rule: the four origins the host reads (a portal, the site of the agency, CasaZen's own site, the host) come from the source
/// and the attribution of the booking by one rule, the badge and the filter of the list agree, the reservation of the booking engine of the agency is
/// attributed to it by the import, and the reservation CasaZen pushed itself never is.
/// </summary>
public class BookingAttributionTests
{
    private const string Code = "K7Q2M9X4TB";

    // ─── Privacy: what is kept ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: privacy rule. A stray, careless or hostile link carries an e-mail address, the address of a page, a phone number and a referrer with
    /// credentials, a port, a path, a query and a fragment: nothing of it that is personal reaches the booking. The values that are not tokens are dropped
    /// (never repaired, never truncated), the referrer is reduced to its host name, and the result fits its columns.
    /// </summary>
    [Fact]
    public void Attribution_SanitizedNoPersonalData_Test()
    {
        var dirty = BookingAttribution.Sanitize(new BookingAttributionInput(
            "mario.rossi@example.com",
            "https://www.agenzia.it/case?nome=Mario",
            "+39 333 1234567",
            "https://mario:segreta@www.agenzia.it:8443/case/trullo?email=mario@example.com#top"));

        Assert.Null(dirty.Source);
        Assert.Null(dirty.Medium);
        Assert.Null(dirty.Campaign);
        Assert.Equal("www.agenzia.it", dirty.ReferrerHost);

        // A phone number written with separators and an Italian fiscal code are personal data as well: nothing of them is kept, in whichever field they come.
        var identifiers = BookingAttribution.Sanitize(new BookingAttributionInput("333-123-4567", "333.123.4567", "RSSMRA80A01H501U", null));
        Assert.True(identifiers.IsEmpty);
        Assert.True(BookingAttribution.Sanitize(new BookingAttributionInput("333_123_4567", "rssmra-80a01-h501u", "0039-333-1234567", null)).IsEmpty);

        // Whatever is kept is a token or a host: no character that an address, a query or a person's contact needs.
        var stored = BookingAttribution.Sanitize(new BookingAttributionInput(
            "Agency-Site", " LINK ", "Estate_2026.v2", "WWW.Agenzia.IT/case?utm=mario@example.com"));
        Assert.Equal(new BookingAttributionValues("agency-site", "link", "estate_2026.v2", "www.agenzia.it"), stored);
        foreach (var value in new[] { stored.Source, stored.Medium, stored.Campaign, stored.ReferrerHost })
            Assert.DoesNotContain(value!, c => c is '@' or ':' or '/' or '?' or '=' or '#' or ' ' or '+');

        // What is sanitized stays so: the rule is idempotent.
        Assert.Equal(stored, BookingAttribution.Sanitize(new BookingAttributionInput(stored.Source, stored.Medium, stored.Campaign, stored.ReferrerHost)));

        // Nothing sent is nothing kept.
        Assert.True(BookingAttribution.Sanitize(null).IsEmpty);
        Assert.True(BookingAttribution.Sanitize(new BookingAttributionInput(null, " ", string.Empty, null)).IsEmpty);
    }

    [Theory]
    [InlineData("agency-site", "agency-site")]
    [InlineData("  Agency-Site ", "agency-site")]
    [InlineData("estate_2026.v2", "estate_2026.v2")]
    [InlineData("città-2026", "città-2026")]
    [InlineData("20261114", "20261114")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("mario.rossi@example.com", null)]
    [InlineData("https://www.agenzia.it/case", null)]
    [InlineData("www.agenzia.it/case", null)]
    [InlineData("estate?x=1", null)]
    [InlineData("mario rossi", null)]
    [InlineData("+39 333 1234567", null)]
    [InlineData("393331234567", null)]
    [InlineData("333-123-4567", null)]
    [InlineData("333.123.4567", null)]
    [InlineData("333_123_4567", null)]
    [InlineData("0039-333-1234567", null)]
    [InlineData("tel-333-1234567", null)]
    [InlineData("2026-11-14-2026-11-20", null)]
    [InlineData("RSSMRA80A01H501U", null)]
    [InlineData("rssmra-80a01-h501u", null)]
    [InlineData("RSSMRA80A01H50MU", null)]
    [InlineData("estate-2026-11-14", "estate-2026-11-14")]
    [InlineData("promo-2026-11-14-v2", "promo-2026-11-14-v2")]
    [InlineData("newsletter-autumn", "newsletter-autumn")]
    [InlineData("-estate", null)]
    [InlineData("estate-", null)]
    [InlineData(".estate", null)]
    [InlineData("estate;drop", null)]
    public void Attribution_ATokenIsKeptOnlyWhenItIsOne_NeverRepaired(string? sent, string? kept)
    {
        var values = BookingAttribution.Sanitize(new BookingAttributionInput(sent, sent, sent, null));

        Assert.Equal((kept, kept, kept), (values.Source, values.Medium, values.Campaign));
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=casa", "www.google.com")]
    [InlineData("https://user:pass@www.agenzia.it:8443/case/trullo?x=1#top", "www.agenzia.it")]
    [InlineData("WWW.Agenzia.IT/case", "www.agenzia.it")]
    [InlineData("agenzia.it", "agenzia.it")]
    [InlineData("agenzia.it.", "agenzia.it")]
    [InlineData("https://münchen.example.com/", "xn--mnchen-3ya.example.com")]
    [InlineData("mario.rossi@example.com", "example.com")]
    [InlineData("http://192.168.1.10/", null)]
    [InlineData("https://[::1]/", null)]
    [InlineData("http://localhost:5173/", null)]
    [InlineData("ftp://files.example.com/", null)]
    [InlineData("android-app://com.google.android.gm", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("not a host", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Attribution_TheReferrerIsReducedToItsHostName_OrDropped(string? sent, string? host)
    {
        Assert.Equal(host, BookingAttribution.Sanitize(new BookingAttributionInput(null, null, null, sent)).ReferrerHost);
    }

    [Fact]
    public void Attribution_AValueLongerThanItsColumn_IsDroppedNotTruncated_AndOneThatFitsIsKept()
    {
        var fits = BookingAttribution.Sanitize(new BookingAttributionInput(new string('a', 40), new string('b', 40), new string('c', 80), "www.agenzia.it"));
        var tooLong = BookingAttribution.Sanitize(new BookingAttributionInput(
            new string('a', 41), new string('b', 41), new string('c', 81), "https://" + new string('h', 300) + ".it/"));
        var huge = BookingAttribution.Sanitize(new BookingAttributionInput(new string('a', 5000), new string('b', 5000), new string('c', 5000), new string('h', 5000)));

        Assert.Equal((40, 40, 80), (fits.Source!.Length, fits.Medium!.Length, fits.Campaign!.Length));
        Assert.True(tooLong.IsEmpty);
        Assert.True(huge.IsEmpty);
        Assert.Equal((40, 40, 80, 255), (BookingAttribution.MaxSourceLength, BookingAttribution.MaxMediumLength, BookingAttribution.MaxCampaignLength, BookingAttribution.MaxReferrerHostLength));
    }

    // ─── Data: the four origins ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data rule. The origin the host reads is worked out from the source and the attribution of the booking, by one rule, and it is one of the four:
    /// a portal for every channel (whatever the attribution says), the site of the agency for a booking of the booking site that came from a link of the agency or
    /// from the booking engine of the agency, CasaZen's own site for any other booking of the booking site, the host for what the host entered.
    /// </summary>
    [Fact]
    public void BookingOrigin_FourOrigins_Test()
    {
        // The table is written out, not derived from the rule: a source that is added to the enum is a decision to take here.
        var expected = new Dictionary<BookingSource, BookingOrigin>
        {
            [BookingSource.Airbnb] = BookingOrigin.Portal,
            [BookingSource.BookingCom] = BookingOrigin.Portal,
            [BookingSource.Expedia] = BookingOrigin.Portal,
            [BookingSource.Vrbo] = BookingOrigin.Portal,
            [BookingSource.TripAdvisor] = BookingOrigin.Portal,
            [BookingSource.Agoda] = BookingOrigin.Portal,
            [BookingSource.OtherChannel] = BookingOrigin.Portal,
            [BookingSource.Manual] = BookingOrigin.Manual,
            [BookingSource.Local] = BookingOrigin.Manual,
        };
        Assert.Equal(Enum.GetValues<BookingSource>().Where(s => s != BookingSource.Direct).Order(), expected.Keys.Order());

        // A source that is not the booking site is its own origin whatever the attribution says: a link of the agency does not turn a portal into the agency.
        foreach (var (source, origin) in expected)
        {
            Assert.Equal(origin, BookingOrigins.Of(source, null));
            Assert.Equal(origin, BookingOrigins.Of(source, BookingAttribution.AgencySiteSource));
            Assert.Equal(origin, BookingOrigins.Of(source, BookingAttribution.OctorateBookingEngineSource));
        }

        // The booking site: the agency's when the attribution says so, CasaZen's own otherwise.
        Assert.Equal(BookingOrigin.AgencySite, BookingOrigins.Of(BookingSource.Direct, "agency-site"));
        Assert.Equal(BookingOrigin.AgencySite, BookingOrigins.Of(BookingSource.Direct, "octorate-booking-engine"));
        Assert.Equal(BookingOrigin.CasaZenSite, BookingOrigins.Of(BookingSource.Direct, null));
        Assert.Equal(BookingOrigin.CasaZenSite, BookingOrigins.Of(BookingSource.Direct, string.Empty));
        Assert.Equal(BookingOrigin.CasaZenSite, BookingOrigins.Of(BookingSource.Direct, "newsletter"));
        Assert.Equal(BookingOrigin.CasaZenSite, BookingOrigins.Of(BookingSource.Direct, "agency"));

        // The four origins are all there, and the source is not touched by any of this.
        Assert.Equal(
            Enum.GetValues<BookingOrigin>().Order(),
            Enum.GetValues<BookingSource>().SelectMany(s => new[] { BookingOrigins.Of(s, null), BookingOrigins.Of(s, "agency-site") }).Distinct().Order());
        Assert.Equal(4, Enum.GetValues<BookingOrigin>().Length);

        // The channel of a portal is the name of its source; no other origin has one.
        Assert.Equal("Airbnb", BookingOrigins.PortalOf(StayOf(BookingSource.Airbnb)));
        Assert.Equal("OtherChannel", BookingOrigins.PortalOf(StayOf(BookingSource.OtherChannel, label: "Holidu")));
        Assert.Null(BookingOrigins.PortalOf(StayOf(BookingSource.Direct, BookingAttribution.AgencySiteSource)));
        Assert.Null(BookingOrigins.PortalOf(StayOf(BookingSource.Direct)));
        Assert.Null(BookingOrigins.PortalOf(StayOf(BookingSource.Manual)));
    }

    /// <summary>
    /// PROOF TEST: data rule. The filter of the list is the same rule as the badge: for every source and attribution the predicate keeps a booking exactly when
    /// its origin is one of those asked for, every booking is in one filter only, none asked for is no booking, and all four ask for every booking.
    /// </summary>
    [Fact]
    public void BookingOrigin_TheListFilterIsTheSameRuleAsTheBadge_Test()
    {
        var stays = (from source in Enum.GetValues<BookingSource>()
                     from attribution in new string?[] { null, "agency-site", "octorate-booking-engine", "newsletter" }
                     select StayOf(source, attribution)).ToList();
        var origins = Enum.GetValues<BookingOrigin>();

        foreach (var origin in origins)
        {
            var keeps = BookingOrigins.Matching([origin]).Compile();
            Assert.All(stays, stay => Assert.Equal(BookingOrigins.Of(stay) == origin, keeps(stay)));
        }

        Assert.All(stays, stay => Assert.Single(origins, origin => BookingOrigins.Matching([origin]).Compile()(stay)));
        Assert.DoesNotContain(stays, BookingOrigins.Matching([]).Compile().Invoke);
        Assert.All(stays, stay => Assert.True(BookingOrigins.Matching(origins).Compile()(stay)));

        var portalOrManual = BookingOrigins.Matching([BookingOrigin.Portal, BookingOrigin.Manual, BookingOrigin.Portal]).Compile();
        Assert.All(stays, stay => Assert.Equal(BookingOrigins.Of(stay) is BookingOrigin.Portal or BookingOrigin.Manual, portalOrManual(stay)));
    }

    [Fact]
    public async Task BookingOrigin_TheFilterRunsOnTheDatabase_OneFilterKeepsOnlyItsBookings()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var portal = StayOf(BookingSource.Airbnb);
        var agencyLink = StayOf(BookingSource.Direct, BookingAttribution.AgencySiteSource);
        var agencyEngine = StayOf(BookingSource.Direct, BookingAttribution.OctorateBookingEngineSource);
        var casazen = StayOf(BookingSource.Direct);
        var casazenWithOtherTag = StayOf(BookingSource.Direct, "newsletter");
        var manual = StayOf(BookingSource.Manual);
        db.Bookings.AddRange(portal, agencyLink, agencyEngine, casazen, casazenWithOtherTag, manual);
        await db.SaveChangesAsync();

        async Task<IReadOnlyList<Guid>> IdsOfAsync(params BookingOrigin[] origins) =>
            (await db.Bookings.AsNoTracking().Where(BookingOrigins.Matching(origins)).Select(b => b.Id).ToListAsync()).Order().ToList();

        static IReadOnlyList<Guid> Sorted(params Guid[] ids) => ids.Order().ToList();

        Assert.Equal(Sorted(portal.Id), await IdsOfAsync(BookingOrigin.Portal));
        Assert.Equal(Sorted(agencyLink.Id, agencyEngine.Id), await IdsOfAsync(BookingOrigin.AgencySite));
        Assert.Equal(Sorted(casazen.Id, casazenWithOtherTag.Id), await IdsOfAsync(BookingOrigin.CasaZenSite));
        Assert.Equal(Sorted(manual.Id), await IdsOfAsync(BookingOrigin.Manual));
        Assert.Equal(Sorted(portal.Id, manual.Id), await IdsOfAsync(BookingOrigin.Manual, BookingOrigin.Portal));
        Assert.Empty(await IdsOfAsync());
    }

    [Fact]
    public void BookingMapper_EveryBookingAnswerSaysItsOrigin_AndAPortalItsChannel_TheSourceUnchanged()
    {
        var now = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        var other = StayOf(BookingSource.OtherChannel, label: "Holidu");
        var stays = new (Booking Stay, string Origin, string? Channel)[]
        {
            (StayOf(BookingSource.Airbnb), "Portal", "Airbnb"),
            (other, "Portal", "OtherChannel"),
            (StayOf(BookingSource.Direct, BookingAttribution.AgencySiteSource), "AgencySite", null),
            (StayOf(BookingSource.Direct, BookingAttribution.OctorateBookingEngineSource), "AgencySite", null),
            (StayOf(BookingSource.Direct), "CasaZenSite", null),
            (StayOf(BookingSource.Manual), "Manual", null),
        };

        foreach (var (stay, origin, channel) in stays)
        {
            // The list item, the detail and the plain answer are one answer for this: the words are those of the base.
            var item = BookingMapper.ToListItem(stay, null, now);
            var detail = BookingMapper.Fill(new BookingDetailDto(), stay, now);
            var plain = BookingMapper.ToResponse(stay, now);
            Assert.All(new BookingResponseDto[] { item, detail, plain }, answer => Assert.Equal((origin, channel), (answer.Origin, answer.OriginChannel)));

            // The invoicing plan reasons per channel: the source is what it was, and the booking engine of the agency is still direct.
            Assert.Equal(stay.Source.ToString(), plain.Source);
        }

        // The name of a portal that has no source of its own is the label the booking has always had.
        Assert.Equal("Holidu", BookingMapper.ToResponse(other, now).ChannelLabel);
    }

    // ─── Data: the import ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data rule. A reservation of the booking engine of the agency (channel 270) stays direct and is attributed to the booking engine, so its origin
    /// is the site of the agency; a reservation of a portal gets no attribution and is a portal.
    /// </summary>
    [Fact]
    public async Task BookingImport_AReservationOfTheBookingEngine_StaysDirectAndIsAttributedToIt_Test()
    {
        var h = new BookingImportHarness();
        h.Serve(
            Res("9101") with { ChannelId = "270", ChannelName = "Direct Reservations" },
            Res("9102"));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(2, result.Created);
        var engine = await h.ReadBookingAsync("9101");
        Assert.Equal((BookingSource.Direct, "octorate-booking-engine"), (engine.Source, engine.AttributionSource));
        Assert.Equal(BookingOrigin.AgencySite, BookingOrigins.Of(engine));
        Assert.Null(engine.AttributionMedium);
        Assert.Null(engine.AttributionCampaign);
        Assert.Null(engine.AttributionReferrerHost);

        var airbnb = await h.ReadBookingAsync("9102");
        Assert.Equal((BookingSource.Airbnb, (string?)null), (airbnb.Source, airbnb.AttributionSource));
        Assert.Equal(BookingOrigin.Portal, BookingOrigins.Of(airbnb));
    }

    /// <summary>
    /// PROOF TEST: data rule. The reservation CasaZen pushed itself to the channel of the booking engine is the booking CasaZen already has: its echo is linked by
    /// reference and its attribution stays as it was (none: CasaZen's own site; the link of the agency it was made from: the site of the agency), and a newer
    /// version of it, read once the push has its row of the mirror, changes nothing either.
    /// </summary>
    [Fact]
    public async Task BookingImport_TheReservationCasaZenPushed_IsNeverReattributed_Test()
    {
        var h = new BookingImportHarness();
        var ownCasaZen = await SeedDirectBookingAsync(h, Code, attribution: null);
        var ownAgencyLink = await SeedDirectBookingAsync(h, "B0THERCODE", attribution: BookingAttribution.AgencySiteSource);
        h.Serve(
            Res("9201") with { Refer = Code, ChannelId = "270", ChannelName = "Direct Reservations" },
            Res("9202") with { Refer = "B0THERCODE", ChannelId = "270", ChannelName = "Direct Reservations" });
        await using var db = h.NewDb();

        var first = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        // The echoes are linked, not imported: no booking is made and nothing is attributed.
        Assert.Equal((0, 0), (first.Created, first.Updated));
        Assert.Equal(2, await ReadAllAsync(h));
        Assert.Equal((BookingSource.Direct, (string?)null, "9201"), await ReadAsync(h, ownCasaZen));
        Assert.Equal((BookingSource.Direct, (string?)"agency-site", "9202"), await ReadAsync(h, ownAgencyLink));

        // The push saved its ids and its rows of the mirror (only the push makes them); a newer version of each is read: the attribution stays as it was.
        await MirrorAsync(h, ownCasaZen);
        await MirrorAsync(h, ownAgencyLink);
        h.Serve(
            Res("9201") with { Refer = Code, ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion },
            Res("9202") with { Refer = "B0THERCODE", ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion });
        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(2, await ReadAllAsync(h));
        Assert.Equal((BookingSource.Direct, (string?)null, "9201"), await ReadAsync(h, ownCasaZen));
        Assert.Equal((BookingSource.Direct, (string?)"agency-site", "9202"), await ReadAsync(h, ownAgencyLink));
    }

    /// <summary>
    /// A booking the import made before the attribution existed (direct, a reservation on Octorate, no row of the mirror) gets the attribution of the booking engine
    /// with the next version of its reservation; a booking that has an attribution already keeps it, and the change alone announces nothing.
    /// </summary>
    [Fact]
    public async Task BookingImport_ABookingTheImportMadeBeforeTheAttribution_GetsItWithTheNextVersion_AndAnAttributedOneKeepsItsOwn()
    {
        var h = new BookingImportHarness();
        var imported = await h.SeedBookingAsync(
            "9301", source: BookingSource.Direct, status: BookingStatus.Confirmed, total: 330m, updatedAt: FirstVersion.AddDays(-1), octorateUpdatedAt: FirstVersion);
        var attributed = await h.SeedBookingAsync(
            "9302", source: BookingSource.Direct, status: BookingStatus.Confirmed, total: 330m, updatedAt: FirstVersion.AddDays(-1), octorateUpdatedAt: FirstVersion);
        await SetAttributionAsync(h, attributed, BookingAttribution.AgencySiteSource);
        h.Serve(
            Res("9301") with { ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion },
            Res("9302") with { ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal((BookingSource.Direct, (string?)"octorate-booking-engine"), await SourceAndAttributionAsync(h, imported));
        Assert.Equal((BookingSource.Direct, (string?)"agency-site"), await SourceAndAttributionAsync(h, attributed));
    }

    [Fact]
    public async Task BookingImport_AReadThatChangesOnlyTheAttribution_IsSavedWithoutAnEvent()
    {
        var h = new BookingImportHarness();
        var imported = await h.SeedBookingAsync(
            "9303", source: BookingSource.Direct, status: BookingStatus.Confirmed, total: 330m, updatedAt: FirstVersion.AddDays(-1), octorateUpdatedAt: FirstVersion);

        // The booking is the one the reservation says: the dates, the guests and the amounts are the same, so only the attribution is new.
        await using (var seed = h.NewDb())
        {
            var row = await seed.Bookings.SingleAsync(b => b.Id == imported);
            row.BasePrice = 300m;
            row.TotalPrice = 330m;
            row.TouristTax = 6m;
            row.NumberOfAdults = 2;
            row.OtaCommission = 45m;
            row.OtaPaidAmount = 100m;
            await seed.SaveChangesAsync();
        }

        h.Serve(Res("9303") with { ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal("octorate-booking-engine", (await SourceAndAttributionAsync(h, imported)).Item2);
        Assert.Empty(await h.ReadEventsAsync());
    }

    [Fact]
    public async Task BookingImport_AManualBookingPushedToTheChannel_IsNeverAttributedToTheBookingEngine()
    {
        var h = new BookingImportHarness();
        var manual = await h.SeedBookingAsync(
            "9401", source: BookingSource.Manual, status: BookingStatus.Confirmed, total: 500m, updatedAt: FirstVersion.AddDays(-1), octorateUpdatedAt: FirstVersion);
        h.Serve(Res("9401") with { ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal((BookingSource.Manual, (string?)null), await SourceAndAttributionAsync(h, manual));
    }

    /// <summary>
    /// A reservation of the booking engine that is attributed to it keeps its attribution when CasaZen cancels it: the cancellation makes a row of the mirror for a
    /// reservation CasaZen did not make (OctorateBookingCancelHandler), and neither that row nor the next version read takes the attribution away or changes it.
    /// </summary>
    [Fact]
    public async Task BookingImport_AnAttributedBooking_KeepsItsAttribution_WhenTheCancellationMakesItsRowOfTheMirror()
    {
        var h = new BookingImportHarness();
        var engine = await h.SeedBookingAsync(
            "9501", source: BookingSource.Direct, status: BookingStatus.Confirmed, total: 330m, updatedAt: FirstVersion.AddDays(-1), octorateUpdatedAt: FirstVersion);
        await SetAttributionAsync(h, engine, BookingAttribution.OctorateBookingEngineSource);
        await MirrorAsync(h, engine, OctorateSyncOperation.Cancel);
        h.Serve(Res("9501") with { ChannelId = "270", ChannelName = "Direct Reservations", UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        var (source, attribution) = await SourceAndAttributionAsync(h, engine);
        Assert.Equal((BookingSource.Direct, (string?)"octorate-booking-engine"), (source, attribution));
        Assert.Equal(BookingOrigin.AgencySite, BookingOrigins.Of(source, attribution));
    }

    // ─── What the tests share ────────────────────────────────────────────────────────────────────────────

    private static Booking StayOf(BookingSource source, string? attribution = null, string? label = null) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = OrgA,
        PropertyId = Guid.NewGuid(),
        CheckInDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 2,
        NumberOfAdults = 2,
        Status = BookingStatus.Confirmed,
        Source = source,
        AttributionSource = attribution,
        ChannelLabel = label,
        TotalPrice = 300m,
    };

    /// <summary>A booking CasaZen made on its booking site (direct, confirmed), with the code that was sent as <c>refer</c> and no Octorate id yet.</summary>
    private static async Task<Guid> SeedDirectBookingAsync(BookingImportHarness h, string code, string? attribution)
    {
        await using var db = h.NewDb();
        var guest = new Guest { OrgId = OrgA, FirstName = "Giulia", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = StayOf(BookingSource.Direct, attribution);
        booking.PropertyId = h.PropertyId;
        booking.GuestId = guest.Id;
        booking.BookingCode = code;
        booking.UpdatedAt = FirstVersion;
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>The row of the mirror the push of CasaZen makes for a booking (the import never does), or the cancellation of CasaZen for a reservation it did not make.</summary>
    private static async Task MirrorAsync(BookingImportHarness h, Guid bookingId, OctorateSyncOperation operation = OctorateSyncOperation.Create)
    {
        await using var db = h.NewDb();
        db.OctorateBookingSyncs.Add(
            new OctorateBookingSync { OrgId = OrgA, BookingId = bookingId, PropertyId = h.PropertyId, LastOperation = operation, State = OctorateSyncState.Synced });
        await db.SaveChangesAsync();
    }

    private static async Task SetAttributionAsync(BookingImportHarness h, Guid bookingId, string attribution)
    {
        await using var db = h.NewDb();
        (await db.Bookings.SingleAsync(b => b.Id == bookingId)).AttributionSource = attribution;
        await db.SaveChangesAsync();
    }

    private static async Task<int> ReadAllAsync(BookingImportHarness h)
    {
        await using var db = h.NewDb();
        return await db.Bookings.CountAsync(b => b.OrgId == OrgA);
    }

    private static async Task<(BookingSource, string?, string?)> ReadAsync(BookingImportHarness h, Guid bookingId)
    {
        await using var db = h.NewDb();
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        return (booking.Source, booking.AttributionSource, booking.OctorateReservationId);
    }

    private static async Task<(BookingSource, string?)> SourceAndAttributionAsync(BookingImportHarness h, Guid bookingId)
    {
        var (source, attribution, _) = await ReadAsync(h, bookingId);
        return (source, attribution);
    }
}
