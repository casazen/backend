using Casazen.Core.Entities;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class GuestSupplierDocumentCalendarTests
{
    [PostgresFact]
    public async Task AcceptedOrderAppearsOnRomeDay_AndItsPrivateDocumentsAreLimitedToSellerAndStay()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestsupplierfiles");
        await using var db = database.CreateContext();
        var order = await GuestSupplierStripeSandboxTests.SeedOrderAsync(db, DateTime.UtcNow);
        order.RequestedFor = new DateTime(2026, 11, 26, 23, 30, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var docs = new GuestSupplierDocuments(db, new InMemoryFileStorage(), TimeProvider.System, new ConfigurationBuilder().Build());
        var agenda = new SupplierAgendaService(db, new SupplierServiceRequestReader(db), new ShowcaseHoldReader(db), NullLogger<SupplierAgendaService>.Instance);
        var day = new DateOnly(2026, 11, 27);
        Assert.Empty((await agenda.GetCalendarAsync(order.SupplierOrgId, day, day)).GuestOrders);
        var bytes = "%PDF-1.4\nSynthetic supplier issued document\n%%EOF"u8.ToArray();
        Assert.Null(await docs.UploadAsync(order.SupplierOrgId, order.Id, "invoice.pdf", new MemoryStream(bytes), default));

        order.State = GuestSupplierOrderState.Accepted;
        order.StripePaymentIntentId = "pi_fixture";
        await db.SaveChangesAsync();
        var tile = Assert.Single((await agenda.GetCalendarAsync(order.SupplierOrgId, day, day)).GuestOrders);
        Assert.Equal(order.Id, tile.Id);
        Assert.Equal(order.RequestedFor, tile.RequestedFor);
        Assert.Empty((await agenda.GetCalendarAsync(order.SupplierOrgId, day.AddDays(-1), day.AddDays(-1))).GuestOrders);
        Assert.Empty((await agenda.GetCalendarAsync(order.HostOrgId, day, day)).GuestOrders);

        Assert.Null(await docs.UploadAsync(order.HostOrgId, order.Id, "invoice.pdf", new MemoryStream(bytes), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => docs.UploadAsync(order.SupplierOrgId, order.Id,
            "invoice.pdf", new MemoryStream("not a PDF"u8.ToArray()), default));
        var file = await docs.UploadAsync(order.SupplierOrgId, order.Id, "../invoice.pdf", new MemoryStream(bytes), default);
        Assert.NotNull(file);
        Assert.Equal("invoice.pdf", file.FileName);
        Assert.Equal(file.Id, (await docs.UploadAsync(order.SupplierOrgId, order.Id, "retry.pdf", new MemoryStream(bytes), default))?.Id);
        var stay = docs.ForStay(order.HostOrgId, order.BookingId, order.PropertyId);
        var view = Assert.Single(await docs.List(stay).ToListAsync());
        Assert.Equal(file.Id, Assert.Single(view.Documents).Id);
        Assert.Null(await docs.OpenAsync(docs.ForStay(order.HostOrgId, Guid.NewGuid(), order.PropertyId), file.Id, default));
        Assert.Null(await docs.OpenAsync(docs.ForStay(order.HostOrgId, order.BookingId, Guid.NewGuid()), file.Id, default));
        Assert.Null(await docs.OpenAsync(docs.ForStay(Guid.NewGuid(), order.BookingId, order.PropertyId), file.Id, default));
        var download = await docs.OpenAsync(stay, file.Id, default);
        Assert.NotNull(download);
        await using var content = download.Value.File;
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.Null(await docs.ReceiptAsync(stay, order.Id, default));
    }
}
