using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof of the rooms and the length-of-stay discounts of a unit (NF-34, NF-35, E02/F03/US01/T05): they are read and written only for a
/// property of the caller's organization; another organization's property is not found, neither to read nor to write (permission rule, TN-2).
/// </summary>
public class PropertyListingDataProofTests
{
    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    private static readonly IReadOnlyList<RoomInput> OneBedroom = [new RoomInput(null, PropertyRoomType.Bedroom, [new RoomBed(BedType.Double, 1)])];

    /// <summary>
    /// Permission rule: organization A saves the rooms and the discounts of its property; organization B asks for the same property and finds
    /// none, to read or to write, and nothing of B is stored. The rooms of A also set its counters, and A reads its own.
    /// </summary>
    [Fact]
    public async Task PropertyRooms_PropertyOfAnotherOrg_NotFound_Test()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            OwnerId = "test-user",
            Name = "Casa di A",
            Address = "Via Test 1",
            City = "Como",
            Bedrooms = 3,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100,
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyListingDataTenancy_{Guid.NewGuid()}")
            .Options;

        await using (var seed = new AppDbContext(options, new FixedTenantContext(orgA)))
        {
            seed.Properties.Add(property);
            await seed.SaveChangesAsync();
        }

        await using (var asOrgA = new AppDbContext(options, new FixedTenantContext(orgA)))
        {
            var savedRooms = await new PropertyRoomService(asOrgA, NullLogger<PropertyRoomService>.Instance).ReplaceAsync(property.Id, OneBedroom);
            var savedTiers = await new StayDiscountService(asOrgA, NullLogger<StayDiscountService>.Instance)
                .ReplaceAsync(property.Id, [new StayDiscountTier(7, 10m)]);

            Assert.Single(savedRooms!);
            Assert.Single(savedTiers!);
        }

        await using (var asOrgB = new AppDbContext(options, new FixedTenantContext(orgB)))
        {
            var rooms = new PropertyRoomService(asOrgB, NullLogger<PropertyRoomService>.Instance);
            var discounts = new StayDiscountService(asOrgB, NullLogger<StayDiscountService>.Instance);

            Assert.Null(await rooms.GetAsync(property.Id));
            Assert.Null(await rooms.ReplaceAsync(property.Id, [new RoomInput(null, PropertyRoomType.Studio, [new RoomBed(BedType.King, 2)])]));
            Assert.Null(await discounts.GetAsync(property.Id));
            Assert.Null(await discounts.ReplaceAsync(property.Id, [new StayDiscountTier(3, 5m)]));
        }

        await using var again = new AppDbContext(options, new FixedTenantContext(orgA));
        var storedRooms = await new PropertyRoomService(again, NullLogger<PropertyRoomService>.Instance).GetAsync(property.Id);
        var storedTiers = await new StayDiscountService(again, NullLogger<StayDiscountService>.Instance).GetAsync(property.Id);
        var room = Assert.Single(storedRooms!);
        Assert.Equal(PropertyRoomType.Bedroom, room.Type);
        Assert.Equal([new RoomBed(BedType.Double, 1)], room.Beds);
        var tier = Assert.Single(storedTiers!);
        Assert.Equal(new StayDiscountTier(7, 10m), tier);

        // The rooms of A set the counters of A's property: one bedroom and one bed, the maximum of guests untouched.
        var counters = await again.Properties.SingleAsync(p => p.Id == property.Id);
        Assert.Equal(1, counters.Bedrooms);
        Assert.Equal(1, counters.Beds);
        Assert.Equal(4, counters.MaxGuests);
    }
}
