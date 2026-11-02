using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// <c>DELETE api/guest/account</c> on real PostgreSQL with the real <c>UserRepository</c> (its <c>ExecuteDelete</c> of the push devices does
/// not exist on EF InMemory): the account goes with its devices and its notifications (the foreign keys cascade), the bookings and the guest
/// records of the hosts stay exactly as they were, and a refusal of Auth0 removes nothing. The rest of the endpoint is proved by
/// <see cref="GuestAccountIntegrationTests"/> on every provider.
/// </summary>
public class GuestAccountDeletionPostgresTests(GuestAccountDeletionPostgresTests.AuthFactory factory)
    : IClassFixture<GuestAccountDeletionPostgresTests.AuthFactory>
{
    private const string AccountPath = "/api/guest/account";

    [PostgresFact]
    public async Task Delete_GuestAccount_RemovesTheAccountItsDevicesAndItsNotifications_AndLeavesTheHostsBooksAlone()
    {
        var userId = $"auth0|guest-delete-{Guid.NewGuid():N}";
        var email = $"guest.delete.{Guid.NewGuid():N}@example.com";
        var (orgId, bookingId, guestId) = await SeedAsync(userId, email);
        factory.Auth0.Setup(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(Auth0SyncResult.Synced);
        using var client = factory.CreateAuthenticatedClient(userId, email: email, emailVerified: true);

        var response = await client.DeleteAsync(AccountPath);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        factory.Auth0.Verify(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == userId));
        Assert.False(await db.DeviceRegistrations.IgnoreQueryFilters().AnyAsync(d => d.UserId == userId));
        Assert.False(await db.InAppNotifications.IgnoreQueryFilters().AnyAsync(n => n.UserId == userId));
        // The hosts' books are not the account's: the booking, its payment and its guest record are as they were.
        var booking = await db.Bookings.IgnoreQueryFilters().Include(b => b.Guest).Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
        Assert.Equal(guestId, booking.GuestId);
        Assert.Equal(email, booking.Guest.Email);
        Assert.Equal("Anna", booking.Guest.FirstName);
        Assert.Null(booking.Guest.DataAnonymizedDate);
        Assert.Equal(362m, Assert.Single(booking.Payments).Amount);
        Assert.Equal(orgId, booking.OrgId);

        // The token already issued still verifies, the account is gone: 401, and Auth0 is not asked again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync(AccountPath)).StatusCode);
        factory.Auth0.Verify(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task Delete_Auth0Refuses_502_AndTheAccountItsDevicesAndItsNotificationsStay()
    {
        var userId = $"auth0|guest-delete-{Guid.NewGuid():N}";
        var email = $"guest.delete.{Guid.NewGuid():N}@example.com";
        await SeedAsync(userId, email);
        factory.Auth0
            .Setup(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.RateLimitedCode));
        using var client = factory.CreateAuthenticatedClient(userId, email: email, emailVerified: true);

        var response = await client.DeleteAsync(AccountPath);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            Auth0SyncResult.RateLimitedCode,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Id == userId));
        Assert.True(await db.DeviceRegistrations.IgnoreQueryFilters().AnyAsync(d => d.UserId == userId));
        Assert.True(await db.InAppNotifications.IgnoreQueryFilters().AnyAsync(n => n.UserId == userId));
    }

    /// <summary>
    /// A guest-only account (no role, no org), a push device and a notification of it, and a booking of the public checkout made with its
    /// address on a site of a host.
    /// </summary>
    private async Task<(Guid OrgId, Guid BookingId, Guid GuestId)> SeedAsync(string userId, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Villa Cancellazione",
            Slug = $"guest-delete-{Guid.NewGuid():N}",
            DisplayName = "Villa Cancellazione",
            ContactEmail = $"host.{Guid.NewGuid():N}@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = $"Casa {Guid.NewGuid():N}"[..20],
            Description = "Guest account deletion",
            Address = $"Via Prova {Guid.NewGuid():N}",
            City = "Como",
            PostalCode = "22100",
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 50m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Guest",
            Email = email,
            PhoneNumber = "+393331112222",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TouristTaxAmount = 12m,
            TotalPrice = 362m,
        };
        db.AddRange(org, property, guest, booking);
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            FirstName = "Anna",
            LastName = "Guest",
            Role = UserRole.None,
            IsActive = true,
        });
        db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = 362m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = $"pi_guest_delete_{Guid.NewGuid():N}",
            StripePaymentIntentId = $"pi_guest_delete_{Guid.NewGuid():N}",
            ProcessedAt = DateTime.UtcNow,
        });
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            UserId = userId,
            OrgId = org.Id,
            Platform = "ios",
            PushToken = $"ExponentPushToken[{Guid.NewGuid():N}]",
            DeviceId = $"device-{Guid.NewGuid():N}",
        });
        db.InAppNotifications.Add(new InAppNotification
        {
            OrgId = org.Id,
            UserId = userId,
            Type = "booking.new",
            DeliveryKey = $"booking:{booking.Id}:new",
        });
        await db.SaveChangesAsync();
        return (org.Id, booking.Id, guest.Id);
    }

    /// <summary>The integration host with Auth0 mocked: the deletion is its only call. The repository is the real one.</summary>
    public sealed class AuthFactory : CasazenWebApplicationFactory
    {
        public Mock<IAuth0ManagementService> Auth0 { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IAuth0ManagementService>(services);
                services.AddSingleton(Auth0.Object);
            });
        }
    }
}
