using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Messages;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E23/F01/US01/T02 (NF-122, NF-63, S18, D24): the contact of the guest of a portal booking, read from the channel manager for the check-in link and for nothing else, on the real
/// service over a database. The proofs the brief asks for: the read is for the check-in link only and never reaches the CRM (nothing of the answer is in a log, a table of the CRM
/// or the activity log but the purpose), and a refusal (the static IP addresses) is a state that changes nothing; and the rules that cost trust if they are wrong: nothing is asked
/// while the flag is off, for a stay that is not a portal's, for a guest that has an address or whose channel holds none, and a failure is never read as «no contact».
/// </summary>
public class GuestContactInfoServiceTests : IAsyncLifetime
{
    private const string GuestEmail = "mei.tanaka@guest-mail.example";
    private const string GuestPhone = "+81 90 1234 5678";

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>
    /// PROOF TEST: privacy rule (S18, D24, decision O10). The contact of ONE booking is read once, for the check-in link: the address is written on the guest of the booking and the
    /// phone number too (for the host's view), the read is one line of the activity log with the purpose <c>check_in_link</c>, nobody as the actor and no address; and nothing goes to
    /// the CRM: neither the operation of the CRM nor its store is touched, and no log, no row of the CRM and no line of the log holds the address or the number.
    /// </summary>
    [Fact]
    public async Task ContactInfo_PurposeCheckInOnly_NotInCrm_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world);
        rig.ChannelHolds(GuestEmail, GuestPhone);

        var outcome = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id);

        Assert.Equal(GuestContactInfoStatus.EmailFilled, outcome.Status);
        var guest = await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == stay.GuestId);
        Assert.Equal(GuestEmail, guest.Email);
        Assert.Equal(GuestPhone, guest.PhoneNumber);

        // One booking, one read, for this link: the operation of the reservation, never the one of the guest registry that feeds the CRM.
        rig.Client.Verify(c => c.GetReservationContactInfoAsync("ACC-1", "REF-77", It.IsAny<CancellationToken>()), Times.Once);
        rig.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await _db.GuestContacts.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.GuestCrmProfiles.AsNoTracking().ToListAsync());

        // The line of the activity log: the booking, the purpose, nobody as the actor, never the address or the number.
        var line = await _db.OrgActivityEntries.AsNoTracking().SingleAsync();
        Assert.Equal(OrgActivityType.ContactInfoRead, line.Type);
        Assert.Equal(OrgActivitySubjectType.Booking, line.SubjectType);
        Assert.Equal(stay.Id.ToString(), line.SubjectId);
        Assert.Null(line.ActorUserId);
        Assert.Equal("{\"purpose\":\"check_in_link\"}", line.DetailsJson);
        Assert.Contains(ContactReadPurposes.CheckInLink, ContactReadPurposes.All);

        var everything = new[]
        {
            rig.Log.AllOutput,
            JsonSerializer.Serialize(await _db.OrgActivityEntries.AsNoTracking().ToListAsync()),
            JsonSerializer.Serialize(await _db.InternalActivities.AsNoTracking().ToListAsync()),
        };
        foreach (var text in everything)
        {
            Assert.DoesNotContain("tanaka", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-mail", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1234 5678", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// PROOF TEST: data rule (S18, D24). A refusal of the channel manager (the static IP addresses of CasaZen are not registered, or the application has no permission) is the state
    /// <c>StaticIpsRequired</c>, never an empty contact: nothing is written on the guest, nothing is recorded (nothing was released), and nothing of the answer is in the log.
    /// </summary>
    [Fact]
    public async Task ContactInfo_ForbiddenWaitsStaticIps_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world);
        rig.ChannelHolds(GuestEmail, GuestPhone);
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateSensitiveDataNotAllowedException(403));

        var outcome = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id);

        Assert.Equal(GuestContactInfoStatus.StaticIpsRequired, outcome.Status);
        var guest = await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == stay.GuestId);
        Assert.Equal(string.Empty, guest.Email);
        Assert.Equal(string.Empty, guest.PhoneNumber);
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.DoesNotContain("guest-mail", rig.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Nothing is asked unless every rule holds -----------------------------------------------------------------------------------------

    /// <summary>With the flag of the module off, or the connector off, nothing is asked: no call at all.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ContactInfo_FlagOrConnectorOff_AsksNothing_Test(bool guestContactInfo, bool connector)
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world);
        rig.Flags.Set(FeatureFlags.GuestContactInfo, guestContactInfo);
        rig.Flags.Set(FeatureFlags.OctorateConnector, connector);

        var outcome = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id);

        Assert.Equal(GuestContactInfoStatus.Disabled, outcome.Status);
        rig.Client.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A stay that is not a portal's, one that is over or cancelled, a no-show, a booking of another org, a guest that has an address, an erased guest and a house with no confirmed
    /// listing are not read: the contact of someone who does not need it is a sensitive read for nothing.
    /// </summary>
    [Fact]
    public async Task ContactInfo_OnlyAnActivePortalStayWithNoAddress_IsRead_Test()
    {
        var manual = await AddStayAsync(source: BookingSource.Manual, reservation: null);
        var cancelled = await AddStayAsync(status: BookingStatus.Cancelled);
        var noShow = await AddStayAsync(noShow: true);
        var withAddress = await AddStayAsync(guestEmail: "already@guest-mail.example");
        var erased = await AddStayAsync(erased: true);
        var rig = new Rig(_db, _world);
        rig.ChannelHolds(GuestEmail, GuestPhone);

        foreach (var stay in new[] { manual, cancelled, noShow, erased })
            Assert.Equal(GuestContactInfoStatus.NotApplicable, (await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id)).Status);

        Assert.Equal(GuestContactInfoStatus.AlreadyHasEmail, (await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, withAddress.Id)).Status);

        // A booking of this org asked for as another org's, and an unknown one.
        var theirs = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OtherOrgId, manual.Id);
        var unknown = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, Guid.NewGuid());
        Assert.Equal(GuestContactInfoStatus.NotApplicable, theirs.Status);
        Assert.Equal(GuestContactInfoStatus.NotApplicable, unknown.Status);

        // A house with no confirmed listing.
        rig.Targets.Setup(t => t.AccommodationOfAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var open = await AddStayAsync();
        Assert.Equal(GuestContactInfoStatus.NotApplicable, (await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, open.Id)).Status);

        rig.Client.VerifyNoOtherCalls();
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// When the channel manager says it holds no address (an Airbnb guest has none) the contact is not asked for: the reservation, which gives flags and no data, is the only call, and
    /// nothing is recorded. An answer that holds no usable address is a read that was made: recorded once, and the guest is not changed.
    /// </summary>
    [Fact]
    public async Task ContactInfo_NoAddressAtTheChannel_IsNotAskedFor_AndAnAnswerWithNoUsableAddressChangesNothing_Test()
    {
        var airbnb = await AddStayAsync();
        var rig = new Rig(_db, _world);
        rig.Client
            .Setup(c => c.GetReservationAsync("ACC-1", "RES-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation { Id = "RES-1", Refer = "REF-77", GuestEmailAvailable = false, GuestPhoneAvailable = false });

        var none = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, airbnb.Id);

        Assert.Equal(GuestContactInfoStatus.NoEmailAtChannel, none.Status);
        rig.Client.Verify(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());

        rig.ChannelHolds("not an address", "+39 333 1234567");
        var unusable = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, airbnb.Id);

        Assert.Equal(GuestContactInfoStatus.NoEmailAtChannel, unusable.Status);
        Assert.Equal(string.Empty, (await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == airbnb.GuestId)).Email);
        Assert.Single(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A failure that says nothing about the permission (an outage, a limit) is thrown, never read as «no contact»: the caller decides whether to ask again. A reservation the channel
    /// manager does not know, and an answer CasaZen cannot read, are states with a code, and the second one still records the read.
    /// </summary>
    [Fact]
    public async Task ContactInfo_AFailureIsNeverReadAsNoContact_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world);
        rig.ChannelHolds(GuestEmail, GuestPhone);
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));

        await Assert.ThrowsAsync<OctorateServerException>(() => rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id));
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());

        rig.Client
            .Setup(c => c.GetReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateNotFoundException());
        var notFound = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id);
        Assert.Equal((GuestContactInfoStatus.NotAvailable, "octorate_not_found"), (notFound.Status, notFound.ErrorCode));

        rig.ChannelHolds(GuestEmail, GuestPhone);
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo(null, null, Recognized: false));
        var unreadable = await rig.Service.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Id);
        Assert.Equal((GuestContactInfoStatus.NotAvailable, GuestContactInfoService.UnrecognizedCode), (unreadable.Status, unreadable.ErrorCode));
        Assert.Single(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.Equal(string.Empty, (await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == stay.GuestId)).Email);
    }

    /// <summary>
    /// PROOF TEST: data rule (the review of S010, fix 9). The number the channel manager gives is for the host's view and only where the guest had none: a number the guest already has is never
    /// overwritten. What does not fit the columns of the guest (an address of more than 255 characters, a number of more than 20) is not written, and what fits at the limit is: the guest keeps what
    /// it had and gets only what is valid.
    /// </summary>
    [Fact]
    public async Task ContactInfo_APhoneTheGuestHasIsNeverOverwritten_AndOnlyWhatFitsTheColumnsIsWritten_Test()
    {
        var rig = new Rig(_db, _world);
        var orgId = _world.Scope.OrgId;
        var longestAddress = new string('a', 47) + "@" + string.Join(".", Enumerable.Repeat(new string('b', 50), 4)) + ".com";
        var longestPhone = "+" + new string('1', 19);
        Assert.Equal((255, 20), (longestAddress.Length, longestPhone.Length));

        // A number the guest already has stays; the address is written.
        var hasPhone = await AddStayAsync(guestPhone: "+39 333 7654321");
        rig.ChannelHolds(GuestEmail, GuestPhone);
        Assert.Equal(GuestContactInfoStatus.EmailFilled, (await rig.Service.ReadForCheckInLinkAsync(orgId, hasPhone.Id)).Status);
        var kept = await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == hasPhone.GuestId);
        Assert.Equal((GuestEmail, "+39 333 7654321"), (kept.Email, kept.PhoneNumber));

        // An address of 256 characters is no address (the guest keeps none), the number that fits is written.
        var tooLongAddress = await AddStayAsync();
        rig.ChannelHolds(longestAddress + "m", GuestPhone);
        Assert.Equal(GuestContactInfoStatus.NoEmailAtChannel, (await rig.Service.ReadForCheckInLinkAsync(orgId, tooLongAddress.Id)).Status);
        var noAddress = await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == tooLongAddress.GuestId);
        Assert.Equal((string.Empty, GuestPhone), (noAddress.Email, noAddress.PhoneNumber));

        // A number of 21 characters is not kept, the address is.
        var tooLongPhone = await AddStayAsync();
        rig.ChannelHolds(GuestEmail, longestPhone + "1");
        Assert.Equal(GuestContactInfoStatus.EmailFilled, (await rig.Service.ReadForCheckInLinkAsync(orgId, tooLongPhone.Id)).Status);
        var noPhone = await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == tooLongPhone.GuestId);
        Assert.Equal((GuestEmail, string.Empty), (noPhone.Email, noPhone.PhoneNumber));

        // At the limit of the columns (255 and 20 characters) both are written.
        var atTheLimit = await AddStayAsync();
        rig.ChannelHolds(longestAddress, longestPhone);
        Assert.Equal(GuestContactInfoStatus.EmailFilled, (await rig.Service.ReadForCheckInLinkAsync(orgId, atTheLimit.Id)).Status);
        var full = await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == atTheLimit.GuestId);
        Assert.Equal((longestAddress, longestPhone), (full.Email, full.PhoneNumber));
    }

    // ---- The data every test runs on ----------------------------------------------------------------------------------------------------

    private async Task<Stay> AddStayAsync(
        BookingSource source = BookingSource.Airbnb,
        string? reservation = "RES-1",
        BookingStatus status = BookingStatus.Confirmed,
        bool noShow = false,
        string guestEmail = "",
        bool erased = false,
        string guestPhone = "")
    {
        var orgId = _world.Scope.OrgId;
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "Mei",
            LastName = "Tanaka",
            Email = guestEmail,
            PhoneNumber = guestPhone,
            DataAnonymizedDate = erased ? HostScopeScenario.Now.UtcDateTime : null,
        };
        _db.Guests.Add(guest);
        var checkIn = HostScopeScenario.Day(2026, 10, 12);
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = _world.Scope.Granted.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = source,
            OctorateReservationId = reservation,
            NoShowAt = noShow ? HostScopeScenario.Now.UtcDateTime : null,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new Stay(booking.Id, guest.Id);
    }

    private sealed record Stay(Guid Id, Guid GuestId);

    private sealed class Rig
    {
        public Rig(AppDbContext db, MessageHubWorld world)
        {
            Flags = new TestFeatureFlags(FeatureFlags.OctorateConnector, FeatureFlags.GuestContactInfo);
            Targets.Setup(t => t.AccommodationOfAsync(world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync("ACC-1");
            Service = new GuestContactInfoService(
                db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                Targets.Object,
                Flags,
                new ActivityLog(db),
                Log,
                new FixedTimeProvider(MessageHubTestKit.Now));
        }

        public Mock<IOctorateApiClient> Client { get; } = new();

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public TestFeatureFlags Flags { get; }

        public CapturingLogger<GuestContactInfoService> Log { get; } = new();

        public GuestContactInfoService Service { get; }

        /// <summary>The channel manager holds an address and a number for the reservation <c>RES-1</c> and gives them for the contact <c>REF-77</c>.</summary>
        public void ChannelHolds(string email, string phone)
        {
            Client
                .Setup(c => c.GetReservationAsync("ACC-1", "RES-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OctorateReservation { Id = "RES-1", Refer = "REF-77", GuestEmailAvailable = true, GuestPhoneAvailable = true });
            Client
                .Setup(c => c.GetReservationContactInfoAsync("ACC-1", "REF-77", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OctorateGuestContactInfo(email, phone));
        }
    }
}
