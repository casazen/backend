using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class AlloggiatiDeadlineAlertJobTests
{
    [Fact]
    public async Task AC6_FlagsIncompleteBookingWithin24Hours()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AppDbContext(options);
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        context.Orgs.Add(new Org
        {
            Id = orgId,
            Name = "Test Org",
            Slug = $"org-{orgId:N}",
            DisplayName = "Test Org",
            ContactEmail = "test@example.com",
        });

        context.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 4,
            CinCode = "IT-TEST",
        });

        context.Guests.Add(new Guest
        {
            Id = guestId,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = "anna@example.com",
        });

        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            OrgId = orgId,
            GuestId = guestId,
            CheckInDate = DateTime.UtcNow.AddHours(12),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
        });

        await context.SaveChangesAsync();

        var notificationMock = new Mock<INotificationService>();
        var alloggiatiMock = new Mock<IAlloggiatiWebService>();
        alloggiatiMock.Setup(s => s.ValidateGuestDataAsync(guestId)).ReturnsAsync(false);
        alloggiatiMock
            .Setup(s => s.IsOverdue(It.IsAny<DateTime>(), false, It.IsAny<AlloggiatiWebStatus?>()))
            .Returns(false);

        var job = new AlloggiatiDeadlineAlertJob(
            context,
            alloggiatiMock.Object,
            notificationMock.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<AlloggiatiDeadlineAlertJob>>());

        await job.ExecuteAsync();

        notificationMock.Verify(n => n.SendAlloggiatiDeadlineAlertAsync(bookingId), Times.Once);
    }
}
