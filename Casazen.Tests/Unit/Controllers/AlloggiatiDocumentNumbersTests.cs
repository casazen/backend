using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// casazen/backend#979, the proof of the privacy rule of <c>GET api/alloggiati/{bookingId}/stay-guests/document-numbers</c> (NF-132): the full number of a document is read by the host of
/// the booking with <c>guest.read</c>, audited with the user and the positions and never with a number. The number of a single guest or a head, as ever, and, in a stay of a property in canton
/// Ticino, the number of every ADULT family or group member the police file notifies on their own row (so the host's dialog fills it instead of asking it again): never of a minor, never of a
/// guest with no number stored, never of a member of a group of six or more; any other stay answers what it always answered. The services are the real ones (the stay guests, the one resolver of
/// the place of a Swiss property over the directory, the real authorization of a booking); EF InMemory.
/// </summary>
public class AlloggiatiDocumentNumbersTests : IDisposable
{
    private const string Owner = "auth0|owner";
    private const string HeadNumber = "TEST-HEAD-0001";
    private const string AdultNumber = "TEST-ADULT-0002";
    private const string MinorNumber = "TEST-MINOR-0003";

    private static readonly DateTime Arrival = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _orgId;
    private readonly OrgEntity _org;

    public AlloggiatiDocumentNumbersTests()
    {
        _org = OrgTeamTestData.AddOrg(_db);
        _orgId = _org.Id;
        OrgTeamTestData.AddUser(_db, Owner, _org.Id, UserRole.PropertyOwner);
        _db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5192", CantonCode = "TI", OfficialName = "Lugano" });
        _db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "261", CantonCode = "ZH", OfficialName = "Zürich" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>A flat of the org: Italian, of canton Ticino or of canton Zurich (the municipality stored, as the other tests of the area do).</summary>
    private Property AddFlat(string country, string? municipality)
    {
        var property = new Property
        {
            OrgId = _orgId,
            OwnerId = Owner,
            Name = $"Casa {Guid.NewGuid():N}"[..14],
            Address = $"Via Test {Guid.NewGuid():N}"[..20],
            City = country == "CH" ? "Lugano" : "Como",
            PostalCode = country == "CH" ? "6900" : "22100",
            Country = country,
            SwissMunicipalityCode = municipality,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 8,
            NightlyRate = 100m,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    /// <summary>One confirmed stay of the flat with the guests given: (type, born, number).</summary>
    private Booking AddStay(Property flat, params (StayGuestType Type, DateTime Born, string Number)[] guests)
    {
        var booker = new Guest { OrgId = _orgId, FirstName = "Hans", LastName = "Muster", Email = "hans.muster@example.com" };
        var booking = new Booking
        {
            OrgId = _orgId,
            PropertyId = flat.Id,
            GuestId = booker.Id,
            CheckInDate = Arrival,
            CheckOutDate = Arrival.AddDays(3),
            Status = BookingStatus.Confirmed,
            NumberOfGuests = guests.Length,
        };
        _db.AddRange(booker, booking);
        for (var position = 0; position < guests.Length; position++)
        {
            _db.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = _orgId,
                Position = position,
                Type = guests[position].Type,
                FirstName = $"Nome{position}",
                LastName = "Muster",
                DateOfBirth = guests[position].Born,
                DocumentType = GuestDocumentType.Passport,
                DocumentNumber = guests[position].Number,
            });
        }

        _db.SaveChanges();
        return booking;
    }

    /// <summary>The head and a second adult with their numbers, a child with the number somebody typed, and a third adult with none: the same data in every flat of these tests.</summary>
    private Booking AddFamilyStay(Property flat) =>
        AddStay(
            flat,
            (StayGuestType.HeadOfFamily, new DateTime(1985, 4, 2, 0, 0, 0, DateTimeKind.Utc), HeadNumber),
            (StayGuestType.FamilyMember, new DateTime(1987, 9, 9, 0, 0, 0, DateTimeKind.Utc), AdultNumber),
            (StayGuestType.FamilyMember, new DateTime(2017, 3, 1, 0, 0, 0, DateTimeKind.Utc), MinorNumber),
            (StayGuestType.FamilyMember, new DateTime(1990, 1, 20, 0, 0, 0, DateTimeKind.Utc), string.Empty));

    private AlloggiatiController Controller(Booking? booking, Guid? callerOrg = null, ILogger<AlloggiatiController>? log = null)
    {
        var bookings = new Mock<IBookingService>();
        if (booking is not null)
            bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);

        var lookup = new Mock<IHostResourceLookup>();
        if (booking is not null)
        {
            lookup
                .Setup(l => l.ForPropertyAsync(booking.PropertyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(HostResource.ForProperty(new Property { Id = booking.PropertyId, OrgId = booking.OrgId, OwnerId = Owner, Name = "Casa" }));
        }

        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new AlloggiatiController(
            Mock.Of<IAlloggiatiWebService>(),
            bookings.Object,
            Mock.Of<IPropertyAuthorizationService>(),
            lookup.Object,
            HostAuthorizationTestHarness.Create(callerOrg ?? _orgId),
            new StayGuestService(_db, Mock.Of<IAlloggiatiCodeTableService>(), _clock),
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IOrgContextResolver>(),
            Mock.Of<IHostScopeResolver>(),
            services.GetRequiredService<IStringLocalizer<SharedResources>>(),
            log ?? NullLogger<AlloggiatiController>.Instance,
            Mock.Of<IComplianceStatusService>(),
            Mock.Of<ICheckInOctorateSyncService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = HostAuthorizationTestHarness.User(Owner, "PropertyOwner"), RequestServices = services },
            },
        };
    }

