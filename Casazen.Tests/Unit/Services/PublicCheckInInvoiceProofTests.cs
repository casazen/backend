using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIS-01, NF-81, E06/F02/US01/T03: the question «Ti serve la fattura?» of the public check-in. The link is anonymous, so what the host or the guest
/// stored about the recipient never leaves through it, and a guest who types a data that is not valid corrects it and sends again: the 422 saves
/// neither the invoice data nor the check-in.
/// </summary>
public class PublicCheckInInvoiceProofTests
{
    private const string FiscalCode = "RSSMRA80A01H501U";
    private const string VatNumber = "IT12345678903";

    private record Seed(AppDbContext Db, Guid BookingId, Guid OrgId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The booking has the invoice data of a company, entered by the host. The public page of the check-in says only that
    /// an invoice was asked for and that the booking has data: no fiscal code, no VAT number, no address, no PEC, no name, whatever the fields of the
    /// answer; once the check-in is completed it says neither.
    /// </summary>
    [Fact]
    public async Task PublicCheckIn_StoredInvoiceData_NeverReturned_Test()
    {
        await using var seed = await SeedAsync();
        var invoices = new InvoiceDataService(seed.Db);
        await invoices.SaveAsync(
            seed.BookingId,
            new SaveInvoiceDataCommand
            {
                RecipientKind = InvoiceRecipientKind.Company,
                Name = "Ristorante Da Gino S.r.l.",
                FiscalCode = "12345678903",
                VatNumber = VatNumber,
                AddressStreet = "Via del Sale Rosa",
                AddressCivic = "42",
                PostalCode = "22100",
                City = "Cernobbio",
                Province = "CO",
                SdiRecipientCode = "ABC1234",
                Pec = "fatture.gino@pec.example.com",
            },
            InvoiceDataSource.Host,
            "auth0|host");
        await invoices.MarkRequestedAsync(seed.BookingId, needInvoice: true);

        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance, invoiceDataService: invoices);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);
        var controller = Controller(svc, seed.Db);

        var open = Assert.IsType<OkObjectResult>((await controller.GetContext(token)).Result);
        var json = JsonSerializer.Serialize(open.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"invoiceRequested\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"hasInvoiceData\":true", json, StringComparison.Ordinal);
        foreach (var stored in new[]
                 {
                     "Ristorante", "Gino", "12345678903", VatNumber, "Sale Rosa", "Cernobbio", "22100", "ABC1234", "fatture.gino", "pec.example.com",
                 })
        {
            Assert.DoesNotContain(stored, json, StringComparison.OrdinalIgnoreCase);
        }

