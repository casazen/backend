using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The cancellation of a booking that came from a channel (E08/F02/US02/T02, D8, NF-76): Octorate first, CasaZen after. The booking is cancelled
/// only when Octorate confirmed the cancellation of its reservation; whatever stops the request before that leaves it exactly as it was.
/// </summary>
public class OtaBookingCancellationTests
{
    private static readonly Guid OrgA = OctorateSyncHarness.OrgA;
    private static readonly Guid OrgB = OctorateSyncHarness.OrgB;

    /// <summary>
    /// PROOF TEST: data rule (D8, NF-76, "never a success Octorate did not confirm"). A booking of an open channel is cancelled in CasaZen only
    /// after Octorate confirmed: when Octorate refuses, or does not answer, the booking is exactly as it was (no status, no time, no note, no event)
    /// and the answer says so with the sentence of the channel; Octorate is asked first (the booking is still confirmed in the database while the
    /// call is made); when it confirms, the booking is cancelled, the note is kept and one <c>BookingCancelled</c> is recorded as a change that
    /// came from the channel manager, which the push of the cancellations CasaZen makes does not send back.
    /// </summary>
    [Fact]
    public async Task OtaBookingCancel_OnlyAfterOctorateConfirms_Test()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedChannelBookingAsync(h);
        var tenant = new RequestTenant(OrgA);
        var before = await h.ReadBookingAsync(bookingId);

        var seenDuringTheCall = new List<BookingStatus>();
        var answers = new Queue<Exception?>([new OctorateRequestRejectedException(400), new OctorateServerException(null), null]);
        h.Client
            .Setup(c => c.CancelReservationAsync(
                It.Is<OctorateBookingTarget>(t => t.AccommodationId == OctorateSyncHarness.Accommodation && t.ProductId == OctorateSyncHarness.Room),
                "7001",
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // Octorate is asked while the booking is still as it was in the database.
                seenDuringTheCall.Add((await h.ReadBookingAsync(bookingId)).Status);
                if (answers.Dequeue() is { } error)
                    throw error;
            });

