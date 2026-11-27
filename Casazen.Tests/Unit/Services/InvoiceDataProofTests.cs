using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIS-01, NF-81, E06/F02/US01/T02: who an invoice of a booking is made out to is data of one org and the host's word is final. The two rules
/// that cost if they fail: a booking of another organization is neither readable nor writable (404), and a lower source (the guest at the check-in,
/// the channel manager's import) never overwrites what a higher one wrote.
/// </summary>
public class InvoiceDataProofTests
{
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281702");
    private static readonly Guid OrgB = Guid.Parse("7e6d5c4b-3a29-4f8e-9d7c-6c5b4a392803");
    private static readonly Guid PropertyOfOrgA = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c11");
    private static readonly Guid PropertyOfColleague = Guid.Parse("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e13");
    private const string Owner = "auth0|owner-a";
    private const string Colleague = "auth0|colleague-a";

    private const string FiscalCode = "RSSMRA80A01H501U";
    private const string OtherFiscalCode = "BNCGLI75C42F205Z";
    private const string VatNumber = "IT12345678903";

    /// <summary>
    /// PROOF TEST: permission rule. A booking of another organization is invisible to the caller (the tenant filter), so both endpoints answer 404
    /// and the service is never asked; a booking the caller sees but does not reach (a property it was not given) is 403, still without the service.
    /// The service itself, under the tenant filter of the caller, finds no data for such a booking and refuses to write any.
    /// </summary>
    [Fact]
    public async Task InvoiceData_OtherOrgBooking_NotReadableOrWritable_Test()
    {
        var bookingOfOrgB = Guid.NewGuid();
        var bookingOfColleague = Guid.NewGuid();
        var bookingOfOwner = Guid.NewGuid();

        var service = new Mock<IInvoiceDataService>(MockBehavior.Strict);
        var lookup = new Mock<IHostResourceLookup>();
        // The tenant filter hides another org's booking: the lookup answers nothing, exactly as for a booking that does not exist.
        lookup.Setup(l => l.ForBookingAsync(bookingOfOrgB, It.IsAny<CancellationToken>())).ReturnsAsync((HostResource?)null);
        lookup.Setup(l => l.ForBookingAsync(bookingOfColleague, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgA, Colleague, PropertyOfColleague));
        lookup.Setup(l => l.ForBookingAsync(bookingOfOwner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgA, Owner, PropertyOfOrgA));

        // An account in no org team with no org-wide role: it reaches only the properties it created.
        var controller = Controller(service, lookup, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        var request = new SaveInvoiceDataRequest { Name = "Anna Verdi", FiscalCode = FiscalCode };

        AssertProblem((await controller.Get(bookingOfOrgB, CancellationToken.None)).Result, StatusCodes.Status404NotFound);
        AssertProblem((await controller.Put(bookingOfOrgB, request, CancellationToken.None)).Result, StatusCodes.Status404NotFound);
        Assert.IsType<ForbidResult>((await controller.Get(bookingOfColleague, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await controller.Put(bookingOfColleague, request, CancellationToken.None)).Result);
        service.VerifyNoOtherCalls();

        // The same caller as the owner of the booking it reaches: the service is asked, and the host's save is the source Host, by its user.
        var saved = new InvoiceDataView(
            bookingOfOwner, false, null, InvoiceRecipientKind.Guest, "Anna Verdi", FiscalCode, null, null, null, null, null, null, "IT", null, null,
            InvoiceDataSource.Host, DateTime.UtcNow);
        service.Setup(s => s.GetAsync(bookingOfOwner, It.IsAny<CancellationToken>())).ReturnsAsync((InvoiceDataView?)null);
        service.Setup(s => s.SaveAsync(
                bookingOfOwner, It.IsAny<SaveInvoiceDataCommand>(), InvoiceDataSource.Host, Owner, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(saved);
        var ownerController = Controller(service, lookup, HostAuthorizationTestHarness.User(Owner, "PropertyOwner"));

        var empty = Assert.IsType<OkObjectResult>((await ownerController.Get(bookingOfOwner, CancellationToken.None)).Result);
        Assert.Equal(InvoiceDataDto.Empty(bookingOfOwner).BookingId, Assert.IsType<InvoiceDataDto>(empty.Value).BookingId);
        Assert.Null(Assert.IsType<InvoiceDataDto>(empty.Value).Source);
        var written = Assert.IsType<OkObjectResult>((await ownerController.Put(bookingOfOwner, request, CancellationToken.None)).Result);
        Assert.Equal("Host", Assert.IsType<InvoiceDataDto>(written.Value).Source);
        service.Verify(
            s => s.SaveAsync(
                bookingOfOwner, It.IsAny<SaveInvoiceDataCommand>(), InvoiceDataSource.Host, Owner, true, It.IsAny<CancellationToken>()),
            Times.Once);

        // The service, under the tenant filter of a caller of org A: the booking of org B has no data to read and none can be written.
        var databaseName = $"invoice-data-tenant-{Guid.NewGuid()}";
        var foreignBooking = await SeedBookingAsync(databaseName, OrgB);
        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(OrgA));
        var sut = new InvoiceDataService(asOrgA);

        Assert.Null(await sut.GetAsync(foreignBooking));
        var refusal = await Assert.ThrowsAsync<NotFoundException>(
            () => sut.SaveAsync(foreignBooking, new SaveInvoiceDataCommand { Name = "Anna Verdi", FiscalCode = FiscalCode }, InvoiceDataSource.Host));
        Assert.Equal("booking_not_found", refusal.Code);
        Assert.Empty(await asOrgA.BookingInvoiceData.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule. The host's save outranks the check-in, which outranks the import: a lower source fills only the fields that are
    /// empty, never overwrites and never clears a stored value, and the row keeps the highest source. Order does not matter: what the import
    /// wrote first is overwritten by the host afterwards, and a save that fills nothing writes nothing and records no event.
    /// </summary>
    [Fact]
    public async Task InvoiceData_LowerSourceNeverOverwritesHost_Test()
    {
        var databaseName = $"invoice-data-precedence-{Guid.NewGuid()}";
        var bookingId = await SeedBookingAsync(databaseName, OrgA);
        var tenant = new FixedTenantContext(OrgA);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

        async Task<InvoiceDataView> SaveAs(InvoiceDataSource source, SaveInvoiceDataCommand command)
        {
            await using var db = NewDb(databaseName, tenant);
            var sut = new InvoiceDataService(db, new DomainEventRecorder(db, tenant, clock), clock);
            return await sut.SaveAsync(bookingId, command, source, source == InvoiceDataSource.Host ? Owner : null);
        }

        // The host types the recipient.
        var host = await SaveAs(
            InvoiceDataSource.Host,
            new SaveInvoiceDataCommand
            {
                RecipientKind = InvoiceRecipientKind.Guest,
                Name = "Mario Rossi",
                FiscalCode = FiscalCode,
                AddressStreet = "Via Roma",
                City = "Roma",
            });
        Assert.Equal(InvoiceDataSource.Host, host.Source);

        // The guest at the check-in: what the host wrote stays (and an empty text does not clear it); only the empty fields are filled.
        var checkIn = await SaveAs(
            InvoiceDataSource.CheckIn,
            new SaveInvoiceDataCommand
            {
                RecipientKind = InvoiceRecipientKind.Company,
                Name = "Mario R.",
                FiscalCode = OtherFiscalCode,
                VatNumber = VatNumber,
                AddressStreet = "Via Milano",
                City = string.Empty,
                Pec = "mario.rossi@pec.example.com",
            });
        Assert.Equal("Mario Rossi", checkIn.Name);
        Assert.Equal(FiscalCode, checkIn.FiscalCode);
        Assert.Equal("Via Roma", checkIn.AddressStreet);
        Assert.Equal("Roma", checkIn.City);
        Assert.Equal(InvoiceRecipientKind.Guest, checkIn.RecipientKind);
        Assert.Equal(VatNumber, checkIn.VatNumber);
        Assert.Equal("mario.rossi@pec.example.com", checkIn.Pec);
        Assert.Equal(InvoiceDataSource.Host, checkIn.Source);

        // The import: it fills the last empty field and touches nothing else.
        var imported = await SaveAs(
            InvoiceDataSource.Octorate,
            new SaveInvoiceDataCommand
            {
                Name = "Octo Name",
                Pec = "import@pec.example.com",
                VatNumber = "IT00000000000",
                SdiRecipientCode = "ZZZ9999",
            });
        Assert.Equal("Mario Rossi", imported.Name);
        Assert.Equal("mario.rossi@pec.example.com", imported.Pec);
        Assert.Equal(VatNumber, imported.VatNumber);
        Assert.Equal("ZZZ9999", imported.SdiRecipientCode);
        Assert.Equal(InvoiceDataSource.Host, imported.Source);

        // An import that has nothing to fill writes nothing: no new event, the row is as it was.
        await using (var db = NewDb(databaseName, tenant))
        {
            var before = await db.OutboxEvents.AsNoTracking().CountAsync();
            var same = await SaveAs(InvoiceDataSource.Octorate, new SaveInvoiceDataCommand { Name = "Another", Pec = "x@pec.example.com" });
            Assert.Equal(imported.UpdatedAt, same.UpdatedAt);
            Assert.Equal(before, await db.OutboxEvents.AsNoTracking().CountAsync());
        }

        // The host corrects the name afterwards: its word replaces the stored one and nothing outranks it.
        clock.Advance(TimeSpan.FromMinutes(5));
        var corrected = await SaveAs(InvoiceDataSource.Host, new SaveInvoiceDataCommand { Name = "Mario Rossi Junior" });
        Assert.Equal("Mario Rossi Junior", corrected.Name);
        Assert.Equal(InvoiceDataSource.Host, corrected.Source);

        // The other way round: what the import wrote first is overwritten by the host, and then the row is the host's.
        var secondBooking = await SeedBookingAsync(databaseName, OrgA);
        await using (var db = NewDb(databaseName, tenant))
        {
            var sut = new InvoiceDataService(db, new DomainEventRecorder(db, tenant, clock), clock);
            await sut.SaveAsync(secondBooking, new SaveInvoiceDataCommand { Name = "Octo Name", City = "Torino" }, InvoiceDataSource.Octorate);
            var overwritten = await sut.SaveAsync(
                secondBooking,
                new SaveInvoiceDataCommand { Name = "Anna Verdi", FiscalCode = FiscalCode },
                InvoiceDataSource.Host,
                Owner);
            Assert.Equal("Anna Verdi", overwritten.Name);
            Assert.Equal("Torino", overwritten.City);
            Assert.Equal(InvoiceDataSource.Host, overwritten.Source);
        }

        // One event for each save that wrote something, about the booking, with the source and nothing personal.
        await using var read = NewDb(databaseName, tenant);
        var events = await read.OutboxEvents.AsNoTracking().Where(e => e.EntityId == bookingId).ToListAsync();
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal(DomainEventType.InvoiceDataSaved, e.EventType));
        Assert.All(events, e => Assert.DoesNotContain("Rossi", e.PayloadJson, StringComparison.Ordinal));
        Assert.All(events, e => Assert.DoesNotContain(FiscalCode, e.PayloadJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvoiceData_AnInvalidSave_NamesTheFieldOfEachFailure_AndSavesNothing()
    {
        var databaseName = $"invoice-data-invalid-{Guid.NewGuid()}";
        var bookingId = await SeedBookingAsync(databaseName, OrgA);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var sut = new InvoiceDataService(db);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(
            () => sut.SaveAsync(
                bookingId,
                new SaveInvoiceDataCommand
                {
                    Name = "Mario Rossi",
                    FiscalCode = "RSSMRA80A01H501X",
                    VatNumber = "IT12345678900",
                    SdiRecipientCode = "abc",
                    Pec = "not an address",
                },
                InvoiceDataSource.Host));

        Assert.Equal(InvoiceDataErrorCodes.FiscalCodeInvalid, refusal.Code);
        Assert.Equal(InvoiceDataFields.FiscalCode, refusal.Extensions["field"]);
        Assert.Equal(
            [InvoiceDataFields.FiscalCode, InvoiceDataFields.VatNumber, InvoiceDataFields.SdiRecipientCode, InvoiceDataFields.Pec],
            ((IEnumerable<object>)refusal.Extensions["errors"]!).Select(e => (string)e.GetType().GetProperty("field")!.GetValue(e)!).ToArray());
        Assert.Empty(await db.BookingInvoiceData.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    private static async Task<Guid> SeedBookingAsync(string databaseName, Guid orgId)
    {
        await using var system = NewDb(databaseName, tenant: null);
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        };
        system.Bookings.Add(booking);
        await system.SaveChangesAsync();
        return booking.Id;
    }

    private static void AssertProblem(IActionResult? result, int status)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal("booking_not_found", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private static InvoiceDataController Controller(
        Mock<IInvoiceDataService> service,
        Mock<IHostResourceLookup> lookup,
        System.Security.Claims.ClaimsPrincipal user) =>
        new(service.Object, lookup.Object, HostAuthorizationTestHarness.Create(OrgA), NullLogger<InvoiceDataController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user,
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
