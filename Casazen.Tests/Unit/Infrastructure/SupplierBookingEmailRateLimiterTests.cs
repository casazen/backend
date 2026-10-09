using Casazen.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-10: the second rate limit of the booking from a supplier's showcase, per e-mail address and supplier (3 an hour), next to the
/// per-IP one. One address cannot be used to hold many slots of one supplier from many IPs, nor to fill a mailbox with
/// verification e-mails; the key is a hash, never the address.
/// </summary>
public class SupplierBookingEmailRateLimiterTests
{
    private static SupplierBookingEmailRateLimiter Limiter(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void TryAcquire_ThreeAnHour_TheFourthWaits()
    {
        using var limiter = Limiter();

        for (var i = 0; i < 3; i++)
            Assert.True(limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out _));
        var fourth = limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out var retryAfter);

        Assert.False(fourth);
        Assert.InRange(retryAfter, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }

    [Fact]
    public void TryAcquire_TwoSpellingsOfOneAddressAndOfTheSlug_AreOnePartition()
    {
        using var limiter = Limiter();
        Assert.True(limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out _));
        Assert.True(limiter.TryAcquire("VETRINA-TEST", "Mario.Rossi@Example.COM", out _));
        Assert.True(limiter.TryAcquire(" vetrina-test ", "  mario.rossi@example.com ", out _));

        Assert.False(limiter.TryAcquire("vetrina-test", "MARIO.ROSSI@EXAMPLE.COM", out _));
    }

    [Fact]
    public void TryAcquire_AnotherAddress_OrAnotherSupplier_HasItsOwnQuota()
    {
        using var limiter = Limiter();
        for (var i = 0; i < 3; i++)
            limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out _);
        Assert.False(limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out _));

        Assert.True(limiter.TryAcquire("vetrina-test", "anna.verdi@example.com", out _));
        Assert.True(limiter.TryAcquire("altra-vetrina", "mario.rossi@example.com", out _));
    }

    [Fact]
    public void TryAcquire_TheLimitIsConfigurable()
    {
        using var limiter = Limiter(("RateLimiting:SupplierBookingCreatePerEmail:PermitLimit", "1"));

        Assert.True(limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out _));
        Assert.False(limiter.TryAcquire("vetrina-test", "mario.rossi@example.com", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TryAcquire_NullArguments_AreRefused()
    {
        using var limiter = Limiter();

        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!, "mario.rossi@example.com", out _));
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire("vetrina-test", null!, out _));
    }
}
