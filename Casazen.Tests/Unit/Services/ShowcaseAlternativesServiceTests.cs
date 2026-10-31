using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10c: the other suppliers offered to the customer of a request nobody answered. At most three, in the order of the one ranking of the platform,
/// that cover the comune and the category (the ranking only lists those), whose showcase is published, not paused and takes bookings; never the supplier
/// that did not answer, never one that cannot be read, nothing at all without a place, a category or the online payments, and the failure of the ranking
/// is an empty answer, not an error. What leaves is what the showcase says and what is measured: no contact, no score, no id.
/// </summary>
public class ShowcaseAlternativesServiceTests
{
    private const string Category = ServiceCategories.Cleaning;

    private readonly Guid _original = Guid.NewGuid();
    private readonly List<SupplierProfile> _ranked = [];
    private readonly Dictionary<Guid, PublishedShowcasePage> _pages = [];
    private readonly Dictionary<Guid, PublicSupplierExtension> _extensions = [];
    private readonly HashSet<Guid> _unreadable = [];
    private readonly Mock<ISupplierMatchService> _match = new();
    private readonly Mock<IPublicSupplierShowcaseService> _showcase = new();
    private readonly TestFeatureFlags _flags = new(FeatureFlags.SupplierOnlinePayments);

    public ShowcaseAlternativesServiceTests()
    {
        _match
            .Setup(m => m.RankActiveAsync(It.IsAny<ComuneTarget>(), It.IsAny<string>(), It.IsAny<ServiceRequestUrgency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<RankedSupplier>)_ranked.Select((profile, index) => new RankedSupplier(profile, 90 - index, 0)).ToList());
        _showcase
            .Setup(s => s.GetPublishedPageAsync(It.IsAny<SupplierProfile>(), It.IsAny<CancellationToken>()))
            .Returns((SupplierProfile profile, CancellationToken _) =>
                _unreadable.Contains(profile.OrgId)
                    ? throw new InvalidOperationException("the page cannot be read")
                    : Task.FromResult<PublishedShowcasePage?>(_pages.GetValueOrDefault(profile.OrgId)));
        _showcase
            .Setup(s => s.GetExtensionAsync(It.IsAny<SupplierProfile>(), It.IsAny<CancellationToken>()))
            .Returns((SupplierProfile profile, CancellationToken _) =>
                Task.FromResult(_extensions.GetValueOrDefault(profile.OrgId) ?? new PublicSupplierExtension([], null)));
    }

    private ShowcaseAlternativesService Service() =>
        new(_match.Object, _showcase.Object, _flags, NullLogger<ShowcaseAlternativesService>.Instance);

    private ShowcaseAlternativesQuery Query(string category = Category, string? istat = "015146", string? city = "Milano") =>
        new(_original, category, istat, city);

    /// <summary>A supplier of the ranking, with the showcase it has: published and open by default.</summary>
    private SupplierProfile Add(
        string name,
        bool published = true,
        bool paused = false,
        bool bookingsOpen = true,
        bool verified = true,
        int? median = 30,
        string? cover = "https://storage.test/cover.jpg",
        IReadOnlyList<SupplierPublicService>? services = null)
    {
        var profile = new SupplierProfile
        {
            OrgId = Guid.NewGuid(),
            LegalName = name,
            Status = SupplierStatus.Active,
            ShowcaseSlug = name.ToLowerInvariant().Replace(' ', '-'),
            Email = $"{name.Replace(' ', '.')}@private.example",
            Phone = "+39 02 1234567",
        };
        _ranked.Add(profile);
        if (published)
        {
            _pages[profile.OrgId] = new PublishedShowcasePage(
                new ShowcaseContentDocument { Cover = cover is null ? null : new ShowcaseCover(cover, "La nostra squadra") },
                paused,
                bookingsOpen,
                verified,
                JobsDone: null,
                180,
                bookingsOpen,
                null);
            _extensions[profile.OrgId] = new PublicSupplierExtension(services ?? [Priced("pulizia-casa", "Pulizia casa", 8000)], median);
        }

        return profile;
    }

