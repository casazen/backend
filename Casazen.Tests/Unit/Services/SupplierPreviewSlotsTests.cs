using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-03b: the supplier's preview "how customers see you" on the real services and an in-memory database. It shows exactly what the
/// public page shows for a published service (the same planner, the same window), and, for the supplier alone, what the public page
/// never does: no cache, a draft or paused service, a supplier that is not active, and whether a comune is one of its zones. Today is
/// Thursday 8 October 2026, 12:00 in Rome; the supplier works Monday to Friday 08-13 and 14-18 and Saturday 08-14.
/// </summary>
public class SupplierPreviewSlotsTests
{
    private const string Slug = "vetrina-preview";

    private static SupplierSlotPreviewService Preview(ServiceRequestScenario s) =>
        new(s.Db, s.Catalog, s.Agenda, ComuneTestServices.Matcher(s.Db), s.Clock);

    private static PublicSupplierShowcaseService Showcase(ServiceRequestScenario s) =>
        new(s.Db, s.Catalog, s.Agenda, new SupplierKpiService(s.Db, s.Clock), ComuneTestServices.Matcher(s.Db), new PublicSupplierSlotCache(s.Clock), s.Clock);

    private static async Task<ServiceRequestScenario> WorldAsync()
    {
        var s = await ServiceRequestScenario.CreateAsync();
        var profile = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == s.SupplierOrgId);
        profile.ShowcaseSlug = Slug;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return s;
    }

    private static List<string> Shape(IEnumerable<PublicSlotDay> days) =>
        days
            .Select(day => $"{day.Date:yyyy-MM-dd}|{day.Available}|{string.Join(",", day.Slots.Select(slot => $"{slot.StartUtc:O}>{slot.EndUtc:O}"))}")
            .ToList();

    [Theory]
    [InlineData(null, null)]
    [InlineData("2026-10-12", 5)]
    [InlineData("2026-10-01", 3)]
    [InlineData("2026-11-05", 10)]
    public async Task Preview_OfAPublishedService_ShowsExactlyTheDaysAndTheSlotsOfThePublicPage(string? from, int? days)
    {
        using var s = await WorldAsync();
        var supplier = (await Showcase(s).FindActiveSupplierAsync(Slug))!;
        var first = from is null ? (DateOnly?)null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture);

        var publicSlots = (await Showcase(s).GetSlotsAsync(supplier, await s.ListingSlugAsync(), first, days))!;
        var preview = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, first, days, comune: null);

        Assert.NotEmpty(preview.Days);
        Assert.Equal(Shape(publicSlots.Days), Shape(preview.Days));
        Assert.Equal(publicSlots.BookableUntil, preview.BookableUntil);
        Assert.Equal(publicSlots.DurationMinutes, preview.DurationMinutes);
        Assert.Equal(publicSlots.ServiceSlug, preview.ServiceSlug);
        Assert.Equal(SupplierServiceListingStatus.Active, preview.ServiceStatus);
    }

    [Fact]
    public async Task Preview_AWindowBeyondTheHorizon_IsAnEmptyWindowThatStillSaysUntilWhenItCanBeBooked()
    {
        using var s = await WorldAsync();

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, new DateOnly(2027, 3, 1), 14, comune: null);

        Assert.Empty(preview.Days);
        Assert.Equal(new DateOnly(2026, 10, 8).AddDays(35), preview.BookableUntil);
    }

    [Theory]
    [InlineData(SupplierServiceListingStatus.Draft)]
    [InlineData(SupplierServiceListingStatus.Paused)]
    public async Task Preview_ADraftOrPausedService_IsComputed_WhereThePublicPageHasNothingToShow(SupplierServiceListingStatus status)
    {
        using var s = await WorldAsync();
        var listingId = await s.AddListingAsync("In lavorazione", status: status);
        var showcase = Showcase(s);
        var supplier = (await showcase.FindActiveSupplierAsync(Slug))!;

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, listingId, null, null, comune: null);

        Assert.Equal(status, preview.ServiceStatus);
        Assert.Equal(ServiceRequestScenario.ServiceMinutes, preview.DurationMinutes);
        Assert.Contains(preview.Days, day => day.Available);
        Assert.Null(await showcase.FindServiceAsync(supplier, await s.ListingSlugAsync(listingId)));
    }

    [Fact]
    public async Task Preview_ADraftWithNoDurationYet_HasNoDayToComputeAndADurationOfZero_NotAnError()
    {
        using var s = await WorldAsync();
        var listingId = await s.AddListingAsync("Senza durata", status: SupplierServiceListingStatus.Draft, durationMinutes: null);

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, listingId, null, null, comune: null);

        Assert.Equal(0, preview.DurationMinutes);
        Assert.Empty(preview.Days);
        Assert.Equal(SupplierServiceListingStatus.Draft, preview.ServiceStatus);
    }

    [Theory]
    [InlineData(SupplierStatus.Pending)]
    [InlineData(SupplierStatus.Suspended)]
    public async Task Preview_ASupplierThatIsNotActive_StillPreviews_ThePublicPageDoesNotKnowIt(SupplierStatus status)
    {
        using var s = await WorldAsync();
        await s.SetSupplierStatusAsync(status);

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, null, null, comune: null);

        Assert.Contains(preview.Days, day => day.Available);
        Assert.Null(await Showcase(s).FindActiveSupplierAsync(Slug));
    }

    [Fact]
    public async Task Preview_HasNoCache_AChangeOfTheAgendaShowsAtOnce_WhereThePublicPlanStaysThirtySeconds()
    {
        using var s = await WorldAsync();
        var showcase = Showcase(s);
        var supplier = (await showcase.FindActiveSupplierAsync(Slug))!;
        var slug = await s.ListingSlugAsync();
        var friday = new DateOnly(2026, 10, 9);
        var tenOClock = ServiceRequestScenario.FridayAt10;

        var publicBefore = (await showcase.GetSlotsAsync(supplier, slug, friday, 1))!;
        var previewBefore = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, friday, 1, comune: null);
        Assert.Contains(previewBefore.Days[0].Slots, slot => slot.StartUtc == tenOClock);
        Assert.Contains(publicBefore.Days[0].Slots, slot => slot.StartUtc == tenOClock);

        await s.Agenda.AddBlockAsync(
            s.SupplierOrgId,
            new SupplierBlockInput(SupplierBusyWindowKind.Block, tenOClock.AddHours(-2), tenOClock.AddHours(3), "Dentista"));
        var previewAfter = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, friday, 1, comune: null);
        var publicAfter = (await showcase.GetSlotsAsync(supplier, slug, friday, 1))!;

        Assert.DoesNotContain(previewAfter.Days[0].Slots, slot => slot.StartUtc == tenOClock);
        Assert.Contains(publicAfter.Days[0].Slots, slot => slot.StartUtc == tenOClock);
    }

    [Fact]
    public async Task Preview_OfAServiceOfAnotherSupplier_OrADeletedOne_IsTheSameNotFound()
    {
        using var s = await WorldAsync();
        var other = await s.AddOtherSupplierAsync();
        var theirs = await s.AddListingAsync("Di un altro", supplierOrgId: other);
        var deleted = await s.AddListingAsync("Eliminato", deletedAt: ServiceRequestScenario.Instant.UtcDateTime);

        foreach (var serviceId in new[] { theirs, deleted, Guid.NewGuid() })
        {
            var ex = await Assert.ThrowsAsync<NotFoundException>(
                () => Preview(s).GetAsync(s.SupplierOrgId, serviceId, null, null, comune: null));
            Assert.Equal(SupplierServiceCatalogErrors.NotFound, ex.Code);
            Assert.Equal("SupplierServiceNotFound", ex.MessageKey);
        }

        Assert.Equal(theirs, (await Preview(s).GetAsync(other, theirs, null, null, comune: null)).ServiceId);
    }

    [Theory]
    [InlineData("H501", true)]
    [InlineData("  H501  ", true)]
    [InlineData("Milano", false)]
    [InlineData("058091", false)]
    public async Task Preview_WithAComune_SaysIfItIsOneOfTheSuppliersZones(string comune, bool inside)
    {
        using var s = await WorldAsync();

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, null, null, comune);

        Assert.NotNull(preview.Coverage);
        Assert.Equal(inside, preview.Coverage.Inside);
        Assert.Equal(comune.Trim(), preview.Coverage.Comune);
        Assert.NotEmpty(preview.Days);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Preview_WithoutAComune_HasNoCoverage(string? comune)
    {
        using var s = await WorldAsync();

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, null, null, comune);

        Assert.Null(preview.Coverage);
    }

    [Fact]
    public async Task Preview_TheDaysThatAreNotFree_SayNothingButThatTheyAreNot()
    {
        using var s = await WorldAsync();
        await s.Agenda.AddTimeOffAsync(s.SupplierOrgId, new SupplierTimeOffInput(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12), SupplierTimeOffReason.Illness, "Influenza"));

        var preview = await Preview(s).GetAsync(s.SupplierOrgId, s.ListingId, new DateOnly(2026, 10, 11), 3, comune: null);

        Assert.Equal([new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 13)], preview.Days.Select(day => day.Date));
        Assert.False(preview.Days[0].Available);
        Assert.False(preview.Days[1].Available);
        Assert.True(preview.Days[2].Available);
        Assert.DoesNotContain(
            typeof(PublicSlotDay).GetProperties().Select(property => property.Name),
            name => name.Contains("Reason", StringComparison.OrdinalIgnoreCase) || name.Contains("Label", StringComparison.OrdinalIgnoreCase));
    }
}
