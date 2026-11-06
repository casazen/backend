using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02 trust proof (review fix): the daily job that sends the check-in link, and the one that reminds the data still missing, send nothing to
/// a stay whose identity verification is with an operator (rejected by one, or a hand-off still open): a new link is a new session, and the
/// guest would start the verification again, against the decision of a person. A plain stay still gets its link.
/// </summary>
public class GuestCheckInSendJobVerificationTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GuestCheckInSendJob_StayWithTheOperator_GetsNoNewLink_Test()
    {
        using var db = OrgTeamTestData.NewDb();
        var org = OrgTeamTestData.AddOrg(db);
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        db.Properties.Add(property);

        // A rejection of an operator; the link of the stay expired. A hand-off still open, on a link that is still usable. A plain stay whose link expired.
        var rejected = AddStay(db, org.Id, property.Id, GuestCheckInSessionStatus.Scaduto);
        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = org.Id,
            BookingId = rejected,
            GuestId = db.Bookings.Local.Single(b => b.Id == rejected).GuestId,
            Outcome = BiometricOutcome.Reject,
            Method = VerificationMethod.Operator,
            Attempts = 1,
            ModelName = "operator",
            ModelVersion = "n/a",
            ThresholdsJson = "{}",
            OperatorUserId = null,
            CreatedAt = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc),
        });
        var handedOver = AddStay(db, org.Id, property.Id, GuestCheckInSessionStatus.InCompilazione, operatorRequestedAt: new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc));
        var plain = AddStay(db, org.Id, property.Id, GuestCheckInSessionStatus.Scaduto);
        await db.SaveChangesAsync();

        var checkIn = new Mock<IGuestCheckInService>();
        checkIn.Setup(s => s.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new IssuedCheckInLink(Guid.NewGuid(), "token", Today.UtcDateTime.AddDays(7)));
        var job = new GuestCheckInSendJob(
            db,
            checkIn.Object,
            Mock.Of<IGuestCheckInLinkEmailQueue>(),
            new StayGuestService(db, Mock.Of<IAlloggiatiCodeTableService>()),
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" })),
            Options.Create(new GuestCheckInOptions()),
            NullLogger<GuestCheckInSendJob>.Instance,
            new FixedTimeProvider(Today));

        await job.ExecuteAsync();

        checkIn.Verify(s => s.IssueLinkAsync(rejected, org.Id), Times.Never);
        checkIn.Verify(s => s.IssueLinkAsync(handedOver, org.Id), Times.Never);
        checkIn.Verify(s => s.IssueLinkAsync(plain, org.Id), Times.AtLeastOnce);
    }

    /// <summary>A confirmed stay arriving tomorrow whose guest got a link on the 8th (in the given state), with the data still to complete.</summary>
    private static Guid AddStay(AppDbContext db, Guid orgId, Guid propertyId, GuestCheckInSessionStatus status, DateTime? operatorRequestedAt = null)
    {
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            FirstName = "Ospite",
            LastName = "Test",
            Email = $"{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 11),
            CheckOutDate = new DateTime(2026, 11, 13),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = orgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Status = status,
            ExpiresAt = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc),
            OperatorRequestedAt = operatorRequestedAt,
            BiometricOutcome = operatorRequestedAt is null ? null : BiometricOutcome.Escalate,
        });
        return booking.Id;
    }
}
