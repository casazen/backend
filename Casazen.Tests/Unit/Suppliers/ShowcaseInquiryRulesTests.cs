using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10b: the rules on what an anonymous customer sends to ask a supplier for something on measure. The one free-text field is the
/// words of the request: it is personal data and the first message of a conversation, so its bounds are checked here and the rest (the
/// shape of the name, the address and the phone) is the booking's, tested with it.
/// </summary>
public class ShowcaseInquiryRulesTests
{
    private const string Version = "2026-10";

    private static ShowcaseInquiryInput Valid() => new(
        Guid.NewGuid(),
        " pulizie-casa ",
        null,
        " Monza ",
        "  Vorrei far sistemare la ringhiera del balcone.\r\n\r\nGrazie.  ",
        " Mario  Rossi ",
        "mario.rossi@example.com",
        "+39 333 123 4567",
        "en-GB",
        true,
        Version,
        "203.0.113.7");

    [Fact]
    public void Normalize_TrimsEveryValue_KeepsTheLinesOfTheWords_AndTakesTheLanguageOfTheCustomer()
    {
        var content = ShowcaseInquiryRules.Normalize(Valid(), Version);

        Assert.Equal("pulizie-casa", content.ServiceSlug);
        Assert.Equal("Monza", content.City);
        Assert.Equal("Vorrei far sistemare la ringhiera del balcone.\n\nGrazie.", content.Message);
        Assert.Equal("Mario Rossi", content.FullName);
        Assert.Equal("+393331234567", content.Phone);
        Assert.Equal("en", content.Locale);
        Assert.Equal(Version, content.PrivacyNoticeVersion);
    }

    [Fact]
    public void Normalize_ARequestAboutNoService_IsValid()
    {
        var content = ShowcaseInquiryRules.Normalize(Valid() with { Service = "  " }, Version);

        Assert.Null(content.ServiceSlug);
    }

    [Theory]
    [InlineData("Ciao")]
    [InlineData("Buongiorn")]
    [InlineData("          ")]
    [InlineData("?!?!?!?!?!?!?!")]
    [InlineData(null)]
    public void Normalize_WordsTooShortOrThatAreNotWords_AreRefused(string? message)
    {
        var ex = Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseInquiryRules.Normalize(Valid() with { Message = message }, Version));

        Assert.Equal(new[] { "message" }, ex.Fields.ToArray());
    }

    [Fact]
    public void Normalize_WordsOfExactlyTheLimits_AreAccepted_OneCharacterMoreIsNot()
    {
        var shortest = new string('a', ShowcaseInquiryLimits.MessageMinLength);
        var longest = new string('b', ShowcaseInquiryLimits.MessageMaxLength);

        Assert.Equal(shortest, ShowcaseInquiryRules.Normalize(Valid() with { Message = shortest }, Version).Message);
        Assert.Equal(longest, ShowcaseInquiryRules.Normalize(Valid() with { Message = longest }, Version).Message);
        Assert.Throws<ShowcaseBookingRuleException>(
            () => ShowcaseInquiryRules.Normalize(Valid() with { Message = longest + "b" }, Version));
        Assert.Throws<ShowcaseBookingRuleException>(
            () => ShowcaseInquiryRules.Normalize(Valid() with { Message = shortest[1..] }, Version));
    }

    [Fact]
    public void Normalize_AControlCharacterInTheWords_IsRefused_ALineBreakIsNot()
    {
        Assert.Throws<ShowcaseBookingRuleException>(
            () => ShowcaseInquiryRules.Normalize(Valid() with { Message = "Una richiesta con un carattere" + (char)0 + " nascosto" }, Version));

        var withLines = ShowcaseInquiryRules.Normalize(Valid() with { Message = "Prima riga della richiesta\nSeconda riga\tcon tab" }, Version);
        Assert.Contains('\n', withLines.Message);
    }

    [Fact]
    public void Normalize_NamesEveryFieldAtFault_AtOnce()
    {
        var ex = Assert.Throws<ShowcaseBookingRuleException>(
            () => ShowcaseInquiryRules.Normalize(
                Valid() with { ClientRequestId = Guid.Empty, City = null, Message = "corto", FullName = "X", Email = "non-un-indirizzo", Phone = "12" },
                Version));

        Assert.Equal("supplier_booking_invalid", ex.Code);
        Assert.Equal(new[] { "clientRequestId", "city", "message", "fullName", "email", "phone" }, ex.Fields.ToArray());
    }

    [Fact]
    public void Normalize_WithoutTheConsent_OrWithAnotherVersionOfTheNotice_IsRefusedWithItsOwnCode()
    {
        var none = Assert.Throws<DomainRuleException>(() => ShowcaseInquiryRules.Normalize(Valid() with { PrivacyAccepted = false }, Version));
        var old = Assert.Throws<DomainRuleException>(() => ShowcaseInquiryRules.Normalize(Valid() with { PrivacyNoticeVersion = "2025-01" }, Version));

        Assert.Equal("supplier_booking_consent_required", none.Code);
        Assert.Equal("supplier_booking_consent_outdated", old.Code);
    }
}
