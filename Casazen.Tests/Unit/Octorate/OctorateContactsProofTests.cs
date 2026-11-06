using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E07/F06/US02/T02 (NF-94, CRM-07, D24, D34): the contact of the guest of ONE Octorate booking, read when someone needs it. The contacts are
/// sensitive data that Octorate releases only to the registered IP addresses of CasaZen and logs on every read, so the proofs are about what is
/// never read (nothing before the product owner's switch, no history, no more than the cap, no list), what is never marketable (an OTA forward),
/// when a customer of the booking engine is soft opt-in, and that only a booking that came from Octorate triggers a read. The service is the real
/// one over the real contact service on EF InMemory; the Octorate client is the interface, as in the tests of the import: no Octorate code is
/// mocked in the application.
/// </summary>
public class OctorateContactsProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Guid OrgA = BookingImportTestKit.OrgA;

    /// <summary>
    /// Proof test (privacy rule, AC-NF94-4): an address that arrives with an OTA booking, or that is on a relay domain, is stored as an OTA
    /// forward, service only, and is never reachable for marketing, not even when the booking engine's soft opt-in is declared. The phone number is
    /// stored too, as service only. Nothing the read returns reaches a log.
    /// </summary>
    [Fact]
    public async Task OctorateContacts_OtaForwardAddress_StoredServiceOnly_Test()
    {
        var h = new BookingImportHarness();
        var viaAirbnb = await h.SeedBookingAsync("res-airbnb", source: BookingSource.Airbnb);
        var viaRelay = await h.SeedBookingAsync("res-relay", source: BookingSource.Direct);
        Serve(h, "res-airbnb", "guest-airbnb", "reply-9f3a@guest.airbnb.example", "+39 333 1234567", channelId: "274");
        Serve(h, "res-relay", "guest-relay", "customer@relay.example.org", phone: null, channelId: BookingStateMapper.BookingEngineChannelId);
        var options = new CrmOptions
        {
            OctorateContactsEnabled = true,
            BookingEngineSoftOptIn = true,
            OtaForwardDomains = ["relay.example.org"],
        };
        var log = new CapturingLogger<OctorateContactsService>();

        await using (var db = h.NewDb())
        {
            var service = Service(h, db, options, log: log);
            var first = await service.ReadForBookingAsync(OrgA, viaAirbnb, Ct);
            var second = await service.ReadForBookingAsync(OrgA, viaRelay, Ct);

            Assert.Equal(OctorateContactReadOutcome.Stored, first.Outcome);
            Assert.Equal(2, first.ContactsStored);
            Assert.Equal(OctorateContactReadOutcome.Stored, second.Outcome);
            Assert.Equal(1, second.ContactsStored);
        }

        await using var verify = h.NewDb();
        var contacts = await verify.GuestContacts.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(3, contacts.Count);

        // The address of the OTA booking is a forward: service only, whatever else is declared.
        var forwardedByOta = Assert.Single(contacts, c => c.Channel == GuestContactChannel.Email && c.Value == "reply-9f3a@guest.airbnb.example");
        Assert.Equal(GuestContactOrigin.OtaForward, forwardedByOta.Origin);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, forwardedByOta.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(forwardedByOta));
        Assert.Equal(CrmContactRules.ReasonOtaForward, CrmContactRules.ReachabilityReason(forwardedByOta));
        Assert.True(CrmContactRules.CanReceiveService(forwardedByOta));

        // The address of a customer of the booking engine that is on a relay domain is a forward too, although the soft opt-in is on and the
        // booking is confirmed.
        var forwardedByDomain = Assert.Single(contacts, c => c.Value == "customer@relay.example.org");
        Assert.Equal(GuestContactOrigin.OtaForward, forwardedByDomain.Origin);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, forwardedByDomain.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(forwardedByDomain));

        // The phone number: service only, from Octorate.
        var phone = Assert.Single(contacts, c => c.Channel == GuestContactChannel.Phone);
        Assert.Equal(GuestContactOrigin.OctorateContactInfo, phone.Origin);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, phone.LegalBasis);
        Assert.Equal(CrmNormalizer.NormalizePhone("+39 333 1234567", "+39"), phone.Value);
        Assert.False(CrmContactRules.CanReceiveMarketing(phone));

        // The contacts belong to the guest of their booking.
        var guestOfAirbnb = await verify.Bookings.AsNoTracking().Where(b => b.Id == viaAirbnb).Select(b => b.GuestId).SingleAsync(Ct);
        Assert.All(contacts.Where(c => c.Value != "customer@relay.example.org"), c => Assert.Equal(guestOfAirbnb, c.GuestId));

        // No address, number or name in a log.
        Assert.DoesNotContain("example", log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234567", log.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proof test (privacy rule, AC-NF94-5): contacts are never read in bulk. A stay whose check-out is past is not read, a booking that is not an
    /// Octorate one is not read, an org gets at most <c>Crm:OctorateContactReadsPerHour</c> reads an hour (the third booking waits, and is read once the
    /// hour has passed), a booking read lately is not read again, and the list of the reservations is never asked for.
    /// </summary>
    [Fact]
    public async Task OctorateContacts_HistoricalOrOverCapBooking_IsNotRead_Test()
    {
        var h = new BookingImportHarness();
        var options = new CrmOptions { OctorateContactsEnabled = true, OctorateContactReadsPerHour = 2 };
        var upcoming = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            upcoming.Add(await h.SeedBookingAsync($"res-{i}", source: BookingSource.BookingCom));
            Serve(h, $"res-{i}", $"guest-{i}", $"guest{i}@guest.example.org", phone: null);
        }

        var over = await h.SeedBookingAsync("res-over", source: BookingSource.BookingCom);
        await MoveStayAsync(h, over, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        Serve(h, "res-over", "guest-over", "guest-over@guest.example.org", phone: null);
        var notOctorate = await h.SeedBookingAsync(octorateId: null, source: BookingSource.Manual);

        await using var db = h.NewDb();
        var service = Service(h, db, options);

        // History and bookings of CasaZen are not read.
        Assert.Equal(OctorateContactReadOutcome.StayOver, (await service.ReadForBookingAsync(OrgA, over, Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.NotAnOctorateBooking, (await service.ReadForBookingAsync(OrgA, notOctorate, Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.NotAnOctorateBooking, (await service.ReadForBookingAsync(OrgA, Guid.NewGuid(), Ct)).Outcome);

        // The cap: two reads in the hour, the third booking waits.
        Assert.Equal(OctorateContactReadOutcome.Stored, (await service.ReadForBookingAsync(OrgA, upcoming[0], Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.Stored, (await service.ReadForBookingAsync(OrgA, upcoming[1], Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.OverCap, (await service.ReadForBookingAsync(OrgA, upcoming[2], Ct)).Outcome);

        h.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        h.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), "guest-3", It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), "guest-over", It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetReservationAsync(It.IsAny<string>(), "res-3", It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetReservationAsync(It.IsAny<string>(), "res-over", It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetReservationsAsync(It.IsAny<string>(), It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()), Times.Never);

        // The hour has passed: the booking that waited is read, and the one read lately is not asked for again.
        h.Clock.Advance(OctorateContactReadLedger.Window + TimeSpan.FromMinutes(1));
        Assert.Equal(OctorateContactReadOutcome.Stored, (await service.ReadForBookingAsync(OrgA, upcoming[2], Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.AlreadyRead, (await service.ReadForBookingAsync(OrgA, upcoming[0], Ct)).Outcome);
        h.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

        // Three bookings were read, one address each: nothing was stored for the others.
        Assert.Equal(3, await db.GuestContacts.CountAsync(Ct));
    }

    /// <summary>
    /// Proof test (privacy rule, AC-NF94-1): nothing sensitive is read before the product owner declares the IP addresses ready. With
    /// <c>Crm:OctorateContactsEnabled</c> off (the default), or with the connector off, the call returns at once: no query, no call to Octorate,
    /// no contact.
    /// </summary>
    [Fact]
    public async Task OctorateContacts_FlagOff_ReadsNothing_Test()
    {
        var h = new BookingImportHarness();
        var bookingId = await h.SeedBookingAsync("res-1", source: BookingSource.BookingCom);
        Serve(h, "res-1", "guest-1", "guest1@guest.example.org", "+393331234567");

        await using var db = h.NewDb();

        // The default of the options: off.
        var off = await Service(h, db, new CrmOptions()).ReadForBookingAsync(OrgA, bookingId, Ct);
        Assert.Equal(OctorateContactReadOutcome.Disabled, off.Outcome);
        Assert.False(off.HasContact);

        // The switch on, the connector off.
        var noConnector = await Service(h, db, new CrmOptions { OctorateContactsEnabled = true }, new TestFeatureFlags())
            .ReadForBookingAsync(OrgA, bookingId, Ct);
        Assert.Equal(OctorateContactReadOutcome.Disabled, noConnector.Outcome);

        h.Client.Verify(c => c.GetReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await db.GuestContacts.AnyAsync(Ct));

        // Both on: the same call reads.
        var on = await Service(h, db, new CrmOptions { OctorateContactsEnabled = true }).ReadForBookingAsync(OrgA, bookingId, Ct);
        Assert.Equal(OctorateContactReadOutcome.Stored, on.Outcome);
    }

    /// <summary>
    /// Proof test (privacy rule, AC-NF94-2): a customer of the booking engine of the agency is soft opt-in for a confirmed booking only while the
    /// product owner's declaration (<c>Crm:BookingEngineSoftOptIn</c>) is on; with it off, or for a booking that is still pending, the address is
    /// service only and is not reachable for marketing.
    /// </summary>
    [Fact]
    public async Task OctorateContacts_BookingEngineCustomer_IsSoftOptInOnlyWhileTheDeclarationIsOn_Test()
    {
        var h = new BookingImportHarness();
        var declaredOff = await h.SeedBookingAsync("res-off", source: BookingSource.Direct, status: BookingStatus.Confirmed);
        var confirmed = await h.SeedBookingAsync("res-confirmed", source: BookingSource.Direct, status: BookingStatus.Confirmed);
        var pending = await h.SeedBookingAsync("res-pending", source: BookingSource.Direct, status: BookingStatus.Pending);
        var channel = BookingStateMapper.BookingEngineChannelId;
        Serve(h, "res-off", "guest-off", "off@customer.example.org", "+393331111111", channel);
        Serve(h, "res-confirmed", "guest-confirmed", "confirmed@customer.example.org", "+393332222222", channel);
        Serve(h, "res-pending", "guest-pending", "pending@customer.example.org", phone: null, channelId: channel);

        await using (var db = h.NewDb())
        {
            var off = Service(h, db, new CrmOptions { OctorateContactsEnabled = true, BookingEngineSoftOptIn = false });
            Assert.Equal(OctorateContactReadOutcome.Stored, (await off.ReadForBookingAsync(OrgA, declaredOff, Ct)).Outcome);
        }

        await using (var db = h.NewDb())
        {
            var on = Service(h, db, new CrmOptions { OctorateContactsEnabled = true, BookingEngineSoftOptIn = true });
            Assert.Equal(OctorateContactReadOutcome.Stored, (await on.ReadForBookingAsync(OrgA, confirmed, Ct)).Outcome);
            Assert.Equal(OctorateContactReadOutcome.Stored, (await on.ReadForBookingAsync(OrgA, pending, Ct)).Outcome);
        }

        await using var verify = h.NewDb();
        var contacts = await verify.GuestContacts.AsNoTracking().ToListAsync(Ct);

        // The declaration is off: service only.
        var withoutDeclaration = Assert.Single(contacts, c => c.Value == "off@customer.example.org");
        Assert.Equal(GuestContactOrigin.OctorateContactInfo, withoutDeclaration.Origin);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, withoutDeclaration.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(withoutDeclaration));

        // The declaration is on and the booking is confirmed: the address is soft opt-in, with the notice recorded; the phone number stays service only.
        var softOptIn = Assert.Single(contacts, c => c.Value == "confirmed@customer.example.org");
        Assert.Equal(GuestContactOrigin.OctorateContactInfo, softOptIn.Origin);
        Assert.Equal(GuestContactLegalBasis.SoftOptIn, softOptIn.LegalBasis);
        Assert.Equal(OctorateContactsService.BookingEngineNoticeVersion, softOptIn.BasisVersion);
        Assert.True(CrmContactRules.CanReceiveMarketing(softOptIn));
        var phone = Assert.Single(contacts, c => c.Channel == GuestContactChannel.Phone && c.GuestId == softOptIn.GuestId);
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, phone.LegalBasis);

        // The declaration is on and the booking is still pending: not a purchase yet, service only.
        var notYet = Assert.Single(contacts, c => c.Value == "pending@customer.example.org");
        Assert.Equal(GuestContactLegalBasis.ServiceOnly, notYet.LegalBasis);
        Assert.False(CrmContactRules.CanReceiveMarketing(notYet));
    }

    /// <summary>
    /// Proof test (permission rule, AC-NF94-6): a refusal of the sensitive data (no static IP, no permission) is recorded as not available for the
    /// booking and is not asked again in a loop: the second call, and the calls of the next hours, do not reach Octorate; an outage is not recorded
    /// (a later call tries again) and nothing is stored in either case.
    /// </summary>
    [Fact]
    public async Task OctorateContacts_RefusedRead_IsRecordedAsNotAvailableAndNotRetriedInALoop_Test()
    {
        var h = new BookingImportHarness();
        var refused = await h.SeedBookingAsync("res-refused", source: BookingSource.BookingCom);
        var outage = await h.SeedBookingAsync("res-outage", source: BookingSource.BookingCom);
        h.Client
            .Setup(c => c.GetReservationAsync(BookingImportTestKit.Accommodation, "res-refused", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation { Id = "res-refused", GuestId = "guest-refused" });
        h.Client
            .Setup(c => c.GetGuestContactInfoAsync(BookingImportTestKit.Accommodation, "guest-refused", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateSensitiveDataNotAllowedException(403));
        h.Client
            .Setup(c => c.GetReservationAsync(BookingImportTestKit.Accommodation, "res-outage", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        var log = new CapturingLogger<OctorateContactsService>();

        await using var db = h.NewDb();
        var service = Service(h, db, new CrmOptions { OctorateContactsEnabled = true }, log: log);

        Assert.Equal(OctorateContactReadOutcome.NotAvailable, (await service.ReadForBookingAsync(OrgA, refused, Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.NotAvailable, (await service.ReadForBookingAsync(OrgA, refused, Ct)).Outcome);
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(OctorateContactReadOutcome.NotAvailable, (await service.ReadForBookingAsync(OrgA, refused, Ct)).Outcome);
        h.Client.Verify(c => c.GetGuestContactInfoAsync(It.IsAny<string>(), "guest-refused", It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(OctorateContactReadOutcome.Deferred, (await service.ReadForBookingAsync(OrgA, outage, Ct)).Outcome);
        Assert.Equal(OctorateContactReadOutcome.Deferred, (await service.ReadForBookingAsync(OrgA, outage, Ct)).Outcome);
        h.Client.Verify(c => c.GetReservationAsync(It.IsAny<string>(), "res-outage", It.IsAny<CancellationToken>()), Times.Exactly(2));

        Assert.False(await db.GuestContacts.AnyAsync(Ct));
        Assert.DoesNotContain("example", log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proof test (privacy rule): the event path reads the contact only of a booking that came from Octorate. <c>BookingCreated</c> with the origin
    /// Octorate reads it; the same event of a booking made in CasaZen, another kind of event and a payload that is not a booking event read nothing;
    /// a service that fails does not fail the event and the failure is logged without the message of the error.
    /// </summary>
    [Fact]
    public async Task OctorateContactsEventHandler_OnlyAnOctorateBookingCreated_ReadsTheContact_Test()
    {
        var service = new Mock<IOctorateContactsService>();
        service
            .Setup(s => s.ReadForBookingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateContactReadResult(OctorateContactReadOutcome.Stored, 1));
        var log = new CapturingLogger<OctorateContactsEventHandler>();
        var handler = new OctorateContactsEventHandler(service.Object, log);
        var bookingId = Guid.NewGuid();

        await handler.HandleAsync(EventOf(DomainEventType.BookingCreated, bookingId, BookingEventOrigin.Octorate), Ct);
        service.Verify(s => s.ReadForBookingAsync(OrgA, bookingId, It.IsAny<CancellationToken>()), Times.Once);

        await handler.HandleAsync(EventOf(DomainEventType.BookingCreated, bookingId, BookingEventOrigin.Host), Ct);
        await handler.HandleAsync(EventOf(DomainEventType.BookingCreated, bookingId, BookingEventOrigin.Guest), Ct);
        await handler.HandleAsync(EventOf(DomainEventType.BookingConfirmed, bookingId, BookingEventOrigin.Octorate), Ct);
        await handler.HandleAsync(EventOf(DomainEventType.BookingModified, bookingId, BookingEventOrigin.Octorate), Ct);
        await handler.HandleAsync(
            new DomainEvent(Guid.NewGuid(), OrgA, DomainEventType.BookingCreated, bookingId, BookingImportTestKit.FirstVersion, "not a payload"),
            Ct);
        await handler.HandleAsync(EventOf(DomainEventType.BookingCreated, Guid.Empty, BookingEventOrigin.Octorate), Ct);
        service.Verify(s => s.ReadForBookingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

        // A failure does not fail the event, and the log has the ids and the kind of error, never its message.
        service.Reset();
        service
            .Setup(s => s.ReadForBookingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mario.rossi@example.com +393331234567"));
        await handler.HandleAsync(EventOf(DomainEventType.BookingCreated, bookingId, BookingEventOrigin.Octorate), Ct);

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(bookingId, Assert.IsType<Guid>(warning.Values["BookingId"]));
        Assert.DoesNotContain("example", log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234567", log.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>The cap is validated at startup: zero or a huge number would switch the guard off without a word.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(50, false)]
    [InlineData(500, false)]
    [InlineData(501, true)]
    [InlineData(-3, true)]
    public void CrmOptions_TheCapOfTheContactReads_IsCheckedAtStartup(int perHour, bool fails)
    {
        var validator = new CrmOptionsValidator(new TestFeatureFlags(), Mock.Of<IHostEnvironment>(host => host.EnvironmentName == "Production"));

        var result = validator.Validate(null, new CrmOptions { OctorateContactReadsPerHour = perHour });

        Assert.Equal(fails, result.Failed);
        Assert.Equal(CrmOptions.DefaultOctorateContactReadsPerHour, new CrmOptions().OctorateContactReadsPerHour);
    }

    // ---- the world of the proofs --------------------------------------------------------------------------------------------------

    private static OctorateContactsService Service(
        BookingImportHarness h,
        AppDbContext db,
        CrmOptions options,
        IFeatureFlags? flags = null,
        OctorateContactReadLedger? ledger = null,
        ILogger<OctorateContactsService>? log = null) =>
        new(
            db,
            h.Client.Object,
            h.Scope(),
            CrmTestWorld.Contacts(db, h.Clock, options),
            ledger ?? new OctorateContactReadLedger(),
            Options.Create(options),
            flags ?? new TestFeatureFlags(FeatureFlags.OctorateConnector),
            h.Clock,
            log ?? NullLogger<OctorateContactsService>.Instance);

    /// <summary>Octorate answers the reservation (with the id of its guest) and, for that guest, the address and the number given (<c>null</c>: none).</summary>
    private static void Serve(
        BookingImportHarness h,
        string reservationId,
        string guestId,
        string? email,
        string? phone,
        string channelId = "274")
    {
        h.Client
            .Setup(c => c.GetReservationAsync(BookingImportTestKit.Accommodation, reservationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation
            {
                Id = reservationId,
                Status = OctorateBookingStatus.Confirmed,
                ChannelId = channelId,
                GuestId = guestId,
                GuestEmailAvailable = email is not null,
                GuestPhoneAvailable = phone is not null,
            });
        h.Client
            .Setup(c => c.GetGuestContactInfoAsync(BookingImportTestKit.Accommodation, guestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo(email, phone));
    }

    private static async Task MoveStayAsync(BookingImportHarness h, Guid bookingId, DateTime checkIn, DateTime checkOut)
    {
        await using var db = h.NewDb();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.CheckInDate = checkIn;
        booking.CheckOutDate = checkOut;
        await db.SaveChangesAsync();
    }

    private static DomainEvent EventOf(DomainEventType type, Guid bookingId, BookingEventOrigin origin)
    {
        var payload = new BookingEventPayload(
            bookingId,
            Guid.NewGuid(),
            BookingSource.BookingCom,
            origin,
            BookingStatus.Confirmed,
            new DateOnly(2026, 11, 20),
            new DateOnly(2026, 11, 22));
        return new DomainEvent(Guid.NewGuid(), OrgA, type, bookingId, BookingImportTestKit.FirstVersion, JsonSerializer.Serialize(payload));
    }
}
