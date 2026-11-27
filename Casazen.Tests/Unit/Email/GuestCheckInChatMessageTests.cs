using System.Globalization;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

public class GuestCheckInChatMessageTests
{
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
        var late = new DateTime(2026, 10, 11, 23, 30, 0, DateTimeKind.Utc);

        var text = EmailTemplates.GuestCheckInChatMessage(CultureInfo.GetCultureInfo("it-IT"), "Trullo", late, "https://public.test/checkin/ABC", Expires);

        Assert.Contains("12 ottobre 2026", text, StringComparison.Ordinal);
    }

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
