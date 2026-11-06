using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The photos of a listing on the channel manager (E02/F03/US01/T02, T07, NF-32): a photo the channel manager took is never sent again, and a photo
/// that has to leave is never marked «removed» unless the channel manager no longer has it. What CasaZen cannot do for a photo (the queue says
/// <c>DONE</c> and gives no name, so there is nothing to delete it with) is said to the host and left as an error, never reported as done. Over an
/// in-memory database and a strict client: a call nobody set up fails the test.
/// </summary>
public class ListingPhotoSyncTests
{
    private const string PublicBase = "https://storage.test/public/";
    private const string Accommodation = "585492";

    private static readonly Guid OrgId = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a41");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"listing-photos-{Guid.NewGuid():N}";
    private readonly Mock<IOctorateApiClient> _client = new(MockBehavior.Strict);
    private readonly Mock<IInternalActivityService> _activities = new();
    private readonly Mock<IFileStorage> _storage = new();
    private readonly Casazen.Tests.Unit.FixedTimeProvider _clock = new(Now);

    public ListingPhotoSyncTests()
    {
        _storage
            .Setup(s => s.TryGetPublicKey(It.IsAny<string>()))
            .Returns<string>(url => url.StartsWith(PublicBase, StringComparison.Ordinal) ? url[PublicBase.Length..] : null);
        _activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task SendAsync_ADoneEntryWithoutAName_StaysDone_IsNeverSentAgain_AndTheHostIsToldAboutIt()
    {
        // The photo is on the channel manager but CasaZen cannot name it. Marking it as an error would send it again at the next publication (a second
        // copy on the channels); so it stays done, and the host is told once for the property that removing one of these photos here will not remove it there.
        var property = await SeedPropertyAsync(photoCount: 2);
        _client
            .Setup(c => c.UploadPhotoAsync(Accommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePhotoQueueEntry("q-1", OctoratePhotoQueueState.Done, null, null));

        await using (var db = NewDb())
        {
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);
        }

        _client.Verify(c => c.UploadPhotoAsync(Accommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var check = NewDb();
        var tasks = await check.PhotoUploadTasks.AsNoTracking().ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task =>
        {
            Assert.Equal(PhotoUploadStatus.Done, task.Status);
            Assert.Null(task.OctoratePhotoId);
        });
        _activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.PublicationError
                                              && r.Reason == ListingTodos.PhotoNotNamedReason
                                              && r.EntityType == ListingTodos.PhotosEntityType
                                              && r.EntityId == property.Id
                                              && r.PropertyId == property.Id),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task SendAsync_APhotoToRemoveThatHasNoNameToBeDeletedWith_IsNeverMarkedRemoved_AndTheHostGetsAnItem()
    {
        // NF-32: «removed» means removed. The queue says DONE and gives no name, so there is nothing to delete the photo with: the task says it could
        // not be removed (and is not tried again), the host gets one item for that photo, and no DELETE is made at a guess.
        var property = await SeedPropertyAsync(photoCount: 0);
        var task = await SeedTaskAsync(property, "a.jpg", PhotoUploadStatus.RemovePending, queueId: "q-9");
        _client
            .Setup(c => c.GetPhotoQueueAsync(Accommodation, "q-9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePhotoQueueEntry("q-9", OctoratePhotoQueueState.Done, null, null));

        await using (var db = NewDb())
        {
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);
        }

        _client.Verify(c => c.DeletePhotoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.GetPhotoQueueAsync(Accommodation, "q-9", It.IsAny<CancellationToken>()), Times.Once);
        await using var check = NewDb();
        var stored = await check.PhotoUploadTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(PhotoUploadStatus.Error, stored.Status);
        Assert.Equal(ListingPhotoSync.NotRemovedMessage, stored.ErrorMessage);
        _activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.Reason == ListingTodos.PhotoNotRemovedReason
                                              && r.EntityType == nameof(PhotoUploadTask)
                                              && r.EntityId == task.Id
                                              && r.PropertyId == property.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_APhotoToRemoveThatTheQueueNoLongerKnows_IsNeverMarkedRemoved_Either()
    {
        var property = await SeedPropertyAsync(photoCount: 0);
        var task = await SeedTaskAsync(property, "a.jpg", PhotoUploadStatus.RemovePending, queueId: "q-9");
        _client.Setup(c => c.GetPhotoQueueAsync(Accommodation, "q-9", It.IsAny<CancellationToken>())).ReturnsAsync((OctoratePhotoQueueEntry?)null);

        await using (var db = NewDb())
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);

        await using var check = NewDb();
        Assert.Equal(PhotoUploadStatus.Error, (await check.PhotoUploadTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status);
        _client.Verify(c => c.DeletePhotoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_APhotoToRemoveThatHasItsName_IsDeletedOnTheChannelManager_ThenRemoved()
    {
        var property = await SeedPropertyAsync(photoCount: 0);
        var task = await SeedTaskAsync(property, "a.jpg", PhotoUploadStatus.RemovePending, queueId: "q-1", photoName: "abc.jpg");
        _client.Setup(c => c.DeletePhotoAsync(Accommodation, "abc.jpg", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await using (var db = NewDb())
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);

        _client.Verify(c => c.DeletePhotoAsync(Accommodation, "abc.jpg", It.IsAny<CancellationToken>()), Times.Once);
        await using var check = NewDb();
        Assert.Equal(PhotoUploadStatus.Removed, (await check.PhotoUploadTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status);
        _activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("q-3")]
    public async Task SendAsync_APhotoTheChannelManagerNeverTook_IsRemovedWithNoCallAndNoItem(string? queueId)
    {
        // Never sent, or its queue entry ended in error: there is nothing on the channel manager to delete.
        var property = await SeedPropertyAsync(photoCount: 0);
        var task = await SeedTaskAsync(property, "a.jpg", PhotoUploadStatus.RemovePending, queueId: queueId);
        if (queueId is not null)
        {
            _client
                .Setup(c => c.GetPhotoQueueAsync(Accommodation, queueId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OctoratePhotoQueueEntry(queueId, OctoratePhotoQueueState.Error, null, "unreachable"));
        }

        await using (var db = NewDb())
            await Sync(db).SendAsync(property, Accommodation, CancellationToken.None);

        _client.Verify(c => c.DeletePhotoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var check = NewDb();
        Assert.Equal(PhotoUploadStatus.Removed, (await check.PhotoUploadTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status);
        _activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ListingPhotoSync Sync(AppDbContext db) =>
        new(
            db,
            _client.Object,
            new PublicPhotoUrlService(_storage.Object),
            new ListingTodos(db, _activities.Object),
            _clock,
            NullLogger<ListingPhotoSync>.Instance);

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

    /// <summary>A property with <paramref name="photoCount"/> photos in the folder of its own photos of the public bucket.</summary>
    private async Task<Property> SeedPropertyAsync(int photoCount)
    {
        await using var db = NewDb();
        var property = new Property { OrgId = OrgId, OwnerId = "auth0|host", Name = "Casa del Lago", Address = "Via del Lago 1", City = "Como" };
        property.PhotoUrls = [.. Enumerable.Range(1, photoCount).Select(i => $"{PublicBase}properties/{property.Id}/photos/p{i}.jpg")];
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task<PhotoUploadTask> SeedTaskAsync(Property property, string name, PhotoUploadStatus status, string? queueId, string? photoName = null)
    {
        await using var db = NewDb();
        var task = new PhotoUploadTask
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            PhotoUrl = $"{PublicBase}properties/{property.Id}/photos/{name}",
            Status = status,
            OctorateQueueId = queueId,
            OctoratePhotoId = photoName,
            CreatedAt = Now.UtcDateTime.AddDays(-1),
        };
        db.PhotoUploadTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }
}