        // The page of a booking with no invoice data says so with the same two booleans.
        var noData = await SeedAsync();
        await using (noData)
        {
            var blank = new GuestCheckInService(noData.Db, NullLogger<GuestCheckInService>.Instance);
            var blankToken = await blank.CreateSessionAsync(noData.BookingId, noData.OrgId);
            var blankJson = JsonSerializer.Serialize(
                Assert.IsType<OkObjectResult>((await Controller(blank, noData.Db).GetContext(blankToken)).Result).Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Contains("\"invoiceRequested\":false", blankJson, StringComparison.Ordinal);
            Assert.Contains("\"hasInvoiceData\":false", blankJson, StringComparison.Ordinal);
        }

        // Once the check-in is completed the page carries no booking data at all, the two booleans included.
        var done = await svc.SubmitAsync(token, ValidGuests());
        Assert.True(done.Success);
        var completed = Assert.IsType<OkObjectResult>((await controller.GetContext(token)).Result);
        var completedJson = JsonSerializer.Serialize(completed.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"completed\":true", completedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice", completedJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PROOF TEST: data rule. A «yes» with an invoice block whose fiscal code is not valid answers 422 with the field and the code: the check-in is
    /// not completed, no guest line, no invoice data, no event. The same guest sends again with the code corrected: the check-in completes and the
    /// data is saved with the source CheckIn, together with the «yes» and one event.
    /// </summary>
    [Fact]
    public async Task PublicCheckIn_InvoiceBlockInvalid_SavesNothing_Test()
    {
        await using var seed = await SeedAsync();
        var invoices = new InvoiceDataService(seed.Db, new DomainEventRecorder(seed.Db, NullTenantContext.Instance, TimeProvider.System));
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance, invoiceDataService: invoices);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var request = ValidGuests();
        var invoice = new GuestCheckInInvoice
        {
            NeedInvoice = true,
            Data = new SaveInvoiceDataCommand { Name = "Luigi Verdi", FiscalCode = "RSSMRA80A01H501X" },
        };
        request.Invoice = invoice;

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(() => svc.SubmitAsync(token, request));
        Assert.Equal(InvoiceDataErrorCodes.FiscalCodeInvalid, refusal.Code);
        Assert.Equal(InvoiceDataFields.FiscalCode, refusal.Extensions["field"]);
        seed.Db.ChangeTracker.Clear();
        Assert.NotEqual(GuestCheckInSessionStatus.Completo, (await seed.Db.GuestCheckInSessions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await seed.Db.StayGuests.AsNoTracking().ToListAsync());
        Assert.Empty(await seed.Db.BookingInvoiceData.AsNoTracking().ToListAsync());
        Assert.Empty(await seed.Db.OutboxEvents.AsNoTracking().ToListAsync());

        // Corrected, the same guest sends again: everything is saved by the one save of the check-in.
        invoice.Data = invoice.Data with { FiscalCode = FiscalCode };
        var done = await svc.SubmitAsync(token, request);

        Assert.True(done.Success);
        seed.Db.ChangeTracker.Clear();
        Assert.Equal(GuestCheckInSessionStatus.Completo, (await seed.Db.GuestCheckInSessions.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await seed.Db.StayGuests.AsNoTracking().ToListAsync());
        var saved = await seed.Db.BookingInvoiceData.AsNoTracking().SingleAsync();
        Assert.Equal((InvoiceDataSource.CheckIn, true, FiscalCode, "Luigi Verdi"), (saved.Source, saved.InvoiceRequested, saved.FiscalCode, saved.Name));
        Assert.Equal(seed.OrgId, saved.OrgId);
        var raised = await seed.Db.OutboxEvents.AsNoTracking().SingleAsync();
        Assert.Equal((DomainEventType.InvoiceDataSaved, seed.BookingId), (raised.EventType, raised.EntityId));
    }

    /// <summary>
    /// A «no» to «Ti serve la fattura?» is nothing, whatever else the block holds: the kind of recipient and the rest are not even read, so the
    /// check-in completes, no invoice data is made and no event is recorded. The same block with a «yes» is refused on the kind, with the field.
    /// </summary>
    [Fact]
    public async Task PublicCheckIn_ANoWithABadKind_NeverBlocksTheCheckIn_AndAYesWithABadKindIsRefused()
    {
        await using var seed = await SeedAsync();
        var invoices = new InvoiceDataService(seed.Db, new DomainEventRecorder(seed.Db, NullTenantContext.Instance, TimeProvider.System));
        var svc = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance, invoiceDataService: invoices);
        var token = await svc.CreateSessionAsync(seed.BookingId, seed.OrgId);

        var no = new PublicCheckInInvoiceDto { NeedInvoice = false, RecipientKind = "not a kind", FiscalCode = "not a code", Pec = "not an address" };
        var input = no.ToInput();
        Assert.False(input.NeedInvoice);
        Assert.False(input.Data.HasContent);

        var request = ValidGuests();
        request.Invoice = input;
        var done = await svc.SubmitAsync(token, request);

        Assert.True(done.Success);
        seed.Db.ChangeTracker.Clear();
        Assert.Equal(GuestCheckInSessionStatus.Completo, (await seed.Db.GuestCheckInSessions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await seed.Db.BookingInvoiceData.AsNoTracking().ToListAsync());
        Assert.Empty(await seed.Db.OutboxEvents.AsNoTracking().ToListAsync());

        var yes = new PublicCheckInInvoiceDto { NeedInvoice = true, RecipientKind = "not a kind" };
        var refusal = Assert.Throws<DomainRuleException>(() => yes.ToInput());
        Assert.Equal(InvoiceDataErrorCodes.FieldInvalid, refusal.Code);
        Assert.Equal(InvoiceDataFields.RecipientKind, refusal.Extensions["field"]);
    }

    private static async Task<Seed> SeedAsync()
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"checkin-invoice-{Guid.NewGuid()}").Options);
        var orgId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        db.Guests.Add(new Guest { Id = guestId, FirstName = "Mario", LastName = "Rossi", Email = "mario@example.com" });
        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "owner-test",
            Name = "Test Property",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            IsActive = true,
        });
        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            OrgId = orgId,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(3),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(6),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        });
        await db.SaveChangesAsync();
        return new Seed(db, bookingId, orgId);
    }

    private static GuestCheckInSubmitRequest ValidGuests() => new()
    {
        Guests =
        [
            new StayGuestInput
            {
                Type = "SingleGuest",
                FirstName = "Luigi",
                LastName = "Verdi",
                DateOfBirth = new DateTime(1990, 5, 15, 0, 0, 0, DateTimeKind.Utc),
                Gender = Gender.Male,
                BornInItaly = true,
                BirthComuneName = "Roma",
                BirthProvince = "rm",
                CitizenshipName = "Italia",
                DocumentType = "Passport",
                DocumentNumber = "YA1234567",
                DocumentIssuePlaceName = "Roma",
            },
        ],
    };

    private static PublicGuestCheckInController Controller(IGuestCheckInService checkIn, AppDbContext db) =>
        new(
            checkIn,
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            Mock.Of<IBiometricVerificationService>(),
            db,
            TimeProvider.System,
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<PublicGuestCheckInController>.Instance)
        {
            // The page reads the Accept-Language of the request to open in the guest's language (NF-40): a controller built by hand needs a request.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
