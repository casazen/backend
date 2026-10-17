using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// DB-12a, the one security check of the reviews: the signed link of the invitation cannot be forged. Only an untouched, unexpired token made
/// by this key ring for this purpose opens a booking; anything else is refused the same way.
/// </summary>
public class ReviewLinkTokensTests
{
    [Fact]
    public void TryRead_OnlyAnUntouchedUnexpiredTokenOfThisKeyRingAndPurpose_OpensItsBooking()
    {
        var keys = new EphemeralDataProtectionProvider();
        var tokens = new ReviewLinkTokens(keys);
        var booking = Guid.NewGuid();
        var token = tokens.Create(booking, DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(tokens.TryRead(token, out var read));
        Assert.Equal(booking, read);

        // A single character changed (in the middle, where every bit counts), a token cut short, nothing at all, and the booking id bare.
        var middle = token.Length / 2;
        Assert.False(tokens.TryRead(token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..], out _));
        Assert.False(tokens.TryRead(token[..^6], out _));
        Assert.False(tokens.TryRead(string.Empty, out _));
        Assert.False(tokens.TryRead(booking.ToString("N"), out _));

        // The link of an invitation that is over, a token of another key ring (another installation) and one made for another purpose.
        Assert.False(tokens.TryRead(tokens.Create(booking, DateTimeOffset.UtcNow.AddMinutes(-1)), out _));
        Assert.False(tokens.TryRead(new ReviewLinkTokens(new EphemeralDataProtectionProvider()).Create(booking, DateTimeOffset.UtcNow.AddDays(1)), out _));
        var otherPurpose = keys.CreateProtector("Casazen.SomethingElse").ToTimeLimitedDataProtector()
            .Protect(booking.ToString("N"), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(tokens.TryRead(otherPurpose, out _));
    }
}
