using System.Globalization;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E23/F01/US01/T01 (NF-122): the one message that carries the check-in link in the chat of the portal, in the five languages of the guest page. The days are formatted from a real
/// value (a <c>DateOnly</c> of Europe/Rome becomes a <c>DateTime</c> before it is formatted: a date-time format on a <c>DateOnly</c> only fails when it runs), the instants are
/// written as the day they fall on in Rome, and nothing in the text names a portal, a supplier or an assistant.
/// </summary>
public class GuestCheckInChatMessageTests
{
    // Monday 12 October 2026 at 02:00 in Rome; and an expiry at 22:30 UTC on the 16th, which is already Saturday the 17th in Rome.
    private static readonly DateTime CheckIn = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Expires = new(2026, 10, 16, 22, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("it", "Buongiorno!", "ottobre")]
    [InlineData("en", "Hello!", "October")]
    [InlineData("de", "Guten Tag!", "Oktober")]
    [InlineData("fr", "Bonjour !", "octobre")]
    [InlineData("es", "¡Hola!", "octubre")]
    public void TheMessage_IsInTheLanguageOfTheGuest_WithTheStayTheLinkAndTheDaysOfRome(string language, string greeting, string month)
    {
        var text = EmailTemplates.GuestCheckInChatMessage(GuestLanguages.CultureOf(language), "Trullo", CheckIn, "https://public.test/checkin/ABC", Expires);

        Assert.StartsWith(greeting, text, StringComparison.Ordinal);
        Assert.Contains("Trullo", text, StringComparison.Ordinal);
        Assert.Contains("https://public.test/checkin/ABC", text, StringComparison.Ordinal);
        Assert.Contains($"12", text, StringComparison.Ordinal);
        Assert.Contains($"{month}", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026", text, StringComparison.Ordinal);

        // The expiry is the day it falls on in Rome (the 17th), never the day of the instant in UTC (the 16th).
        Assert.Contains("17", text, StringComparison.Ordinal);
        Assert.DoesNotContain("16", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.InRange(text.Length, 100, 600);
        foreach (var forbidden in new[] { "octorate", "airbnb", "booking.com", "assistant", "assistente" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDayOfTheStay_IsTheDayInRome_AtTheEdgeOfTheDay()
    {
        // 23:30 UTC on the 11th is already the 12th in Rome (CEST): the guest reads the 12th.
        var late = new DateTime(2026, 10, 11, 23, 30, 0, DateTimeKind.Utc);

        var text = EmailTemplates.GuestCheckInChatMessage(CultureInfo.GetCultureInfo("it-IT"), "Trullo", late, "https://public.test/checkin/ABC", Expires);

        Assert.Contains("12 ottobre 2026", text, StringComparison.Ordinal);
    }

    /// <summary>The language of a guest by the country on the booking: Italian for Italy, German for Germany and Austria, French, Spanish, English for the others, none when unknown.</summary>
    [Theory]
    [InlineData("IT", "it")]
    [InlineData("ita", "it")]
    [InlineData(" Italy ", "it")]
    [InlineData("DE", "de")]
    [InlineData("AT", "de")]
    [InlineData("France", "fr")]
    [InlineData("ES", "es")]
    [InlineData("España", "es")]
    [InlineData("JP", "en")]
    [InlineData("CH", "en")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void OfCountry_GivesTheLanguageOfTheGuest_OrNoneWhenTheBookingSaysNoCountry(string? country, string? expected)
    {
        Assert.Equal(expected, GuestLanguages.OfCountry(country));
        if (expected is not null)
            Assert.Contains(expected, GuestLanguages.Supported);
    }
}