    private static SupplierPublicService Priced(string slug, string name, int? cents, string category = Category) =>
        new(slug, name, category, null, null, cents, SupplierServicePriceUnit.PerJob, true, cents is null, 120, [], [], [], [], null, SupplierServiceWeekdays.AllMask);

    // ─── Who is offered, and in which order ───

    [Fact]
    public async Task Find_OffersTheSuppliersInTheOrderOfTheRanking_WithWhatTheirShowcaseSays()
    {
        Add("Pulizie Rossi", median: 45);
        Add("Casa Pronta", verified: false, median: null, cover: null, services: [Priced("su-misura", "Pulizie su misura", null)]);
        Add("Lucida Tutto");

        var found = await Service().FindAsync(Query());

        Assert.Equal(new[] { "Pulizie Rossi", "Casa Pronta", "Lucida Tutto" }, found.Select(a => a.Name).ToArray());
        var first = found[0];
        Assert.Equal("pulizie-rossi", first.Slug);
        Assert.Equal("https://storage.test/cover.jpg", first.CoverUrl);
        Assert.Equal("La nostra squadra", first.CoverAlt);
        Assert.True(first.Verified);
        Assert.Equal(45, first.MedianResponseMinutes);
        Assert.Equal(new ShowcaseAlternativeService("pulizia-casa", "Pulizia casa", 8000, SupplierServicePriceUnit.PerJob, false), first.Service);
        var second = found[1];
        Assert.False(second.Verified);
        Assert.Null(second.MedianResponseMinutes);
        Assert.Null(second.CoverUrl);
        Assert.Null(second.Service!.FromPriceCents);
        Assert.True(second.Service.RequiresQuote);
    }

    [Fact]
    public async Task Find_NeverOffersTheSupplierThatDidNotAnswer()
    {
        _ranked.Add(new SupplierProfile { OrgId = _original, LegalName = "Quello Che Tace", Status = SupplierStatus.Active, ShowcaseSlug = "quello-che-tace" });
        _pages[_original] = new PublishedShowcasePage(new ShowcaseContentDocument(), false, true, true, null, 180, true, null);
        Add("Pulizie Rossi");

        var found = await Service().FindAsync(Query());

        Assert.Equal("Pulizie Rossi", Assert.Single(found).Name);
    }

    [Fact]
    public async Task Find_SkipsTheOnesThatDoNotTakeBookingsNow_AndKeepsTheOthers()
    {
        Add("Senza Vetrina", published: false);
        Add("In Pausa", paused: true);
        Add("Prenotazioni Chiuse", bookingsOpen: false);
        Add("Pulizie Rossi");

        var found = await Service().FindAsync(Query());

        Assert.Equal("Pulizie Rossi", Assert.Single(found).Name);
    }

