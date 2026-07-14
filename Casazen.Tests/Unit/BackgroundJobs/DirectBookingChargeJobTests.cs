using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Stripe;
using Xunit;
using PropertyEntity = Casazen.Core.Entities.Property;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class DirectBookingChargeJobTests
{
    private const string DeadlineChargeDescription = "Direct checkout - deferred payment (charged at deadline)";
    private const string LegacyDeadlineChargeDescription = "Direct booking - charged at deadline";

    [Fact]
    public async Task ExecuteAsync_DeferredBookingAlreadyCharged_DoesNotChargeAgain()
    {
        await using var context = CreateContext();
        var (booking, org) = await SeedChargeableBookingAsync(context);
        var paymentRepository = new PaymentRepository(context);
        var stripeService = new Mock<IStripeService>();
        var orgService = new Mock<IOrgService>();
        orgService
            .Setup(s => s.GetByIdAsync(org.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);
        stripeService
            .Setup(s => s.ChargePaymentMethodAsync(
                org.StripeConnectedAccountId!,
                booking.StripeCustomerId!,
                booking.StripePaymentMethodId!,
                12345,
                "eur",
                It.IsAny<Dictionary<string, string>>(),
                $"direct-booking-deadline:{booking.Id}"))
            .ReturnsAsync(new PaymentIntent { Id = "pi_deadline_1" });

        var job = CreateJob(context, paymentRepository, stripeService.Object, orgService.Object);

        await job.ExecuteAsync();
        await job.ExecuteAsync();

        stripeService.Verify(s => s.ChargePaymentMethodAsync(
            org.StripeConnectedAccountId!,
            booking.StripeCustomerId!,
            booking.StripePaymentMethodId!,
            12345,
            "eur",
            It.Is<Dictionary<string, string>>(m =>
                m["bookingId"] == booking.Id.ToString() &&
                m["kind"] == "direct-booking-deadline-charge"),
            $"direct-booking-deadline:{booking.Id}"), Times.Once);

        var payments = await context.Payments
            .Where(p => p.BookingId == booking.Id)
            .ToListAsync();
        var payment = Assert.Single(payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal("pi_deadline_1", payment.TransactionId);
        Assert.Equal("pi_deadline_1", payment.StripePaymentIntentId);
        Assert.NotNull(payment.ProcessedAt);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyCompletedDeadlineChargeExists_SkipsBooking()
    {
        await using var context = CreateContext();
        var (booking, org) = await SeedChargeableBookingAsync(
            context,
            paymentStatus: PaymentStatus.Completed,
            paymentDescription: LegacyDeadlineChargeDescription,
            transactionId: "pi_existing_deadline");
        var paymentRepository = new PaymentRepository(context);
        var stripeService = new Mock<IStripeService>();
        var orgService = new Mock<IOrgService>();
        orgService
            .Setup(s => s.GetByIdAsync(org.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var job = CreateJob(context, paymentRepository, stripeService.Object, orgService.Object);

        await job.ExecuteAsync();

        stripeService.Verify(s => s.ChargePaymentMethodAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(),
            It.IsAny<string?>()), Times.Never);

        var payment = Assert.Single(await context.Payments.Where(p => p.BookingId == booking.Id).ToListAsync());
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal("pi_existing_deadline", payment.TransactionId);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static DirectBookingChargeJob CreateJob(
        AppDbContext context,
        IPaymentRepository paymentRepository,
        IStripeService stripeService,
        IOrgService orgService)
    {
        return new DirectBookingChargeJob(
            context,
            paymentRepository,
            stripeService,
            orgService,
            Mock.Of<ILogger<DirectBookingChargeJob>>());
    }

    private static async Task<(Booking Booking, OrgEntity Org)> SeedChargeableBookingAsync(
        AppDbContext context,
        PaymentStatus paymentStatus = PaymentStatus.Pending,
        string paymentDescription = DeadlineChargeDescription,
        string transactionId = "seti_pending")
    {
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "CasaZen Host",
            DisplayName = "CasaZen Host",
            Slug = $"host-{Guid.NewGuid():N}",
            StripeConnectedAccountId = "acct_123",
            ConnectChargesEnabled = true,
        };
        var property = new PropertyEntity
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            OwnerId = "owner-1",
            Name = "Apartment",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
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
            CheckInDate = DateTime.UtcNow.Date.AddDays(7),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(10),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            TotalPrice = 123.45m,
            PaymentOption = PaymentOption.OnCancellationDeadline,
            FreeRefundDeadline = DateTime.UtcNow.Date.AddDays(-1),
            StripeSetupIntentId = "seti_pending",
            StripeCustomerId = "cus_123",
            StripePaymentMethodId = "pm_123",
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            Booking = booking,
            OrgId = org.Id,
            Org = org,
            Amount = booking.TotalPrice,
            Status = paymentStatus,
            Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
            TransactionId = transactionId,
            StripePaymentIntentId = paymentStatus == PaymentStatus.Completed ? transactionId : null,
            Description = paymentDescription,
            ProcessedAt = paymentStatus == PaymentStatus.Completed ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        context.AddRange(org, property, guest, booking, payment);
        await context.SaveChangesAsync();
        return (booking, org);
    }
}