        // Octorate refuses: 422, and the booking is untouched.
        await using (var db = h.NewDb(tenant))
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => Service(h, db, tenant, Open(BookingSource.Airbnb)).CancelAsync(bookingId, "Ospite non arrivato", CancellationToken.None));
            Assert.Equal(BookingErrorCodes.OctorateCancelFailed, refused.Code);
            Assert.Equal("OtaChannelCancelGuidanceAirbnb", refused.MessageKey);
            Assert.Equal("Airbnb", refused.Extensions[OtaChannelActionPolicy.ChannelExtension]);
        }

        await AssertUntouchedAsync(h, bookingId, before);

        // Octorate does not answer: 503, and the booking is untouched.
        await using (var db = h.NewDb(tenant))
        {
            var unreachable = await Assert.ThrowsAsync<OtaCancellationUnavailableException>(
                () => Service(h, db, tenant, Open(BookingSource.Airbnb)).CancelAsync(bookingId, "Ospite non arrivato", CancellationToken.None));
            Assert.Equal("OtaChannelCancelGuidanceAirbnb", unreachable.GuidanceKey);
            Assert.Equal("Airbnb", unreachable.Channel);
        }

        await AssertUntouchedAsync(h, bookingId, before);

        // Octorate confirms: the booking is cancelled, with the reason of the host as its note.
        await using (var db = h.NewDb(tenant))
        {
            var cancelled = await Service(h, db, tenant, Open(BookingSource.Airbnb)).CancelAsync(bookingId, "  Ospite non arrivato  ", CancellationToken.None);
            Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        }

        var stored = await h.ReadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, stored.CancelledAt);
        Assert.Equal("Ospite non arrivato", stored.CancellationNote);
        Assert.Null(stored.CancellationReason);
        Assert.Equal("7001", stored.OctorateReservationId);

        // Asked first, every time: the booking was confirmed in the database while Octorate was being asked.
        Assert.Equal([BookingStatus.Confirmed, BookingStatus.Confirmed, BookingStatus.Confirmed], seenDuringTheCall);

        // One event, a change that came from the channel manager: nothing is pushed back to it.
        var events = await EventsAsync(h, bookingId);
        var recorded = Assert.Single(events);
        Assert.Equal(DomainEventType.BookingCancelled, recorded.EventType);
        var payload = JsonSerializer.Deserialize<BookingEventPayload>(recorded.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal(BookingEventOrigin.Octorate, payload.Origin);
        Assert.Equal(BookingStatus.Cancelled, payload.Status);
    }

    [Fact]
    public async Task CancelAsync_ClosedChannel_AsksOctorateNothing_AndChangesNothing()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedChannelBookingAsync(h);
        var tenant = new RequestTenant(OrgA);
        var before = await h.ReadBookingAsync(bookingId);

        await using (var db = h.NewDb(tenant))
        {
            // The configuration opens Expedia only: an Airbnb booking is closed, and so is the default of no entry at all.
            foreach (var policy in new[] { OtaChannelActionPolicy.Closed, Open(BookingSource.Expedia) })
            {
                var refused = await Assert.ThrowsAsync<DomainRuleException>(
                    () => Service(h, db, tenant, policy).CancelAsync(bookingId, null, CancellationToken.None));
                Assert.Equal(BookingErrorCodes.OtaBookingCancelNotAllowed, refused.Code);
                Assert.Equal("OtaChannelCancelGuidanceAirbnb", refused.MessageKey);
            }
        }

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await AssertUntouchedAsync(h, bookingId, before);
    }

    [Fact]
    public async Task CancelAsync_ConnectorOff_OrPropertyNotMirrored_IsRefusedWithoutAskingOctorate()
    {
        var h = new OctorateSyncHarness();
        var mirrored = await SeedChannelBookingAsync(h);
        var tenant = new RequestTenant(OrgA);

        // A property of the org that has no confirmed mapping: nothing is ever written to Octorate for it.
        Guid unmappedProperty;
        await using (var seed = h.NewDb())
        {
            var property = new Property
            {
                OrgId = OrgA,
                OwnerId = "auth0|owner",
                Name = "Casa senza mapping",
                Address = "Via Nuova 2",
                City = "Como",
                MaxGuests = 4,
                Currency = "EUR",
            };
            seed.Properties.Add(property);
            await seed.SaveChangesAsync();
            unmappedProperty = property.Id;
        }

        var unmapped = await SeedChannelBookingAsync(h, propertyId: unmappedProperty, bookingCode: "U7Q2M9X4TB", reservationId: "7002");

        await using (var db = h.NewDb(tenant))
        {
            var off = await Assert.ThrowsAsync<DomainRuleException>(
                () => Service(h, db, tenant, Open(BookingSource.Airbnb), connector: false).CancelAsync(mirrored, null, CancellationToken.None));
            Assert.Equal(BookingErrorCodes.OctorateCancelFailed, off.Code);

            var notMirrored = await Assert.ThrowsAsync<DomainRuleException>(
                () => Service(h, db, tenant, Open(BookingSource.Airbnb)).CancelAsync(unmapped, null, CancellationToken.None));
            Assert.Equal(BookingErrorCodes.OctorateCancelFailed, notMirrored.Code);
        }

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(BookingStatus.Confirmed, (await h.ReadBookingAsync(mirrored)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await h.ReadBookingAsync(unmapped)).Status);
    }

    [Fact]
    public async Task CancelAsync_WhateverOctorateAnswers_IsA422WhenItRefusesAnd503WhenItDoesNotAnswer()
    {
        var cases = new (Exception Error, bool Unreachable)[]
        {
            (new OctorateConflictException(), false),
            (new OctorateNotFoundException(), false),
            (OctorateAuthException.Refused(403), false),
            (OctorateAuthException.NotConnected(), false),
            (new OctorateRateLimitException(429), true),
            (new OctorateServerException(502), true),
        };

        foreach (var (error, unreachable) in cases)
        {
            var h = new OctorateSyncHarness();
            var bookingId = await SeedChannelBookingAsync(h);
            var tenant = new RequestTenant(OrgA);
            h.Client
                .Setup(c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), "7001", It.IsAny<CancellationToken>()))
                .ThrowsAsync(error);

            await using var db = h.NewDb(tenant);
            var service = Service(h, db, tenant, Open(BookingSource.Airbnb));
            if (unreachable)
                await Assert.ThrowsAsync<OtaCancellationUnavailableException>(() => service.CancelAsync(bookingId, null, CancellationToken.None));
            else
                Assert.Equal(BookingErrorCodes.OctorateCancelFailed, (await Assert.ThrowsAsync<DomainRuleException>(() => service.CancelAsync(bookingId, null, CancellationToken.None))).Code);

            Assert.Equal(BookingStatus.Confirmed, (await h.ReadBookingAsync(bookingId)).Status);
        }
    }

    [Fact]
    public async Task CancelAsync_ABookingOfAnotherOrg_ABookingAlreadyCancelled_AndABookingNotOfAChannel_AreNotSentToOctorate()
    {
        var h = new OctorateSyncHarness();
        var bookingId = await SeedChannelBookingAsync(h);
        var manual = await h.SeedBookingAsync(bookingCode: "N7Q2M9X4TB");

        // Another org does not see the booking: 404, and Octorate is not asked.
        var tenantB = new RequestTenant(OrgB);
        await using (var other = h.NewDb(tenantB))
        {
            await Assert.ThrowsAsync<NotFoundException>(
                () => Service(h, other, tenantB, Open(BookingSource.Airbnb)).CancelAsync(bookingId, null, CancellationToken.None));
        }

        var tenant = new RequestTenant(OrgA);
        await using (var db = h.NewDb(tenant))
        {
            // A booking CasaZen holds is cancelled the other way round: this service is not for it.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Service(h, db, tenant, Open(BookingSource.Airbnb)).CancelAsync(manual, null, CancellationToken.None));
        }

        await using (var seed = h.NewDb())
        {
            (await seed.Bookings.SingleAsync(b => b.Id == bookingId)).Status = BookingStatus.Cancelled;
            await seed.SaveChangesAsync();
        }

        await using (var db = h.NewDb(tenant))
        {
            var already = await Assert.ThrowsAsync<DomainConflictException>(
                () => Service(h, db, tenant, Open(BookingSource.Airbnb)).CancelAsync(bookingId, null, CancellationToken.None));
            Assert.Equal(BookingErrorCodes.AlreadyCancelled, already.Code);
        }

        h.Client.Verify(
            c => c.CancelReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static OtaBookingCancellationService Service(
        OctorateSyncHarness h,
        AppDbContext db,
        RequestTenant tenant,
        OtaChannelActionPolicy policy,
        bool connector = true)
    {
        return new OtaBookingCancellationService(
            db,
            h.Client.Object,
            new OctorateOrgScope(tenant),
            h.Targets(db, tenant),
            policy,
            new DomainEventRecorder(db, tenant, h.Clock),
            Flags(connector),
            h.Clock,
            NullLogger<OtaBookingCancellationService>.Instance);
    }

    private static IFeatureFlags Flags(bool octorateConnector)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(octorateConnector);
        return flags.Object;
    }

    /// <summary>The configuration opens the cancellation of one channel.</summary>
    private static OtaChannelActionPolicy Open(BookingSource source)
    {
        var entry = new OctorateChannelActionOptions { CanCancel = true };
        var options = new OctorateChannelActionsOptions();
        switch (source)
        {
            case BookingSource.Airbnb:
                options.Airbnb = entry;
                break;
            case BookingSource.Expedia:
                options.Expedia = entry;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Not a channel of this test.");
        }

        return new OtaChannelActionPolicy(options);
    }

    /// <summary>A confirmed Airbnb booking of the org that has its reservation on Octorate.</summary>
    private static async Task<Guid> SeedChannelBookingAsync(
        OctorateSyncHarness h,
        Guid? propertyId = null,
        string bookingCode = "C7Q2M9X4TB",
        string reservationId = "7001")
    {
        var id = await h.SeedBookingAsync(propertyId: propertyId, source: BookingSource.Airbnb, bookingCode: bookingCode);
        await using var db = h.NewDb();
        (await db.Bookings.SingleAsync(b => b.Id == id)).OctorateReservationId = reservationId;
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task AssertUntouchedAsync(OctorateSyncHarness h, Guid bookingId, Booking before)
    {
        var stored = await h.ReadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Null(stored.CancelledAt);
        Assert.Null(stored.CancellationNote);
        Assert.Equal(before.UpdatedAt, stored.UpdatedAt);
        Assert.Empty(await EventsAsync(h, bookingId));
    }

    private static async Task<List<OutboxEvent>> EventsAsync(OctorateSyncHarness h, Guid bookingId)
    {
        await using var db = h.NewDb();
        return await db.OutboxEvents.AsNoTracking().Where(e => e.EntityId == bookingId).ToListAsync();
    }
}
