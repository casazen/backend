using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Casazen.Infrastructure.Data;

namespace Casazen.Tests.Unit.Services;

public class AlloggiatiWebServiceTests
{
    [Fact]
    public async Task ReportGuest_WhenDisabled_MarksSubmittedWithSimulatedNote()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AppDbContext(options);
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        context.Guests.Add(new Guest
        {
            Id = guestId,
            FirstName = "Test",
            LastName = "Guest",
            Email = "test@example.com",
            DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PlaceOfBirth = "Roma",
            Nationality = "IT",
            DocumentType = GuestDocumentType.Passport,
            DocumentNumber = "X123",
            DocumentIssuingCountry = "IT",
            Gender = Gender.Male,
        });

        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            GuestId = guestId,
            CheckInDate = DateTime.UtcNow,
            CheckOutDate = DateTime.UtcNow.AddDays(2),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
        });

        await context.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Alloggiati:Enabled"] = "false" })
            .Build();

        var service = new AlloggiatiWebService(
            new GuestRepository(context),
            new AlloggiatiWebReportRepository(context),
            new BookingRepository(context),
            context,
            config,
            Mock.Of<ILogger<AlloggiatiWebService>>());

        await service.ReportGuestAsync(guestId, bookingId);

        var report = context.AlloggiatiWebReports.Single(r => r.BookingId == bookingId);
        Assert.Equal(AlloggiatiWebStatus.Submitted, report.Status);
        Assert.Equal("simulated", report.ErrorMessage);
    }

    [Fact]
    public void IsOverdue_WhenPastDeadlineAndIncomplete_ReturnsTrue()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new AppDbContext(options);
        var config = new ConfigurationBuilder().Build();
        var service = new AlloggiatiWebService(
            new GuestRepository(context),
            new AlloggiatiWebReportRepository(context),
            new BookingRepository(context),
            context,
            config,
            Mock.Of<ILogger<AlloggiatiWebService>>());

        var overdue = service.IsOverdue(
            DateTime.UtcNow.AddHours(-30),
            dataComplete: false,
            AlloggiatiWebStatus.Pending);

        Assert.True(overdue);
    }
}
