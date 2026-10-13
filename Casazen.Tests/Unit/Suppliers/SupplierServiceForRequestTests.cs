using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Tests.Unit.Services;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-04: a service of the catalog as a service request needs it (name, category, duration, rules of the slot, price in advance).</summary>
public class SupplierServiceForRequestTests
{
    // ─── The record ───

    [Theory]
    [InlineData(SupplierServiceListingStatus.Active, true)]
    [InlineData(SupplierServiceListingStatus.Draft, false)]
    [InlineData(SupplierServiceListingStatus.Paused, false)]
    public void IsRequestable_OnlyAPublishedService(SupplierServiceListingStatus status, bool expected)
    {
        Assert.Equal(expected, Service(status: status).IsRequestable);
    }

    [Theory]
    [InlineData(6000, SupplierServicePriceUnit.PerJob, false, 6000)]
    [InlineData(6000, SupplierServicePriceUnit.PerJob, true, null)] // on quote: the supplier gives the price
    [InlineData(6000, SupplierServicePriceUnit.PerHour, false, null)] // the hours are not known
    [InlineData(6000, SupplierServicePriceUnit.PerSet, false, null)]
    [InlineData(6000, SupplierServicePriceUnit.PerSquareMeter, false, null)]
    [InlineData(0, SupplierServicePriceUnit.PerJob, false, null)]
    [InlineData(null, SupplierServicePriceUnit.PerJob, false, null)]
    public void EstimatedAmountCents_OnlyThePriceOfAJobThatIsNotOnQuote(int? price, SupplierServicePriceUnit unit, bool requiresQuote, int? expected)
    {
        Assert.Equal(expected, Service(price: price, unit: unit, requiresQuote: requiresQuote).EstimatedAmountCents);
    }

    [Fact]
    public void ToSlotQuery_CarriesTheDurationTheNoticeAndTheWeekdaysOfTheService()
    {
        var mask = SupplierServiceWeekdays.ToMask([DayOfWeek.Monday, DayOfWeek.Friday]);

        var query = Service(duration: 90, minNoticeHours: 36, weekdaysMask: mask).ToSlotQuery();

        Assert.Equal(new SupplierSlotQuery(90, 36, mask), query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void ToSlotQuery_AServiceWithoutADuration_HasNoSlots(int? duration)
    {
        Assert.Null(Service(duration: duration).ToSlotQuery());
    }

    // ─── The lookup in the catalog ───

    [Fact]
    public async Task FindForRequestAsync_APublishedService_ReturnsWhatTheRequestNeeds()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var found = await s.Catalog.FindForRequestAsync(s.SupplierOrgId, s.ListingId);

        Assert.NotNull(found);
        Assert.Equal(s.ListingId, found.Id);
        Assert.Equal(ServiceRequestScenario.ServiceName, found.Name);
        Assert.Equal(ServiceCategories.Cleaning, found.Category);
        Assert.True(found.IsRequestable);
        Assert.Equal(ServiceRequestScenario.ServiceMinutes, found.DurationMinutes);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, found.EstimatedAmountCents);
        Assert.Equal(SupplierServiceWeekdays.AllMask, found.WeekdaysMask);
    }

    [Fact]
    public async Task FindForRequestAsync_ADraftOrPausedService_IsFoundButNotRequestable()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var draft = await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft);
        var paused = await s.AddListingAsync("In pausa", status: SupplierServiceListingStatus.Paused);

        Assert.False((await s.Catalog.FindForRequestAsync(s.SupplierOrgId, draft))!.IsRequestable);
        Assert.False((await s.Catalog.FindForRequestAsync(s.SupplierOrgId, paused))!.IsRequestable);
    }

    [Fact]
    public async Task FindForRequestAsync_ADeletedServiceOrOneOfAnotherSupplier_IsNotFound()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var deleted = await s.AddListingAsync("Tolto", deletedAt: ServiceRequestScenario.Instant.UtcDateTime);
        var other = await s.AddOtherSupplierAsync();
        var theirs = await s.AddListingAsync("Del collega", supplierOrgId: other);

        Assert.Null(await s.Catalog.FindForRequestAsync(s.SupplierOrgId, deleted));
        Assert.Null(await s.Catalog.FindForRequestAsync(s.SupplierOrgId, theirs));
        Assert.Null(await s.Catalog.FindForRequestAsync(s.SupplierOrgId, Guid.NewGuid()));
        Assert.NotNull(await s.Catalog.FindForRequestAsync(other, theirs));
    }

    [Fact]
    public async Task CountActiveAsync_CountsThePublishedServicesOfTheSupplierOnly()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft);
        await s.AddListingAsync("In pausa", status: SupplierServiceListingStatus.Paused);
        await s.AddListingAsync("Tolto", deletedAt: ServiceRequestScenario.Instant.UtcDateTime);
        var other = await s.AddOtherSupplierAsync();
        await s.AddListingAsync("Del collega", supplierOrgId: other);
        await s.AddListingAsync("Secondo");

        Assert.Equal(2, await s.Catalog.CountActiveAsync(s.SupplierOrgId));
        Assert.Equal(1, await s.Catalog.CountActiveAsync(other));
        Assert.Equal(0, await s.Catalog.CountActiveAsync(Guid.NewGuid()));
    }

    private static SupplierServiceForRequest Service(
        SupplierServiceListingStatus status = SupplierServiceListingStatus.Active,
        int? price = 6000,
        SupplierServicePriceUnit unit = SupplierServicePriceUnit.PerJob,
        bool requiresQuote = false,
        int? duration = 120,
        int? minNoticeHours = null,
        int weekdaysMask = SupplierServiceWeekdays.AllMask) =>
        new(Guid.NewGuid(), "Pulizia", ServiceCategories.Cleaning, status, duration, minNoticeHours, weekdaysMask, price, unit, requiresQuote, "pulizia");
}
