using System.Globalization;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Search;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// UI-13a: what the global search finds, and for whom, on the in-memory provider (the search keys are written by
/// <see cref="SearchKeysForTests"/>, as PostgreSQL's generated columns do). The same world and the same questions run on PostgreSQL
/// in <c>GlobalSearchPostgresTests</c>. Covers: the words (case, accents, beginning of a word), the groups and what each one shows,
/// the org boundary, the collaborator limited to some properties, the permissions of the caller, the limit, and the data that must
/// never come back (document, phone, full e-mail, anonymized people).
/// </summary>
public class GlobalSearchServiceTests : IAsyncLifetime
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private AppDbContext _db = null!;
    private SearchWorld _world = null!;

    public async Task InitializeAsync()
    {
        _db = SearchScenario.NewInMemoryDb();
        _world = await SearchScenario.SeedAsync(_db, computeKeys: true);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private Task<GlobalSearchResult> SearchAsync(
        string term, HostSearchAccess? host, Guid? supplierOrgId = null, int limit = 5, CultureInfo? culture = null) =>
        new GlobalSearchService(_db, NullLogger<GlobalSearchService>.Instance)
            .SearchAsync(new GlobalSearchRequest(SearchText.Parse(term), limit, host, supplierOrgId, culture ?? Italian));

    private Task<GlobalSearchResult> AsOwnerAsync(string term, int limit = 5) => SearchAsync(term, _world.Everything(), limit: limit);

    private static IReadOnlyList<string> Types(GlobalSearchResult result) => result.Groups.Select(g => g.Type).ToList();

    private static IReadOnlyList<SearchHit> Hits(GlobalSearchResult result, string type) =>
        result.Groups.SingleOrDefault(g => g.Type == type)?.Items ?? [];

    private static IReadOnlyList<Guid> Ids(GlobalSearchResult result, string type) => Hits(result, type).Select(h => h.Id).ToList();

    // ─── The words ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("trullo")]
    [InlineData("TRULLO")]
    [InlineData("  Trullo  ")]
    [InlineData("tru")]
    [InlineData("bianco trullo")]
    public async Task Properties_TheBeginningOfAWord_FindsTheProperty(string term)
    {
        var result = await AsOwnerAsync(term);

        // Only the property of this org: the other org has a "Trullo Altrove" too.
        Assert.Equal(["property"], Types(result));
        var hit = Assert.Single(Hits(result, "property"));
        Assert.Equal(_world.Trullo.Id, hit.Id);
        Assert.Equal("Trullo Bianco", hit.Title);
        Assert.Equal("Alberobello", hit.Subtitle);
        Assert.Equal(SearchDestinations.ShortRentProperty, hit.Destination);
    }

    [Theory]
    [InlineData("forli")]
    [InlineData("FORLÌ")]
    [InlineData("forlì")]
    [InlineData("bella")]
    [InlineData("CASA")]
    public async Task Properties_CaseAndAccentsDoNotMatter_AWordInTheMiddleIsFound(string term)
    {
        var result = await AsOwnerAsync(term);

        Assert.Equal(_world.CasaBella.Id, Assert.Single(Hits(result, "property")).Id);
    }

    [Fact]
    public async Task Properties_AWordInTheMiddleOfAWord_IsNotFound()
    {
        // "ullo" is inside "trullo", not at the beginning of a word.
        Assert.Empty((await AsOwnerAsync("ullo")).Groups);
    }

    [Theory]
    [InlineData("IT0720")]
    [InlineData("it072003c2abcd12")]
    public async Task Properties_TheCinIsFoundByItsBeginning(string term)
    {
        Assert.Equal(_world.Trullo.Id, Assert.Single(Hits(await AsOwnerAsync(term), "property")).Id);
    }

    [Theory]
    [InlineData("villa cancellata")]
    [InlineData("rustico")]
    public async Task Properties_ADeletedOrSwitchedOffProperty_IsNeverFound(string term)
    {
        Assert.Empty((await AsOwnerAsync(term)).Groups);
    }

    [Fact]
    public async Task Properties_ALongTermProperty_IsFoundWithTheLongRentPermissionOnly_AndOpensInThatArea()
    {
        var withBoth = await AsOwnerAsync("loft");
        var hit = Assert.Single(Hits(withBoth, "property"));
        Assert.Equal((_world.LoftNavigli.Id, SearchDestinations.LongRentProperty), (hit.Id, hit.Destination));

        // Property.read of the short-rent context says nothing about the properties of the long-term area (and the reverse).
        var shortOnly = await SearchAsync("loft", new HostSearchAccess(_world.OrgWide, true, false, true, true, true));
        Assert.Empty(Hits(shortOnly, "property"));
        var longOnly = await SearchAsync("trullo", new HostSearchAccess(_world.OrgWide, false, true, true, true, true));
        Assert.Empty(Hits(longOnly, "property"));
    }

    [Fact]
    public async Task Search_TheWordsMustAllBePresent_InAnyOrder()
    {
        Assert.Equal(_world.CasaBella.Id, Assert.Single(Hits(await AsOwnerAsync("forli casa"), "property")).Id);
        Assert.Empty((await AsOwnerAsync("forli trullo")).Groups);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a b")]
    [InlineData("!!")]
    [InlineData("")]
    public async Task Search_ATermWithNoWordOfTwoCharacters_FindsNothing(string term)
    {
        Assert.Empty((await AsOwnerAsync(term)).Groups);
    }

    // ─── Guests ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jose")]
    [InlineData("José")]
    [InlineData("muller")]
    [InlineData("MÜLLER")]
    [InlineData("müller josé")]
    [InlineData("jose.muller@exam")]
    public async Task Guests_ByNameAndEmail_CaseAndAccentsDoNotMatter(string term)
    {
        var result = await AsOwnerAsync(term);

        var guest = Assert.Single(Hits(result, "guest"));
        Assert.Equal(_world.Jose.Id, guest.Id);
        Assert.Equal("José Müller", guest.Title);
        Assert.Equal(SearchDestinations.ShortRentGuest, guest.Destination);
    }

    [Fact]
    public async Task Guests_TheAnswerCarriesTheNameAndAMaskedEmail_NeverThePhoneTheDocumentOrTheWholeAddress()
    {
        var everything = new[] { "jose", "rossi", "maria", "mario", "muller", "example", "carla" };
        foreach (var term in everything)
        {
            var json = JsonSerializer.Serialize(await AsOwnerAsync(term));

            foreach (var secret in new[] { "+391234567890", "+390612345678", "+39333111222", "AX1234567", "CA00000AA", "jose.muller@example.com", "maria.rossi@example.com", "mario.rossi@example.com", "carla@example.com", "Monaco" })
                Assert.DoesNotContain(secret, json);
        }

        var guest = Assert.Single(Hits(await AsOwnerAsync("jose"), "guest"));
        Assert.Equal("j***@example.com", guest.Subtitle);
    }

    [Fact]
    public async Task Guests_ASurnameSharedByTwo_FindsBoth_OfThisOrgOnly()
    {
        var result = await AsOwnerAsync("rossi");

        // The Maria Rossi of the other org is not here, and the order is the folded one (Maria before Mario).
        Assert.Equal([_world.MariaRossi.Id, _world.MarioRossi.Id], Ids(result, "guest"));
        Assert.DoesNotContain(_world.ForeignMaria.Id, result.Groups.SelectMany(g => g.Items).Select(h => h.Id));
        Assert.Equal(["booking", "guest", "supplier"], Types(result));
    }

    [Fact]
    public async Task Guests_FirstNameAndSurnameAsPrefixes_FindTheTwoGuests()
    {
        Assert.Equal([_world.MariaRossi.Id, _world.MarioRossi.Id], Ids(await AsOwnerAsync("mar ros"), "guest"));
        Assert.Equal([_world.MarioRossi.Id], Ids(await AsOwnerAsync("mario ros"), "guest"));
    }

    [Theory]
    [InlineData("anonymized")]
    [InlineData("elena")]
    [InlineData("cancellata elena")]
    public async Task Guests_AnAnonymizedOrDeletedGuest_IsNeverFound(string term)
    {
        Assert.Empty(Hits(await AsOwnerAsync(term), "guest"));
    }

    [Fact]
    public async Task Guests_AGuestWithNoStay_IsFoundByTheWholeOrgOnly()
    {
        Assert.Equal(_world.WithoutStay.Id, Assert.Single(Hits(await AsOwnerAsync("carla"), "guest")).Id);
        Assert.Empty(Hits(await SearchAsync("carla", _world.Everything(_world.Collaborator)), "guest"));
        Assert.Empty(Hits(await SearchAsync("carla", _world.Everything(_world.OwnedByOwner)), "guest"));
    }

    // ─── Bookings ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("7K3M9-PQ2XV")]
    [InlineData("7k3m9pq2xv")]
    [InlineData("7K3M9 PQ2XV")]
    [InlineData("7K3M")]
    [InlineData("7k3m9-pq")]
    public async Task Bookings_ByCode_TheFormShownToPeopleOrAnyPrefixOfIt(string term)
    {
        var result = await AsOwnerAsync(term);

        var hit = Assert.Single(Hits(result, "booking"));
        Assert.Equal(_world.StayJose.Id, hit.Id);
        Assert.Equal("José Müller", hit.Title);
        Assert.Equal("7K3M9-PQ2XV" + GlobalSearchService.SubtitleSeparator + "Trullo Bianco", hit.Subtitle);
        Assert.Equal(" · ", GlobalSearchService.SubtitleSeparator);
        Assert.Equal(SearchDestinations.ShortRentBooking, hit.Destination);
    }

    [Fact]
    public async Task Bookings_ACodeWithNoDigit_IsLookedUpOnlyWhole()
    {
        // "ZXCV12345K" has digits; a name made of the letters of the alphabet is not taken for the beginning of a code.
        Assert.Equal(_world.StayMarioAtTrullo.Id, Assert.Single(Hits(await AsOwnerAsync("ZXCV12"), "booking")).Id);
        Assert.Empty(Hits(await AsOwnerAsync("zxcv"), "booking"));
    }

    [Fact]
    public async Task Bookings_ByGuest_LatestCheckInFirst_AndTheCodeIsNotRequired()
    {
        var result = await AsOwnerAsync("rossi");

        Assert.Equal(
            [_world.StayMarioAtCasaBella.Id, _world.StayMarioAtTrullo.Id, _world.StayMaria.Id],
            Ids(result, "booking"));
        Assert.DoesNotContain(_world.StayForeign.Id, Ids(result, "booking"));
    }

    [Fact]
    public async Task Bookings_AGuestThatWasAnonymized_ShowsTheCodeInsteadOfTheName()
    {
        var result = await AsOwnerAsync("QWERT12345");

        var hit = Assert.Single(Hits(result, "booking"));
        Assert.Equal(_world.StayAnonymized.Id, hit.Id);
        Assert.Equal("QWERT-12345", hit.Title);
        Assert.Equal("Trullo Bianco", hit.Subtitle);
        Assert.DoesNotContain("ANONYMIZED", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Bookings_OfAnotherOrg_AreNeverFound_ByCodeOrByGuest()
    {
        Assert.Empty(Hits(await AsOwnerAsync("XYZ9876543"), "booking"));
        var otherOrg = await SearchAsync("XYZ9876543", _world.Everything(_world.OtherOrg));
        Assert.Equal(_world.StayForeign.Id, Assert.Single(Hits(otherOrg, "booking")).Id);
    }

    [Fact]
    public async Task Bookings_FoundByTheGuestAndByTheCodeAtOnce_AreToldOnce()
    {
        // A guest whose first name is a beginning of a booking code, with a stay that has that code: both ways find the stay.
        var guest = new Casazen.Core.Entities.Guest { OrgId = _world.OrgId, FirstName = "B2C3D4", LastName = "Prova", Email = "prova@example.com" };
        _db.Guests.Add(guest);
        await _db.SaveChangesAsync();
        var stay = new Casazen.Core.Entities.Booking
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Trullo.Id,
            GuestId = guest.Id,
            BookingCode = "B2C3D4E5F6",
            CheckInDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 1,
        };
        _db.Bookings.Add(stay);
        await _db.SaveChangesAsync();
        await SearchKeysForTests.ComputeAsync(_db);

        var result = await AsOwnerAsync("b2c3d4");

        // The stay is found through its guest and through its code (B2C3D4...): once. The stay of Maria has the code A1B2C3D4E5, no.
        Assert.Equal([stay.Id], Ids(result, "booking"));
    }

    // ─── Leases ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("verdi")]
    [InlineData("giulia")]
    [InlineData("paolo verdi")]
    public async Task Leases_ByTheNameOfATenant_TheTenantsAreTheTitle(string term)
    {
        var result = await AsOwnerAsync(term);

        var hit = Assert.Single(Hits(result, "lease"));
        Assert.Equal(_world.LeaseVerdi.Id, hit.Id);
        // The third tenant was anonymized: it is neither found nor shown. The other org's lease with the same name is not here.
        Assert.Equal("Giulia Verdi, Paolo Verdi", hit.Title);
        Assert.Equal("Loft Navigli", hit.Subtitle);
        Assert.Equal(SearchDestinations.LongRentLease, hit.Destination);
    }

    [Theory]
    [InlineData("bianchi")]
    [InlineData("chiara")]
    [InlineData("proprietario")]
    [InlineData("titolare")]
    public async Task Leases_ALandlordOrAnAnonymizedTenant_IsNeverFound(string term)
    {
        Assert.Empty(Hits(await AsOwnerAsync(term), "lease"));
    }

    [Fact]
    public async Task Leases_NoFiscalCodeOrEmailOfAPartyIsEverReturned()
    {
        var json = JsonSerializer.Serialize(await AsOwnerAsync("verdi"));

        Assert.DoesNotContain("RSSMRA80A01H501Z", json);
        Assert.DoesNotContain("giulia.verdi@example.com", json);
    }

    // ─── Requests and suppliers ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServiceRequests_ByTheNameOfTheService_ShowThePropertyAndOpenInTheirArea()
    {
        var hit = Assert.Single(Hits(await AsOwnerAsync("pulizia"), "service-request"));

        Assert.Equal(_world.Cleaning.Id, hit.Id);
        Assert.Equal("Pulizia finale", hit.Title);
        Assert.Equal("Trullo Bianco", hit.Subtitle);
        Assert.Equal(SearchDestinations.ShortRentServiceRequest, hit.Destination);

        var boiler = Assert.Single(Hits(await AsOwnerAsync("caldaia"), "service-request"));
        Assert.Equal((_world.Boiler.Id, SearchDestinations.LongRentServiceRequest), (boiler.Id, boiler.Destination));
    }

    [Fact]
    public async Task ServiceRequests_ByCategoryCode_ShowTheCategoryInTheLanguageOfTheCaller()
    {
        foreach (var (culture, name) in new[] { ("it-IT", "Italian"), ("en-GB", "English") })
        {
            var info = CultureInfo.GetCultureInfo(culture);
            var result = await SearchAsync("plumbing", _world.Everything(), culture: info);

            var withoutName = Assert.Single(Hits(result, "service-request"), h => h.Id == _world.Plumbing.Id);
            Assert.True(
                EmailTemplates.ServiceCategoryLabel(info, "plumbing") == withoutName.Title,
                $"{name}: the title is the label of the category");
            Assert.NotEqual("plumbing", withoutName.Title);
        }
    }

    [Fact]
    public async Task ServiceRequests_OfAnotherOrg_AreNeverFound_AndLongTermOnesNeedTheLongRentPermission()
    {
        Assert.DoesNotContain(_world.ForeignRequest.Id, Ids(await AsOwnerAsync("pulizia"), "service-request"));

        var shortOnly = await SearchAsync("caldaia", new HostSearchAccess(_world.OrgWide, true, false, true, true, true));
        Assert.Empty(Hits(shortOnly, "service-request"));
        var longOnly = await SearchAsync("pulizia", new HostSearchAccess(_world.OrgWide, false, true, true, true, true));
        Assert.Empty(Hits(longOnly, "service-request"));
    }

    [Fact]
    public async Task Suppliers_OnlyTheActiveOnesTheOrgAlreadyAskedSomething()
    {
        var plumbers = Assert.Single(Hits(await AsOwnerAsync("idraulica"), "supplier"));
        Assert.Equal(_world.PlumbersOrgId, plumbers.Id);
        Assert.Equal("Idraulica Rossi S.r.l.", plumbers.Title);
        Assert.Equal(EmailTemplates.ServiceCategoryLabel(Italian, "plumbing"), plumbers.Subtitle);
        Assert.Equal(SearchDestinations.ShortRentSupplier, plumbers.Destination);

        // Suspended (it was asked something) and idle (it never was): neither is found.
        Assert.DoesNotContain(_world.SuspendedOrgId, Ids(await AsOwnerAsync("idraulica"), "supplier"));
        Assert.Empty(Hits(await AsOwnerAsync("giardini"), "supplier"));
        Assert.Equal(_world.CleanersOrgId, Assert.Single(Hits(await AsOwnerAsync("splendore"), "supplier")).Id);
    }

    [Fact]
    public async Task Suppliers_AreAShortRentMatter_TheMarketplaceOfTheHostAsksPropertyReadThere()
    {
        var longOnly = await SearchAsync("splendore", new HostSearchAccess(_world.OrgWide, false, true, true, true, true));

        Assert.Empty(Hits(longOnly, "supplier"));
    }

    // ─── The supplier's inbox ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SupplierRequests_TheInboxOfTheSupplier_ShowsTheComuneOfTheWork()
    {
        var result = await SearchAsync("plumbing", host: null, supplierOrgId: _world.PlumbersOrgId);

        Assert.Equal(["supplier-request"], Types(result));
        var hit = Assert.Single(result.Groups[0].Items);
        Assert.Equal(_world.Plumbing.Id, hit.Id);
        Assert.Equal(EmailTemplates.ServiceCategoryLabel(Italian, "plumbing"), hit.Title);
        // The comune of the property, which is what the supplier sees before it takes the request, not the property's name.
        Assert.Equal("Forlì", hit.Subtitle);
        Assert.Equal(SearchDestinations.SupplierRequest, hit.Destination);

        var boiler = Assert.Single(Hits(await SearchAsync("caldaia", null, _world.PlumbersOrgId), "supplier-request"));
        Assert.Equal((_world.Boiler.Id, "Milano"), (boiler.Id, boiler.Subtitle));
    }

    [Fact]
    public async Task SupplierRequests_NeverTheRequestsOfAnotherSupplier_NorHostGroupsWithoutAHostAccess()
    {
        // The cleaners got "Pulizia finale" (twice, from two orgs); the plumbers did not.
        Assert.Empty((await SearchAsync("pulizia", null, _world.PlumbersOrgId)).Groups);
        Assert.Equal(2, Hits(await SearchAsync("pulizia", null, _world.CleanersOrgId), "supplier-request").Count);

        // A supplier-only caller has no host access: the properties and guests of the org the requests come from are not searched.
        Assert.Empty((await SearchAsync("trullo", null, _world.CleanersOrgId)).Groups);
        Assert.Empty((await SearchAsync("rossi", null, _world.CleanersOrgId)).Groups);
    }

    [Fact]
    public async Task SupplierRequests_ADualCaller_GetsTheHostGroupsAndItsInbox()
    {
        var result = await SearchAsync("pulizia", _world.Everything(), supplierOrgId: _world.CleanersOrgId);

        Assert.Equal(["service-request", "supplier-request"], Types(result));
    }

    // ─── The org boundary and the collaborator ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Org_TheOtherOrgFindsItsOwnDataOnly()
    {
        var result = await SearchAsync("trullo", _world.Everything(_world.OtherOrg));

        Assert.Equal(_world.Foreign.Id, Assert.Single(Hits(result, "property")).Id);
        Assert.Equal(_world.ForeignMaria.Id, Assert.Single(Hits(await SearchAsync("maria", _world.Everything(_world.OtherOrg)), "guest")).Id);
        Assert.Equal(_world.LeaseForeign.Id, Assert.Single(Hits(await SearchAsync("verdi", _world.Everything(_world.OtherOrg)), "lease")).Id);
    }

    [Fact]
    public async Task Collaborator_LimitedToOneProperty_FindsOnlyThatOne_AndWhatBelongsToIt()
    {
        var access = _world.Everything(_world.Collaborator);

        Assert.Equal([_world.Trullo.Id], Ids(await SearchAsync("trullo", access), "property"));
        Assert.Empty((await SearchAsync("casa", access)).Groups);
        Assert.Empty((await SearchAsync("forli", access)).Groups);
        Assert.Empty((await SearchAsync("loft", access)).Groups);
    }

    [Fact]
    public async Task Collaborator_FindsTheGuestsAndStaysOfItsPropertyOnly()
    {
        var access = _world.Everything(_world.Collaborator);
        var rossi = await SearchAsync("rossi", access);

        // Maria Rossi stayed only at the property it cannot reach: she is not found, nor her stay; Mario stayed at both: only the stay at Trullo.
        Assert.Equal([_world.MarioRossi.Id], Ids(rossi, "guest"));
        Assert.Equal([_world.StayMarioAtTrullo.Id], Ids(rossi, "booking"));
        Assert.Empty(Hits(rossi, "supplier"));

        Assert.Equal([_world.Jose.Id], Ids(await SearchAsync("jose", access), "guest"));
        Assert.Equal([_world.StayAnonymized.Id], Ids(await SearchAsync("QWERT12345", access), "booking"));
        Assert.Empty(Hits(await SearchAsync("A1B2C3D4E5", access), "booking"));
        Assert.Empty(Hits(await SearchAsync("maria", access), "guest"));
    }

    [Fact]
    public async Task Collaborator_FindsTheLeasesRequestsAndSuppliersOfItsPropertyOnly()
    {
        var access = _world.Everything(_world.Collaborator);

        Assert.Equal([_world.LeaseNeri.Id], Ids(await SearchAsync("neri", access), "lease"));
        Assert.Empty((await SearchAsync("verdi", access)).Groups);
        Assert.Equal([_world.Cleaning.Id], Ids(await SearchAsync("pulizia", access), "service-request"));
        Assert.Empty((await SearchAsync("caldaia", access)).Groups);
        Assert.Equal([_world.CleanersOrgId], Ids(await SearchAsync("splendore", access), "supplier"));
        Assert.Empty((await SearchAsync("idraulica", access)).Groups);
    }

    [Fact]
    public async Task Collaborator_GivenNothing_FindsNothing()
    {
        var nobody = new HostScope(_world.OrgId, GrantedToUserId: "auth0|nessun-immobile");

        foreach (var term in new[] { "trullo", "casa", "rossi", "jose", "verdi", "pulizia", "idraulica", "7K3M9PQ2XV" })
            Assert.Empty((await SearchAsync(term, _world.Everything(nobody))).Groups);
    }

    [Fact]
    public async Task AnAccountInNoTeam_ReachesTheStaysOfThePropertiesItCreated()
    {
        var access = _world.Everything(_world.OwnedByOwner);

        Assert.Equal([_world.Trullo.Id], Ids(await SearchAsync("trullo", access), "property"));
        Assert.Equal([_world.MariaRossi.Id, _world.MarioRossi.Id], Ids(await SearchAsync("rossi", access), "guest"));

        // Created by somebody else: not its property, so not its guests either.
        var other = new HostScope(_world.OrgId, OwnerId: "auth0|un-altro");
        Assert.Empty((await SearchAsync("trullo", _world.Everything(other))).Groups);
        Assert.Empty(Hits(await SearchAsync("rossi", _world.Everything(other)), "guest"));
    }

    // ─── Permissions ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Permissions_AGroupThatTheCallerMayNotReadIsNotThere_EvenWhenRowsMatch()
    {
        var withoutBookings = await SearchAsync("jose", new HostSearchAccess(_world.OrgWide, true, true, false, true, true));
        Assert.Equal(["guest"], Types(withoutBookings));

        var withoutGuests = await SearchAsync("jose", new HostSearchAccess(_world.OrgWide, true, true, true, false, true));
        Assert.Equal(["booking"], Types(withoutGuests));

        var withoutLeases = await SearchAsync("verdi", new HostSearchAccess(_world.OrgWide, true, true, true, true, false));
        Assert.Empty(withoutLeases.Groups);

        var nothing = await SearchAsync("rossi", new HostSearchAccess(_world.OrgWide, false, false, false, false, false));
        Assert.Empty(nothing.Groups);
    }

    [Fact]
    public async Task Permissions_ACallerWithNoAccessAtAll_FindsNothing()
    {
        Assert.Empty((await SearchAsync("trullo", host: null)).Groups);
    }

    // ─── Limits ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Limit_IsPerGroup_AndHasMoreSaysThereIsMore()
    {
        var one = await AsOwnerAsync("rossi", limit: 1);
        Assert.All(one.Groups, group => Assert.True(group.Items.Count <= 1));
        Assert.True(one.Groups.Single(g => g.Type == "guest").HasMore);
        Assert.True(one.Groups.Single(g => g.Type == "booking").HasMore);
        Assert.False(one.Groups.Single(g => g.Type == "supplier").HasMore);

        var exactly = await AsOwnerAsync("rossi", limit: 2);
        Assert.False(exactly.Groups.Single(g => g.Type == "guest").HasMore);
        Assert.Equal(2, exactly.Groups.Single(g => g.Type == "guest").Items.Count);
        Assert.True(exactly.Groups.Single(g => g.Type == "booking").HasMore);
    }

    [Fact]
    public async Task Limit_IsBoundedByTheService_WhateverTheCallerAsks()
    {
        var many = await SearchAsync("rossi", _world.Everything(), limit: 1_000);
        Assert.All(many.Groups, group => Assert.True(group.Items.Count <= SearchLimits.MaxLimit));

        var none = await SearchAsync("rossi", _world.Everything(), limit: 0);
        Assert.All(none.Groups, group => Assert.True(group.Items.Count <= 1));
    }

    [Fact]
    public async Task Groups_ComeInTheFixedOrder_AndOnlyTheOnesWithResults()
    {
        var result = await AsOwnerAsync("rossi");
        var order = Types(result).Select(type => SearchTypes.All.ToList().IndexOf(type)).ToList();

        Assert.Equal(["booking", "guest", "supplier"], Types(result));
        Assert.Equal(order.Order(), order);
        Assert.All(result.Groups, group => Assert.NotEmpty(group.Items));
    }
}
