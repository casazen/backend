using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class GuestCheckInSendJobTests
{
    [Fact]
    public async Task ExecuteAsync_CheckedInBookingWithoutSession_SendsGuestLink()
    {
        await using var context = CreateContext();
        var (bookingId, orgId) = await SeedBookingAsync(context, BookingStatus.CheckedIn);
        var checkInService = new Mock<IGuestCheckInService>();
        checkInService
            .Setup(s => s.CreateSessionAsync(bookingId, orgId))
            .ReturnsAsync("guest-token");
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));

        var job = CreateJob(context, checkInService.Object, emailService.Object);

        await job.ExecuteAsync();

        checkInService.Verify(s => s.CreateSessionAsync(bookingId, orgId), Times.Once);
        emailService.Verify(s => s.SendEmailAsync(
            "anna@example.com",
            It.Is<string>(subject => subject.Contains("Completa il check-in")),
            It.Is<string>(html => html.Contains("https://public.example/check-in/guest-token"))),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CheckedInBookingWithSubmittedReport_DoesNotSendGuestLink()
    {
        await using var context = CreateContext();
        var (bookingId, orgId) = await SeedBookingAsync(context, BookingStatus.CheckedIn);
        context.AlloggiatiWebReports.Add(new AlloggiatiWebReport
        {
            BookingId = bookingId,
            GuestId = context.Bookings.Single(b => b.Id == bookingId).GuestId,
            Status = AlloggiatiWebStatus.Submitted,
        });
        await context.SaveChangesAsync();

        var checkInService = new Mock<IGuestCheckInService>();
        var emailService = new Mock<IEmailService>();
        var job = CreateJob(context, checkInService.Object, emailService.Object);

        await job.ExecuteAsync();

        checkInService.Verify(s => s.CreateSessionAsync(It.IsAny<Guid>(), orgId), Times.Never);
        emailService.Verify(s => s.SendEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()),
            Times.Never);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static GuestCheckInSendJob CreateJob(
        AppDbContext context,
        IGuestCheckInService checkInService,
        IEmailService emailService)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PublicSiteBaseUrl"] = "https://public.example",
                ["CheckIn:SendWindowDays"] = "3",
            })
            .Build();

        return new GuestCheckInSendJob(
            context,
            checkInService,
            emailService,
            configuration,
            Mock.Of<ILogger<GuestCheckInSendJob>>());
    }

    private static async Task<(Guid BookingId, Guid OrgId)> SeedBookingAsync(
        AppDbContext context,
        BookingStatus status)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        context.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Test Org",
            Slug = $"org-{orgId:N}",
            DisplayName = "Test Org",
            ContactEmail = "host@example.com",
        });

        context.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 4,
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
            CheckInDate = DateTime.UtcNow.Date,
            CheckOutDate = DateTime.UtcNow.Date.AddDays(2),
            Status = status,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 100m,
        });

        await context.SaveChangesAsync();
        return (bookingId, orgId);
    }
}
