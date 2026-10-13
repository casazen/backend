using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="OrgSiteDocumentService"/> (BK-14, A3-21): immutable versions numbered per org and kind, withdrawal, nothing
/// stored for an invalid input, and the public read that only ever returns what the operator published.
/// </summary>
public class OrgSiteDocumentServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly OrgSiteDocumentService _service;
    private readonly OrgEntity _org;
    private readonly OrgEntity _otherOrg;

    public OrgSiteDocumentServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"org-site-documents-{Guid.NewGuid():N}")
            .Options);
        _org = new OrgEntity { Name = "Villa Parco", DisplayName = "Villa Parco", Slug = "villa-parco", ContactEmail = "host@example.com" };
        _otherOrg = new OrgEntity { Name = "Casa Mare", DisplayName = "Casa Mare", Slug = "casa-mare", ContactEmail = "mare@example.com" };
        _db.Orgs.AddRange(_org, _otherOrg);
        _db.SaveChanges();
        _service = new OrgSiteDocumentService(_db, NullLogger<OrgSiteDocumentService>.Instance, _clock);
    }

    public void Dispose() => _db.Dispose();

    private static OrgSiteDocumentInput Text(string content) => new(OrgSiteDocumentSource.Text, content, null);

    private static OrgSiteDocumentInput Url(string url) => new(OrgSiteDocumentSource.ExternalUrl, null, url);

    [Fact]
    public async Task PublishAsync_FirstText_CreatesVersionOneWithSanitizedHtmlAndAuthor()
    {
        var document = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("# Titolare\nMario Rossi\n\n- nome\n- email"), "auth0|admin");

        Assert.Equal(1, document.Version);
        Assert.Equal(OrgSiteDocumentSource.Text, document.Source);
        Assert.Equal("# Titolare\nMario Rossi\n\n- nome\n- email", document.Content);
        Assert.Equal("<h2>Titolare</h2><p>Mario Rossi</p><ul><li>nome</li><li>email</li></ul>", document.ContentHtml);
        Assert.Null(document.ExternalUrl);
        Assert.Equal(Now.UtcDateTime, document.PublishedAt);
        Assert.Equal("auth0|admin", document.PublishedByUserId);
        Assert.Null(document.WithdrawnAt);
    }

    [Fact]
    public async Task PublishAsync_ExternalUrl_StoresTheNormalizedAddressAndNoText()
    {
        var document = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Terms, Url("  https://Example.test/termini "), null);

        Assert.Equal(OrgSiteDocumentSource.ExternalUrl, document.Source);
        Assert.Equal("https://example.test/termini", document.ExternalUrl);
        Assert.Null(document.Content);
        Assert.Null(document.ContentHtml);
    }

    [Fact]
    public async Task PublishAsync_TextWithAnIgnoredAddress_KeepsOnlyTheFieldOfItsSource()
    {
        var document = await _service.PublishAsync(
            _org.Id,
            OrgSiteDocumentKind.Privacy,
            new OrgSiteDocumentInput(OrgSiteDocumentSource.Text, "Testo", "javascript:alert(1)"),
            null);

        Assert.Null(document.ExternalUrl);
        Assert.Equal("Testo", document.Content);
    }

    [Fact]
    public async Task PublishAsync_ChangedText_AddsANewVersionAndKeepsTheOldOne()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Versione uno"), null);
        _clock.Advance(TimeSpan.FromDays(2));

        var second = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Versione due"), null);

        Assert.Equal(2, second.Version);
        var versions = await _db.OrgSiteDocuments.AsNoTracking().Where(d => d.OrgId == _org.Id).OrderBy(d => d.Version).ToListAsync();
        Assert.Equal(["Versione uno", "Versione due"], versions.Select(d => d.Content));
        Assert.Equal(Now.UtcDateTime, versions[0].PublishedAt);
        Assert.Equal(Now.UtcDateTime.AddDays(2), versions[1].PublishedAt);
    }

    [Fact]
    public async Task PublishAsync_SwitchFromTextToAddress_AddsANewVersion()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Testo"), null);

        var second = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Url("https://example.test/privacy"), null);

        Assert.Equal(2, second.Version);
        Assert.Equal(OrgSiteDocumentSource.ExternalUrl, second.Source);
    }

    [Fact]
    public async Task PublishAsync_SameTextAsTheOneShown_ChangesNothing()
    {
        var first = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Stesso testo"), null);

        var again = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Stesso testo\r\n"), "auth0|other");

        Assert.Equal(first.Version, again.Version);
        Assert.Equal(1, await _db.OrgSiteDocuments.CountAsync());
    }

    [Fact]
    public async Task PublishAsync_SameTextAfterWithdrawal_PublishesItAgainAsANewVersion()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Testo"), null);
        await _service.WithdrawAsync(_org.Id, OrgSiteDocumentKind.Privacy);

        var again = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Testo"), null);

        Assert.Equal(2, again.Version);
        Assert.NotNull(await _service.GetPublishedAsync(_org.Id, OrgSiteDocumentKind.Privacy));
    }

    [Fact]
    public async Task PublishAsync_BothKinds_AreNumberedIndependently()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Privacy"), null);
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Privacy 2"), null);

        var terms = await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Terms, Text("Termini"), null);

        Assert.Equal(1, terms.Version);
    }

    [Fact]
    public async Task PublishAsync_TwoOrgs_AreNumberedIndependentlyAndDoNotSeeEachOther()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Villa Parco"), null);

        var other = await _service.PublishAsync(_otherOrg.Id, OrgSiteDocumentKind.Privacy, Text("Casa Mare"), null);

        Assert.Equal(1, other.Version);
        Assert.Equal("Villa Parco", (await _service.GetPublishedAsync(_org.Id, OrgSiteDocumentKind.Privacy))!.Content);
        Assert.Equal("Casa Mare", (await _service.GetPublishedAsync(_otherOrg.Id, OrgSiteDocumentKind.Privacy))!.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("[x](javascript:alert(1))")]
    public async Task PublishAsync_InvalidText_ThrowsAndStoresNothing(string content)
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text(content), null));

        Assert.StartsWith("org_document_", ex.Code);
        Assert.Equal(0, await _db.OrgSiteDocuments.CountAsync());
    }

    [Theory]
    [InlineData("http://example.test/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task PublishAsync_InvalidAddress_ThrowsAndKeepsTheCurrentVersion(string url)
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Testo valido"), null);

        await Assert.ThrowsAsync<DomainRuleException>(() => _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Url(url), null));

        var stored = Assert.Single(await _db.OrgSiteDocuments.AsNoTracking().ToListAsync());
        Assert.Equal("Testo valido", stored.Content);
    }

    [Fact]
    public async Task WithdrawAsync_CurrentVersion_StopsShowingItButKeepsTheHistory()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Terms, Text("Termini"), null);
        _clock.Advance(TimeSpan.FromHours(3));

        var withdrawn = await _service.WithdrawAsync(_org.Id, OrgSiteDocumentKind.Terms);

        Assert.True(withdrawn);
        Assert.Null(await _service.GetPublishedAsync(_org.Id, OrgSiteDocumentKind.Terms));
        var stored = Assert.Single(await _db.OrgSiteDocuments.AsNoTracking().ToListAsync());
        Assert.Equal(Now.UtcDateTime.AddHours(3), stored.WithdrawnAt);
    }

    [Fact]
    public async Task WithdrawAsync_NothingShown_ReturnsFalse()
    {
        Assert.False(await _service.WithdrawAsync(_org.Id, OrgSiteDocumentKind.Terms));

        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Terms, Text("Termini"), null);
        Assert.True(await _service.WithdrawAsync(_org.Id, OrgSiteDocumentKind.Terms));
        Assert.False(await _service.WithdrawAsync(_org.Id, OrgSiteDocumentKind.Terms));
    }

    [Fact]
    public async Task WithdrawAsync_AnotherOrgsDocument_IsNotTouched()
    {
        await _service.PublishAsync(_otherOrg.Id, OrgSiteDocumentKind.Terms, Text("Termini altrui"), null);

        Assert.False(await _service.WithdrawAsync(_org.Id, OrgSiteDocumentKind.Terms));

        Assert.NotNull(await _service.GetPublishedAsync(_otherOrg.Id, OrgSiteDocumentKind.Terms));
    }

    [Fact]
    public async Task GetPublishedAsync_NeverPublished_ReturnsNull()
    {
        Assert.Null(await _service.GetPublishedAsync(_org.Id, OrgSiteDocumentKind.Privacy));
    }

    [Fact]
    public async Task GetPublishedAsync_RowWithUnsafeHtml_IsSanitizedOnRead()
    {
        // A row written by other means (script, import, manual fix) never reaches a page unsanitized.
        _db.OrgSiteDocuments.Add(new Casazen.Core.Entities.OrgSiteDocument
        {
            OrgId = _org.Id,
            Kind = OrgSiteDocumentKind.Privacy,
            Version = 1,
            Source = OrgSiteDocumentSource.Text,
            Content = "x",
            ContentHtml = "<p onclick=\"x()\">ciao</p><script>alert(1)</script><a href=\"javascript:alert(1)\">x</a>",
            PublishedAt = Now.UtcDateTime,
        });
        await _db.SaveChangesAsync();

        var document = await _service.GetPublishedAsync(_org.Id, OrgSiteDocumentKind.Privacy);

        Assert.NotNull(document);
        Assert.DoesNotContain("script", document.ContentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", document.ContentHtml);
        Assert.DoesNotContain("javascript:", document.ContentHtml);
        Assert.Contains("ciao", document.ContentHtml);
    }

    [Fact]
    public async Task GetStatesAsync_NothingPublished_ReturnsBothKindsWithoutCurrentVersion()
    {
        var states = await _service.GetStatesAsync(_org.Id);

        Assert.Equal([OrgSiteDocumentKind.Privacy, OrgSiteDocumentKind.Terms], states.Select(s => s.Kind));
        Assert.All(states, s =>
        {
            Assert.Null(s.Current);
            Assert.Empty(s.History);
        });
    }

    [Fact]
    public async Task GetStatesAsync_WithVersions_ReturnsCurrentWithTextAndHistoryNewestFirstWithoutText()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Uno"), null);
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Due"), null);
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Url("https://example.test/privacy"), null);
        await _service.PublishAsync(_otherOrg.Id, OrgSiteDocumentKind.Privacy, Text("Altrui"), null);

        var privacy = (await _service.GetStatesAsync(_org.Id)).Single(s => s.Kind == OrgSiteDocumentKind.Privacy);

        Assert.Equal(3, privacy.Current!.Version);
        Assert.Equal("https://example.test/privacy", privacy.Current.ExternalUrl);
        Assert.Equal([3, 2, 1], privacy.History.Select(d => d.Version));
        Assert.All(privacy.History, d =>
        {
            Assert.Null(d.Content);
            Assert.Null(d.ContentHtml);
        });
    }

    [Fact]
    public async Task GetStatesAsync_ManyVersions_ListsOnlyTheMostRecent()
    {
        for (var i = 1; i <= OrgSiteDocumentService.HistoryLimit + 5; i++)
            await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Terms, Text($"Versione {i}"), null);

        var terms = (await _service.GetStatesAsync(_org.Id)).Single(s => s.Kind == OrgSiteDocumentKind.Terms);

        Assert.Equal(OrgSiteDocumentService.HistoryLimit + 5, terms.Current!.Version);
        Assert.Equal(OrgSiteDocumentService.HistoryLimit, terms.History.Count);
        Assert.Equal(OrgSiteDocumentService.HistoryLimit + 5, terms.History[0].Version);
    }

    [Fact]
    public async Task GetVersionAsync_ExistingVersion_ReturnsItWithItsText()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Uno"), null);
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Due"), null);

        var first = await _service.GetVersionAsync(_org.Id, OrgSiteDocumentKind.Privacy, 1);

        Assert.Equal("Uno", first!.Content);
    }

    [Fact]
    public async Task GetVersionAsync_UnknownVersionKindOrOtherOrg_ReturnsNull()
    {
        await _service.PublishAsync(_org.Id, OrgSiteDocumentKind.Privacy, Text("Uno"), null);

        Assert.Null(await _service.GetVersionAsync(_org.Id, OrgSiteDocumentKind.Privacy, 2));
        Assert.Null(await _service.GetVersionAsync(_org.Id, OrgSiteDocumentKind.Terms, 1));
        Assert.Null(await _service.GetVersionAsync(_otherOrg.Id, OrgSiteDocumentKind.Privacy, 1));
    }
}
