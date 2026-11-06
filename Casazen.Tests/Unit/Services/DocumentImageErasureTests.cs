using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T04 privacy proofs (ADR-013-R7, art. 17 GDPR): the photo of an identity document that a verification kept in the private bucket
/// (the object <b>and</b> its <see cref="DocumentImage"/> row) is deleted when the guest is erased and when the Alloggiati receipt arrives, also
/// the rows a verification already deleted from the bucket and marked; the photos of another guest or of another booking stay.
/// </summary>
public class DocumentImageErasureTests
{
    /// <summary>Privacy rule: the erasure of a guest deletes every photo kept for the verification of its bookings, objects and rows, and only its own.</summary>
    [Fact]
    public async Task DocumentImageErasure_ErasingAGuest_DeletesItsPhotosAndNobodyElses_Test()
    {
        using var db = OrgTeamTestData.NewDb();
        var world = World.Seed(db);
        var storage = StorageThatRemembers(out var deleted);
        var eraser = new GuestDataEraser(db, storage.Object, NullLogger<GuestDataEraser>.Instance);

        var outcome = await eraser.AnonymizeAsync(world.GuestA, DateTime.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(outcome.Changed);
        Assert.Equal(2, outcome.FilesDeleted);
        Assert.Equal(world.KeysOf(world.GuestA).Order(StringComparer.Ordinal), deleted.Order(StringComparer.Ordinal));
        var left = await db.DocumentImages.AsNoTracking().ToListAsync();
        Assert.Equal(world.GuestB.Id, Assert.Single(left).GuestId);

        // Once done there is nothing left to delete.
        deleted.Clear();
        Assert.Equal(0, await eraser.DeleteVerificationDocumentImagesAsync(world.GuestA, CancellationToken.None));
        Assert.Empty(deleted);
    }

    /// <summary>Privacy rule: the receipt of the communication of a stay deletes the photos of its verification, objects and rows, and only its own.</summary>
    [Fact]
    public async Task DocumentImageErasure_AlloggiatiReceipt_DeletesThePhotosOfTheBooking_Test()
    {
        using var db = OrgTeamTestData.NewDb();
        var world = World.Seed(db);
        var storage = StorageThatRemembers(out var deleted);
        var job = new AlloggiatiReceiptJob(db, null!, storage.Object, NullLogger<AlloggiatiReceiptJob>.Instance, TimeProvider.System);

        await job.DeleteDocumentImagesAsync(world.BookingA.Id, CancellationToken.None);

        Assert.Equal(world.KeysOf(world.GuestA).Order(StringComparer.Ordinal), deleted.Order(StringComparer.Ordinal));
        var left = await db.DocumentImages.AsNoTracking().ToListAsync();
        Assert.Equal(world.BookingB.Id, Assert.Single(left).BookingId);
    }

    private static Mock<IFileStorage> StorageThatRemembers(out List<string> deleted)
    {
        var keys = new List<string>();
        var storage = new Mock<IFileStorage>();
        storage.Setup(s => s.DeleteAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<StorageBucket, string, CancellationToken>((_, key, _) => keys.Add(key))
            .Returns(Task.CompletedTask);
        deleted = keys;
        return storage;
    }

    /// <summary>Two guests of one org, a stay each; the first one has two photos (one already deleted from the bucket by its verification and marked), the second one.</summary>
    private sealed class World
    {
        public required Guest GuestA { get; init; }

        public required Guest GuestB { get; init; }

        public required Booking BookingA { get; init; }

        public required Booking BookingB { get; init; }

        private List<DocumentImage> Images { get; } = [];

        public static World Seed(AppDbContext db)
        {
            var org = OrgTeamTestData.AddOrg(db);
            var property = new Property
            {
                Id = Guid.NewGuid(),
                OrgId = org.Id,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            };
            db.Properties.Add(property);
            var guestA = NewGuest(org.Id, "Anna");
            var guestB = NewGuest(org.Id, "Bruno");
            var bookingA = NewBooking(org.Id, property.Id, guestA.Id);
            var bookingB = NewBooking(org.Id, property.Id, guestB.Id);
            db.Guests.AddRange(guestA, guestB);
            db.Bookings.AddRange(bookingA, bookingB);

            var world = new World { GuestA = guestA, GuestB = guestB, BookingA = bookingA, BookingB = bookingB };
            world.Images.Add(Image(org.Id, guestA, bookingA, "a1.bin", deletedAt: null));
            world.Images.Add(Image(org.Id, guestA, bookingA, "a2.bin", deletedAt: DateTime.UtcNow.AddHours(-1)));
            world.Images.Add(Image(org.Id, guestB, bookingB, "b1.bin", deletedAt: null));
            db.DocumentImages.AddRange(world.Images);
            db.SaveChanges();
            return world;
        }

        /// <summary>The storage keys of the photos of <paramref name="guest"/>.</summary>
        public IEnumerable<string> KeysOf(Guest guest) => Images.Where(i => i.GuestId == guest.Id).Select(i => i.FrontImageUrl);

        private static Guest NewGuest(Guid orgId, string firstName) => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            FirstName = firstName,
            LastName = "Test",
            Email = $"{firstName.ToLowerInvariant()}@example.com",
        };

        private static Booking NewBooking(Guid orgId, Guid propertyId, Guid guestId) => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 11, 12),
            CheckOutDate = new DateTime(2026, 11, 14),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };

        private static DocumentImage Image(Guid orgId, Guest guest, Booking booking, string file, DateTime? deletedAt) => new()
        {
            OrgId = orgId,
            GuestId = guest.Id,
            BookingId = booking.Id,
            FrontImageUrl = StorageKeys.GuestDocument(orgId, guest.Id, file),
            UploadedAt = DateTime.UtcNow.AddHours(-2),
            ProcessedAt = DateTime.UtcNow.AddHours(-2),
            DeletedAt = deletedAt,
        };
    }
}
