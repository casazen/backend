using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed partial class GuestSupplierStripeSandboxTests
{
    [PostgresFact]
    public async Task PrivateCaseEvidence_RemovesMetadata_AndStaffCanReviewEachPartyOnce()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestcase");
        await using var db = database.CreateContext();
        var order = await SeedOrderAsync(db, DateTime.UtcNow);
        order.State = GuestSupplierOrderState.Disputed;
        order.Complaint = "Service incomplete";
        await db.SaveChangesAsync();
        var blobs = new Dictionary<string, byte[]>();
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        storage.Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), "image/jpeg", It.IsAny<CancellationToken>()))
            .Returns(async (StorageBucket _, string name, Stream data, string _, CancellationToken token) =>
            {
                using var bytes = new MemoryStream();
                await data.CopyToAsync(bytes, token);
                blobs.Add(name, bytes.ToArray());
            });
        storage.Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StorageBucket _, string name, CancellationToken _) => blobs.TryGetValue(name, out var data) ? new MemoryStream(data) : null);
        var cases = new GuestSupplierCaseFiles(db, storage.Object, TimeProvider.System);
        var id = Guid.NewGuid();
        var photo = ShowcaseTestImages.PhoneJpeg(6);
        for (var i = 0; i < 2; i++)
        {
            using var upload = new MemoryStream(photo);
            var view = await cases.AddPhotoAsync(order, GuestCaseActor.Guest, id, upload, default);
            Assert.Equal(id, Assert.Single(view!.Photos).Id);
        }
        var stored = Assert.Single(blobs);
        Assert.True(stored.Value.Length < photo.Length);
        Assert.DoesNotContain(ShowcaseTestImages.CommentText, Encoding.ASCII.GetString(stored.Value));
        Assert.Empty(cases.Details(order, GuestCaseActor.Supplier)!.Photos);
        Assert.Null(await cases.OpenAsync(order, GuestCaseActor.Supplier, id, default));
        using var content = (await cases.OpenAsync(order, GuestCaseActor.Staff, id, default))!.Stream;
        await Assert.ThrowsAsync<InvalidDataException>(() => cases.AddPhotoAsync(order,
            GuestCaseActor.Supplier, Guid.NewGuid(), new MemoryStream("<svg/>"u8.ToArray()), default));
        Assert.True((await cases.ClaimAsync(order, "staff-one", default))!.Assigned);
        Assert.Null(await cases.ClaimAsync(order, "other", default));
        order.State = GuestSupplierOrderState.Resolved;
        order.Decision = "Refund issued";
        await db.SaveChangesAsync();
        var viewAfter = await cases.RequestReviewAsync(order, GuestCaseActor.Guest, "Please review", default);
        var first = Assert.Single(viewAfter!.Reviews);
        var replay = await cases.RequestReviewAsync(order, GuestCaseActor.Guest, "Please review", default);
        Assert.Equal(first.Id, Assert.Single(replay!.Reviews).Id);
        Assert.Empty(cases.Details(order, GuestCaseActor.Supplier)!.Reviews);
        Assert.True(order.HumanReviewPending);
        Assert.NotNull(await cases.CloseReviewAsync(order, first.Id, "reviewer", "Evidence assessed by staff", default));
        await db.Entry(order).ReloadAsync();
        Assert.False(order.HumanReviewPending);
        Assert.Equal("Evidence assessed by staff", Assert.Single(cases.Details(order, GuestCaseActor.Guest)!.Reviews).Finding);
        Assert.Null(await cases.CloseReviewAsync(order, first.Id, "other", "replace", default));
    }
}