    [Fact]
    public async Task Find_OffersAtMostThree()
    {
        foreach (var name in new[] { "Uno", "Due", "Tre", "Quattro", "Cinque" })
            Add(name);

        var found = await Service().FindAsync(Query());

        Assert.Equal(new[] { "Uno", "Due", "Tre" }, found.Select(a => a.Name).ToArray());
        // The fourth and the fifth were never even read.
        _showcase.Verify(s => s.GetPublishedPageAsync(It.IsAny<SupplierProfile>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Find_LooksAtNoMoreThanTwelveSuppliers_ToFindThemEvenWhenNoneQualifies()
    {
        for (var i = 0; i < 15; i++)
            Add($"Chiuso {i}", bookingsOpen: false);
        Add("Aperto Tardi");

        var found = await Service().FindAsync(Query());

        Assert.Empty(found);
        _showcase.Verify(s => s.GetPublishedPageAsync(It.IsAny<SupplierProfile>(), It.IsAny<CancellationToken>()), Times.Exactly(ShowcaseAlternativesLimits.MaxCandidates));
    }

    [Fact]
    public async Task Find_TheSupplierOfTheRequestDoesNotCountAmongTheTwelve()
    {
        _ranked.Add(new SupplierProfile { OrgId = _original, LegalName = "Quello Che Tace", Status = SupplierStatus.Active, ShowcaseSlug = "quello-che-tace" });
        for (var i = 0; i < 11; i++)
            Add($"Chiuso {i}", bookingsOpen: false);
        Add("Aperto Per Ultimo");

        var found = await Service().FindAsync(Query());

        Assert.Equal("Aperto Per Ultimo", Assert.Single(found).Name);
    }

    [Fact]
    public async Task Find_ASupplierWhoseShowcaseCannotBeRead_IsSkipped_TheOthersStay()
    {
        var broken = Add("Pagina Rotta");
        _unreadable.Add(broken.OrgId);
        Add("Pulizie Rossi");

        var found = await Service().FindAsync(Query());

        Assert.Equal("Pulizie Rossi", Assert.Single(found).Name);
    }

    // ─── When nothing is looked for ───

    [Fact]
    public async Task Find_WithTheOnlinePaymentsOff_NoShowcaseTakesBookings_SoNothingIsLookedFor()
    {
        _flags.Set(FeatureFlags.SupplierOnlinePayments, false);
        Add("Pulizie Rossi");

        var found = await Service().FindAsync(Query());

        Assert.Empty(found);
        _match.Verify(
            m => m.RankActiveAsync(It.IsAny<ComuneTarget>(), It.IsAny<string>(), It.IsAny<ServiceRequestUrgency>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(Category, null, null)]
    [InlineData(Category, " ", "  ")]
    [InlineData("", "015146", "Milano")]
    [InlineData(" ", "015146", "Milano")]
    public async Task Find_WithoutAPlaceOrACategory_IsEmpty(string category, string? istat, string? city)
    {
        Add("Pulizie Rossi");

        var found = await Service().FindAsync(Query(category, istat, city));

        Assert.Empty(found);
    }

    [Fact]
    public async Task Find_LooksForTheComuneByItsCodeAndItsName_AndTheCategoryOfTheRequest()
    {
        Add("Pulizie Rossi");

        await Service().FindAsync(Query(istat: " 015146 ", city: " Milano "));

        _match.Verify(
            m => m.RankActiveAsync(new ComuneTarget("015146", "Milano"), Category, ServiceRequestUrgency.Normal, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Find_ACategoryThatIsNotOneOfTheCodes_IsEmpty()
    {
        _match
            .Setup(m => m.RankActiveAsync(It.IsAny<ComuneTarget>(), "not-a-category", It.IsAny<ServiceRequestUrgency>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException("invalid_service_category", "InvalidServiceCategory"));

        var found = await Service().FindAsync(Query("not-a-category"));

        Assert.Empty(found);
    }

    [Fact]
    public async Task Find_AFailureOfTheRanking_IsAnEmptyAnswer_NotAnError()
    {
        _match
            .Setup(m => m.RankActiveAsync(It.IsAny<ComuneTarget>(), It.IsAny<string>(), It.IsAny<ServiceRequestUrgency>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));

        var found = await Service().FindAsync(Query());

        Assert.Empty(found);
    }

    [Fact]
    public async Task Find_ACancelledCall_IsCancelled_NotSwallowed()
    {
        Add("Pulizie Rossi");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _match
            .Setup(m => m.RankActiveAsync(It.IsAny<ComuneTarget>(), It.IsAny<string>(), It.IsAny<ServiceRequestUrgency>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().FindAsync(Query(), cts.Token));
    }

    // ─── What leaves ───

    [Fact]
    public void WhatLeaves_HasNoContactNoScoreAndNoId()
    {
        var members = typeof(ShowcaseAlternative).GetProperties().Select(p => p.Name).Concat(typeof(ShowcaseAlternativeService).GetProperties().Select(p => p.Name)).ToList();

        foreach (var forbidden in new[] { "Email", "Phone", "OrgId", "Id", "Score", "Rank", "Address", "VatNumber", "Bio" })
            Assert.DoesNotContain(forbidden, members);
    }
}
