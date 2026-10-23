using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04e: the free text <c>q</c> of the inbox searches what the supplier already sees before the take (service name, category, comune)
/// and nothing that the take discloses or that identifies a customer (decision D9, SP-D23: the search by name stays on the console, on
/// the rows it loaded). A booking code matches only as a whole: it is a credential of the customer and the supplier never sees it.
/// </summary>
public class SupplierInboxSearchTests
{
    private const string HostCity = "Lecce";

    // ─── Service name, category, comune ───

    [Theory]
    [InlineData("pulizia")]
    [InlineData("PULIZIA")]
    [InlineData("Pulizia")]
    [InlineData("  appartamento ")]
    [InlineData("pulizia appartamento")]
    [InlineData("zia app")]
    public async Task Text_FindsTheServiceByPartOfItsName_AnyCase(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        var found = await SearchAsync(s, text);

        // The cleaning of the host and the booking of the showcase are for the same service of the catalog.
        Assert.Equal(new[] { world.Cleaning, world.Showcase }.Order(), found.Order());
    }

    [Fact]
    public async Task Text_AServiceNameThatIsNoLongerInTheCatalog_StillMatches_BecauseTheRequestKeepsItsOwnCopy()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        Assert.Equal(new[] { world.Plumbing }, await SearchAsync(s, "perdita"));
    }

    [Theory]
    [InlineData("plumbing")]
    [InlineData("PLUMB")]
    [InlineData("idraulico")]
    [InlineData("Idrau")]
    public async Task Text_FindsTheCategoryByItsCodeOrItsLabel(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        var found = await SearchAsync(s, text);

        Assert.Equal(new[] { world.Plumbing }, found);
    }

    [Theory]
    [InlineData("electrician")] // the English label: not the code ("electrical")
    [InlineData("ELECTRICIAN")]
    [InlineData("elettricista")] // the Italian label
    [InlineData("electrical")] // the code
    public async Task Text_FindsTheCategoryByItsLabelInItalianOrEnglish_EvenWhenTheLabelIsNotTheCode(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);
        var electrical = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.Category = ServiceCategories.Electrical);

        Assert.Equal(new[] { electrical.Id }, await SearchAsync(s, text));
    }

    [Theory]
    [InlineData("giardinaggio")]
    [InlineData("giardin")]
    [InlineData("gardening")]
    [InlineData("GARDEN")]
    public async Task Text_ARequestWithNoServiceName_IsFoundByTheLabelOfItsCategory(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        Assert.Equal(new[] { world.Gardening }, await SearchAsync(s, text));
    }

    [Theory]
    [InlineData("lecce")]
    [InlineData("LECCE")]
    [InlineData("lec")]
    public async Task Text_FindsTheComuneOfTheWork_AnyCase(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        var found = await SearchAsync(s, text);

        Assert.Equal(new[] { world.Cleaning, world.Plumbing, world.Gardening }.Order(), found.Order());
        Assert.DoesNotContain(world.Showcase, found);
    }

    [Fact]
    public async Task Text_FindsTheComuneOfAShowcaseRequest_ThatTheCustomerWrote()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, ShowcaseScenario.City));
        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, "monza"));
    }

    [Fact]
    public async Task Text_ThatMatchesNothing_ReturnsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        Assert.Empty(await SearchAsync(s, "elicottero"));
    }

    // ─── What it never searches ───

    [Theory]
    [InlineData("Riservata")] // the name of the property
    [InlineData("Segretissima")] // the street address
    [InlineData("citofono")] // the host's notes
    [InlineData("Piano 3")] // the floor of a showcase customer
    [InlineData("cassetta")] // the access notes of a showcase customer
    public async Task Text_NeverFindsWhatTheTakeDisclosesNorWhatIsDisclosedAfterIt(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);
        // A taken job: the property, the notes and the address are shown for it (and for the showcase booking, once taken).
        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);
        await s.Service.TakeAsync((await s.ShowcaseRequestsAsync()).Single().Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Empty(await SearchAsync(s, text));
    }

    [Theory]
    [InlineData("rossi")] // the host org ("Casa Rossi") and the private customer ("Mario Rossi")
    [InlineData("mario")]
    [InlineData("example.com")]
    [InlineData("3331234567")]
    public async Task Text_NeverFindsACustomerByName_ThatIsTheConsolesJobOnTheRowsItLoaded(string text)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        Assert.Empty(await SearchAsync(s, text));
    }

    [Fact]
    public async Task Text_APercentOrAnUnderscore_IsTheCharacterItIs_NotAWildcard()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        Assert.Empty(await SearchAsync(s, "%%"));
        Assert.Empty(await SearchAsync(s, "__"));
        Assert.Empty(await SearchAsync(s, "p%a"));
        Assert.Empty(await SearchAsync(s, "pul_zia"));
    }

    [Fact]
    public async Task Text_NeverFindsTheRequestsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ServiceNameSnapshot = "Pulizia segreta del collega", supplierOrgId: other);

        Assert.Empty(await SearchAsync(s, "segreta"));
        Assert.Single(await SearchAsync(s, "segreta", supplierOrgId: other));
    }

    // ─── The booking code ───

    [Fact]
    public async Task BookingCode_AWholeCodeFindsTheRequest_AnyCaseWithOrWithoutTheDash()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);
        var code = world.ShowcaseCode;
        var formatted = BookingCodes.Format(code);

        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, code));
        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, code.ToLowerInvariant()));
        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, formatted));
        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, formatted.ToLowerInvariant()));
        Assert.Equal(new[] { world.Showcase }, await SearchAsync(s, $"  {formatted}  "));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task BookingCode_APartOfTheCode_FindsNothing_SoTheCodeCannotBeFoundOutCharacterByCharacter(int length)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // A code made of digits and letters no word of the service, the category or the comune contains.
        var whole = (await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.PublicCode = "7Z9Q2X4W8V")).Id;

        // Two characters is the shortest search. A part of the code is no code: it is read as a text, and matches nothing.
        Assert.Empty(await SearchAsync(s, "7Z9Q2X4W8V"[..length]));
        Assert.Equal(new[] { whole }, await SearchAsync(s, "7Z9Q2-X4W8V"));
    }

    [Fact]
    public async Task BookingCode_AnotherSuppliersCode_FindsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.PublicCode = "ABCDEFGHJK", supplierOrgId: other);

        Assert.Empty(await SearchAsync(s, "ABCDE-FGHJK"));
        Assert.Single(await SearchAsync(s, "ABCDE-FGHJK", supplierOrgId: other));
    }

    [Fact]
    public async Task BookingCode_ACodeIsNotReadFromTheViewsTheConsoleReceives()
    {
        // The supplier can search by a whole code the customer gives it, but never reads a code out of the console.
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, new SupplierInboxQuery([], null, null, 1, 100));
        var detail = await s.Reader.GetAsync(world.Showcase, s.SupplierOrgId);

        var text = System.Text.Json.JsonSerializer.Serialize(new object?[] { items, detail });
        Assert.DoesNotContain(world.ShowcaseCode, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BookingCodes.Format(world.ShowcaseCode), text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── With the rest of the query ───

    [Fact]
    public async Task Text_IsCombinedWithTheOtherFilters()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);
        var query = new SupplierInboxQuery([], null, null, 1, 100, Text: "pulizia", Source: ServiceRequestSource.Showcase);

        var items = (await s.Reader.ListAsync(s.SupplierOrgId, query)).Items;

        Assert.Equal(new[] { world.Showcase }, items.Select(item => item.Id));
    }

    [Fact]
    public async Task Text_IsCombinedWithTheTabs()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var world = await SeedAsync(s);
        var done = (await s.SeedAsync(ServiceRequestStatus.Completato, r =>
        {
            r.CompletedAt = r.CreatedAt;
            r.ServiceNameSnapshot = "Pulizia finale";
        })).Id;

        var toAnswer = (await s.Reader.ListAsync(
            s.SupplierOrgId,
            new SupplierInboxQuery(SupplierInboxTabs.StatusesOf(SupplierInboxTab.ToAnswer), null, null, 1, 100, Text: "pulizia", AwaitingCustomer: false))).Items;
        var toCollect = (await s.Reader.ListAsync(
            s.SupplierOrgId,
            new SupplierInboxQuery(SupplierInboxTabs.StatusesOf(SupplierInboxTab.ToCollect), null, null, 1, 100, Text: "pulizia"))).Items;

        Assert.Equal(new[] { world.Cleaning, world.Showcase }.Order(), toAnswer.Select(item => item.Id).Order());
        Assert.Equal(new[] { done }, toCollect.Select(item => item.Id));
    }

    [Fact]
    public async Task Text_TheTotalIsTheNumberOfMatches()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await SeedAsync(s);

        var (items, total) = await s.Reader.ListAsync(
            s.SupplierOrgId, new SupplierInboxQuery([], null, null, 1, 1, Text: "lecce"));

        Assert.Equal(3, total);
        Assert.Single(items);
    }

    // ─── helpers ───

    private sealed record World(Guid Cleaning, Guid Plumbing, Guid Gardening, Guid Showcase, string ShowcaseCode);

    /// <summary>
    /// Three new requests of a host in Lecce (a cleaning with the name of the catalog, a plumbing with another, a gardening with none)
    /// and one from the showcase of the supplier, in Monza; every request is for the property "Casa Riservata", host "Casa Rossi".
    /// </summary>
    private static async Task<World> SeedAsync(ServiceRequestScenario s)
    {
        var property = await s.Db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == s.PropertyId);
        property.City = HostCity;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var cleaning = await s.SeedAsync(ServiceRequestStatus.Richiesto, r =>
        {
            r.Category = ServiceCategories.Cleaning;
            r.ServiceNameSnapshot = "Pulizia appartamento";
        });
        var plumbing = await s.SeedAsync(ServiceRequestStatus.Richiesto, r =>
        {
            r.Category = ServiceCategories.Plumbing;
            r.ServiceNameSnapshot = "Riparazione perdita";
        });
        var gardening = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.Category = ServiceCategories.Gardening);

        await s.EnableBookingAsync();
        var (showcase, _) = await s.BookedAsync();
        return new World(cleaning.Id, plumbing.Id, gardening.Id, showcase.Id, showcase.PublicCode!);
    }

    private static async Task<List<Guid>> SearchAsync(ServiceRequestScenario s, string text, Guid? supplierOrgId = null)
    {
        var query = new SupplierInboxQuery([], null, null, 1, 100, Text: text);
        var (items, _) = await s.Reader.ListAsync(supplierOrgId ?? s.SupplierOrgId, query);
        return items.Select(item => item.Id).ToList();
    }
}
