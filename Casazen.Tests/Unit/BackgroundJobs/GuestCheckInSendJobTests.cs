using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class GuestCheckInSendJobTests
{
    [Fact]
    public async Task ExecuteAsync_EmailSendFails_ExpiresCreatedSessionSoBookingCanRetry()
    {
        await using var db = CreateContext();
        var booking = await SeedBookingAsync(db);
        var checkInService = new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance);
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync(booking.Guest.Email, It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(false, "provider rejected request"));
        var job = CreateJob(db, checkInService, emailService.Object);

        await job.ExecuteAsync();
        await job.ExecuteAsync();

        emailService.Verify(
            s => s.SendEmailAsync(booking.Guest.Email, It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(2));

        var sessions = await db.GuestCheckInSessions
            .Where(s => s.BookingId == booking.Id)
            .ToListAsync();

        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(GuestCheckInSessionStatus.Scaduto, s.Status));
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static GuestCheckInSendJob CreateJob(
        AppDbContext db,
        IGuestCheckInService checkInService,
        IEmailService emailService)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PublicSiteBaseUrl"] = "https://public.test",
                ["CheckIn:SendWindowDays"] = "3",
            })
            .Build();

        return new GuestCheckInSendJob(
            db,
            checkInService,
            emailService,
            configuration,
            NullLogger<GuestCheckInSendJob>.Instance);
    }

    private static async Task<Booking> SeedBookingAsync(AppDbContext db)
    {
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "CasaZen",
            Slug = "casazen",
            DisplayName = "CasaZen",
            ContactEmail = "host@example.com",
        };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            OwnerId = "owner-test",
            Name = "Test Villa",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            IsActive = true,
        };
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            PropertyId = property.Id,
            Property = property,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(4),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        };

        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        return booking;
    }
}
