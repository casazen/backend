using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestPortalInvoiceDataTests
{
    [Fact]
    public async Task GuestCanReplaceRecipientUntilTheStayInvoiceIsIssued()
    {
        using var world = new GuestPortalTestWorld();
        var invoices = new InvoiceDataService(world.Db);
        var portal = new GuestPortalInvoiceData(world.Db, invoices);
        var org = world.Org.Id;
        var booking = world.Booking.Id;
        var recipient = new SaveInvoiceDataCommand { Name = "Mario Rossi", FiscalCode = "RSSMRA80A01H501U" };
        Assert.True(await portal.SaveAsync(org, booking, true, recipient, default));
        Assert.True(await portal.SaveAsync(org, booking, true, recipient with { Name = "Anna Rossi" }, default));
        Assert.Equal("Anna Rossi", (await invoices.GetAsync(booking))?.Name);
        Assert.Equal(new GuestPortalInvoiceData.State(false, true, true, false), await portal.GetAsync(org, booking, default));
        world.Db.FiscalDocuments.Add(new FiscalDocument
        {
            Id = Guid.NewGuid(), OrgId = org, BookingId = booking, Purpose = FiscalDocumentPurpose.GuestStay,
            Kind = FiscalDocumentKind.Invoice, Source = FiscalDocumentSource.CasaZen, Status = FiscalDocumentStatus.Issued,
            Number = "1", IssueDate = new DateOnly(2026, 10, 10),
        });
        await world.Db.SaveChangesAsync();
        Assert.False(await portal.SaveAsync(org, booking, true, recipient, default));
        Assert.Equal("Anna Rossi", (await invoices.GetAsync(booking))?.Name);
        Assert.True((await portal.GetAsync(org, booking, default)).Issued);
    }

    [Fact]
    public async Task HostManagedRecipientIsIdentifiedWithoutReturningStoredData()
    {
        using var world = new GuestPortalTestWorld();
        var invoices = new InvoiceDataService(world.Db);
        var portal = new GuestPortalInvoiceData(world.Db, invoices);
        var recipient = new SaveInvoiceDataCommand { Name = "Mario Rossi", FiscalCode = "RSSMRA80A01H501U" };
        await invoices.SaveAsync(world.Booking.Id, recipient, InvoiceDataSource.Host);
        var state = await portal.GetAsync(world.Org.Id, world.Booking.Id, default);
        Assert.True(state.HostManaged);
        Assert.True(await portal.SaveAsync(world.Org.Id, world.Booking.Id, true, recipient with { Name = "Jane", City = "Roma" }, default));
        var saved = await invoices.GetAsync(world.Booking.Id);
        Assert.Equal("Mario Rossi", saved?.Name);
        Assert.Equal("Roma", saved?.City);
    }
}