    private static async Task<List<StayGuestDocumentNumberDto>> NumbersAsync(AlloggiatiController controller, Booking booking, int? position = null)
    {
        var result = await controller.GetDocumentNumbers(booking.Id, position);
        return Assert.IsAssignableFrom<IEnumerable<StayGuestDocumentNumberDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).ToList();
    }

    // ─── The stay of Ticino ───

    [Fact]
    public async Task DocumentNumbers_InAStayOfTicino_AreTheHeadAndTheAdultMember_NeverTheMinorNorAGuestWithNoNumber()
    {
        var booking = AddFamilyStay(AddFlat("CH", "5192"));

        var numbers = await NumbersAsync(Controller(booking), booking);

        // The head, as ever, and the second adult the file notifies on their own row; not the child (a number somebody typed is never read back), not the adult with no number.
        Assert.Equal([(0, HeadNumber), (1, AdultNumber)], numbers.OrderBy(n => n.Position).Select(n => (n.Position, n.DocumentNumber)).ToArray());
        Assert.DoesNotContain(numbers, n => n.DocumentNumber == MinorNumber);
        Assert.DoesNotContain(numbers, n => n.Position is 2 or 3);
    }

    [Fact]
    public async Task DocumentNumbers_InAStayOfTicino_ThePositionStillFiltersToOneGuest()
    {
        var booking = AddFamilyStay(AddFlat("CH", "5192"));
        var controller = Controller(booking);

        Assert.Equal([AdultNumber], (await NumbersAsync(controller, booking, position: 1)).Select(n => n.DocumentNumber));
        Assert.Equal([HeadNumber], (await NumbersAsync(controller, booking, position: 0)).Select(n => n.DocumentNumber));
        Assert.Empty(await NumbersAsync(controller, booking, position: 2));
        Assert.Empty(await NumbersAsync(controller, booking, position: 3));
        Assert.Empty(await NumbersAsync(controller, booking, position: 9));
    }

    [Fact]
    public async Task DocumentNumbers_InAGroupOfSixInTicino_AreTheLeaderOnly_TheMembersAreNotARowOfTheFile()
    {
        var born = new DateTime(1980, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var booking = AddStay(
            AddFlat("CH", "5192"),
            (StayGuestType.HeadOfGroup, born, HeadNumber),
            (StayGuestType.GroupMember, born, "TEST-MEMBER-1"),
            (StayGuestType.GroupMember, born, "TEST-MEMBER-2"),
            (StayGuestType.GroupMember, born, "TEST-MEMBER-3"),
            (StayGuestType.GroupMember, born, "TEST-MEMBER-4"),
            (StayGuestType.GroupMember, born, "TEST-MEMBER-5"));

        var numbers = await NumbersAsync(Controller(booking), booking);

        Assert.Equal([(0, HeadNumber)], numbers.Select(n => (n.Position, n.DocumentNumber)).ToArray());
    }

    [Fact]
    public async Task DocumentNumbers_OfAStayWhoseGuestsAreAllMinors_AreTheSingleGuestsAsEver_NeverAMemberOfFamily()
    {
        // The file notifies every minor of a stay with no adult: their documents are stored. A single guest has its number read as it always had; a family member of minors is never read.
        var born = new DateTime(2010, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var booking = AddStay(
            AddFlat("CH", "5192"),
            (StayGuestType.SingleGuest, born, "TEST-SINGLE-1"),
            (StayGuestType.FamilyMember, born, MinorNumber));

        var numbers = await NumbersAsync(Controller(booking), booking);

        Assert.Equal([(0, "TEST-SINGLE-1")], numbers.Select(n => (n.Position, n.DocumentNumber)).ToArray());
    }

    // ─── Every other stay answers what it always answered ───

    [Fact]
    public async Task DocumentNumbers_InAnItalianStayWithTheSameData_AreTheHeadOnly()
    {
        var booking = AddFamilyStay(AddFlat("IT", null));

        var numbers = await NumbersAsync(Controller(booking), booking);

        Assert.Equal([(0, HeadNumber)], numbers.Select(n => (n.Position, n.DocumentNumber)).ToArray());
    }

    [Fact]
    public async Task DocumentNumbers_InAStayOfAnotherCantonWithTheSameData_AreTheHeadOnly()
    {
        // Zurich has no police file of its own in CasaZen: the check-in keeps no document of a family member there, and nothing is read of one.
        var booking = AddFamilyStay(AddFlat("CH", "261"));

        var numbers = await NumbersAsync(Controller(booking), booking);

        Assert.Equal([(0, HeadNumber)], numbers.Select(n => (n.Position, n.DocumentNumber)).ToArray());
    }

    [Fact]
    public async Task DocumentNumbers_InASwissStayWhosePlaceIsOpen_AreTheHeadOnly_ASwissPlaceThatIsNotKnownIsNotInTicino()
    {
        // No municipality stored, and the directory has no locality of this postal code: the one resolver cannot place the flat, so it is not a flat of Ticino and nothing is added.
        var booking = AddFamilyStay(AddFlat("CH", null));

        var numbers = await NumbersAsync(Controller(booking), booking);

        Assert.Equal([(0, HeadNumber)], numbers.Select(n => (n.Position, n.DocumentNumber)).ToArray());
    }

    // ─── Permission, tenant and audit ───

    [Fact]
    public void TheEndpoint_IsBehindGuestRead_TheOneOfTheFullDocumentNumbers()
    {
        var method = typeof(AlloggiatiController).GetMethod(nameof(AlloggiatiController.GetDocumentNumbers))!;

        Assert.Equal(CasazenPolicies.GuestRead, Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Equal("{bookingId:guid}/stay-guests/document-numbers", Assert.Single(method.GetCustomAttributes<HttpGetAttribute>()).Template);
    }

    [Fact]
    public async Task DocumentNumbers_OfABookingOfAnotherOrg_AreNotFoundOrForbidden_AsToday()
    {
        var booking = AddFamilyStay(AddFlat("CH", "5192"));

        // A booking the tenant filter hides is a 404, and one that is visible to a caller of another org is a 403: no number of a stay of Ticino leaves its org.
        Assert.IsType<NotFoundResult>((await Controller(null).GetDocumentNumbers(booking.Id, null)).Result);
        Assert.IsType<ForbidResult>((await Controller(booking, callerOrg: Guid.NewGuid()).GetDocumentNumbers(booking.Id, null)).Result);
    }

    [Fact]
    public async Task DocumentNumbers_TheAuditLine_HoldsTheUserAndThePositions_NeverANumber()
    {
        var booking = AddFamilyStay(AddFlat("CH", "5192"));
        var log = new CapturingLogger<AlloggiatiController>();

        await NumbersAsync(Controller(booking, log: log), booking);

        var entry = Assert.Single(log.Entries, e => e.Message.Contains("viewed the document numbers", StringComparison.Ordinal));
        Assert.Contains($"User {Owner} viewed the document numbers of booking {booking.Id}, positions 0,1", entry.Message, StringComparison.Ordinal);
        foreach (var number in new[] { HeadNumber, AdultNumber, MinorNumber })
            Assert.DoesNotContain(number, log.AllOutput, StringComparison.Ordinal);
    }
}
