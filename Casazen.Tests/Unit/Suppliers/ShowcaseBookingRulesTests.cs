using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10: the rules on the values of a booking from a supplier's showcase. Pure functions: which fields are required, how long
/// they can be, what shape they have, the consent with the version of the notice, and the name shown before the take (D9).
/// </summary>
public class ShowcaseBookingRulesTests
{
    private const string Version = "2026-11-test";

    private static readonly DateTime Start = new(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc);

    private static ShowcaseBookingInput Valid() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "pulizia-appartamento",
        Start,
        null,
        null,
        null,
        "015146",
        "Milano",
        "20121",
        "Via Roma 1",
        "Piano 3, interno 7",
        "Citofono Rossi",
        "Mario Rossi",
        "mario.rossi@example.com",
        "+39 333 123 4567",
        "it",
        true,
        Version,
        "203.0.113.7");

    [Fact]
    public void Normalize_AValidBooking_IsTrimmedAndKeptWhole()
    {
        var content = ShowcaseBookingRules.Normalize(
            Valid() with { FullName = "  Mario   Rossi ", Email = " mario.rossi@example.com ", Address = " Via Roma 1 " }, Version);

        Assert.Equal("Mario Rossi", content.FullName);
        Assert.Equal("mario.rossi@example.com", content.Email);
        Assert.Equal("Via Roma 1", content.Address);
        Assert.Equal("pulizia-appartamento", content.ServiceSlug);
        Assert.Equal(Start, content.StartUtc);
        Assert.Equal("015146", content.ComuneIstat);
        Assert.Equal("Milano", content.City);
        Assert.Equal("20121", content.PostalCode);
        Assert.Equal("Piano 3, interno 7", content.Floor);
        Assert.Equal("Citofono Rossi", content.AccessNotes);
        Assert.Equal(Version, content.PrivacyNoticeVersion);
        Assert.Equal("203.0.113.7", content.ConsentIp);
    }

    [Fact]
    public void Normalize_TheEstimateChoices_AreHandedOnAsTheyAre_WithTheComuneToCheckAndThePostalCode()
    {
        var options = new List<SupplierQuoteOption?> { new("bagno", 2) };

        var content = ShowcaseBookingRules.Normalize(Valid() with { Quantity = 3, SurfaceSqm = 80, Options = options }, Version);

        Assert.Equal(3, content.Quote.Quantity);
        Assert.Equal(80, content.Quote.SurfaceSqm);
        Assert.Same(options, content.Quote.Options);
        // The code of the comune is what is checked against the supplier's zones; the postal code is echoed.
        Assert.Equal("015146", content.Quote.Comune);
        Assert.Equal("20121", content.Quote.PostalCode);
    }

    [Fact]
    public void Normalize_WithoutAnIstatCode_TheComuneToCheckIsTheNameWritten()
    {
        var content = ShowcaseBookingRules.Normalize(Valid() with { ComuneIstat = "  ", City = "Monza" }, Version);

        Assert.Null(content.ComuneIstat);
        Assert.Equal("Monza", content.Quote.Comune);
    }

    [Fact]
    public void Normalize_TheOptionalFields_CanBeLeftOut()
    {
        var content = ShowcaseBookingRules.Normalize(Valid() with { Floor = null, AccessNotes = " \n ", ComuneIstat = null }, Version);

        Assert.Null(content.Floor);
        Assert.Null(content.AccessNotes);
        Assert.Null(content.ComuneIstat);
    }

    [Fact]
    public void Normalize_EverythingThatIsNotValid_IsRefusedTogether_WithTheNamesOfTheFields()
    {
        var input = Valid() with
        {
            ClientRequestId = Guid.Empty,
            Service = " ",
            StartUtc = null,
            ComuneIstat = "12",
            City = "",
            PostalCode = "2012",
            Address = " ",
            FullName = "M",
            Email = "mario",
            Phone = "abc",
        };

        var ex = Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(input, Version));

        Assert.Equal(ShowcaseBookingErrors.Invalid, ex.Code);
        Assert.Equal(
            new[] { "clientRequestId", "service", "startUtc", "comuneIstat", "city", "postalCode", "address", "fullName", "email", "phone" },
            ex.Fields);
    }

    [Theory]
    [InlineData("mario@example.com", true)]
    [InlineData("Mario.Rossi+casazen@Example.IT", true)]
    [InlineData("mario@example", false)]
    [InlineData("mario@@example.com", false)]
    [InlineData("mario rossi@example.com", false)]
    [InlineData("Mario <mario@example.com>", false)]
    [InlineData("@example.com", false)]
    [InlineData("", false)]
    public void Normalize_Email_IsAnAddressAndNothingElse(string email, bool valid)
    {
        var input = Valid() with { Email = email };

        if (valid)
            Assert.Equal(email, ShowcaseBookingRules.Normalize(input, Version).Email);
        else
            Assert.Equal(["email"], Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(input, Version)).Fields);
    }

    [Theory]
    [InlineData("+39 333 123 4567", "+393331234567")]
    [InlineData("333.123.4567", "3331234567")]
    [InlineData("(06) 5550199", "065550199")]
    [InlineData("06/5550199", "065550199")]
    public void Normalize_Phone_KeepsTheDigitsAndTheLeadingPlus(string phone, string expected) =>
        Assert.Equal(expected, ShowcaseBookingRules.Normalize(Valid() with { Phone = phone }, Version).Phone);

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567890123456")]
    [InlineData("333 123 456 7 x")]
    [InlineData("33+3123456")]
    [InlineData("")]
    public void Normalize_Phone_NotANumber_IsRefused(string phone) =>
        Assert.Equal(["phone"], Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(Valid() with { Phone = phone }, Version)).Fields);

    [Theory]
    [InlineData("2012")]
    [InlineData("201211")]
    [InlineData("2O121")]
    [InlineData("")]
    public void Normalize_PostalCode_IsFiveDigits(string postalCode) =>
        Assert.Equal(["postalCode"], Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(Valid() with { PostalCode = postalCode }, Version)).Fields);

    [Fact]
    public void Normalize_TheLimitsOfTheFields_AreEnforced()
    {
        var input = Valid() with
        {
            FullName = new string('a', ShowcaseBookingLimits.FullNameMaxLength + 1),
            City = new string('a', ShowcaseBookingLimits.CityMaxLength + 1),
            Address = new string('a', ShowcaseBookingLimits.AddressMaxLength + 1),
            Floor = new string('a', ShowcaseBookingLimits.FloorMaxLength + 1),
            AccessNotes = new string('a', ShowcaseBookingLimits.AccessNotesMaxLength + 1),
        };

        var ex = Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(input, Version));

        Assert.Equal(new[] { "city", "address", "floor", "accessNotes", "fullName" }, ex.Fields);
    }

    [Fact]
    public void Normalize_ControlCharacters_AreRefused_ButANoteMayHaveLines()
    {
        var withLines = ShowcaseBookingRules.Normalize(Valid() with { AccessNotes = "Citofono Rossi\r\nChiavi nella cassetta" }, Version);
        Assert.Equal("Citofono Rossi\nChiavi nella cassetta", withLines.AccessNotes);

        var input = Valid() with { Address = "Via Roma\u0000 1", FullName = "Mario\u0007 Rossi", AccessNotes = "x\u001b[0m" };
        Assert.Equal(
            new[] { "address", "accessNotes", "fullName" },
            Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(input, Version)).Fields);
    }

    [Fact]
    public void Normalize_AName_NeedsALetter()
    {
        Assert.Equal(
            ["fullName"],
            Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(Valid() with { FullName = "12 34" }, Version)).Fields);
    }

    [Theory]
    [InlineData("2026-10-13T07:00:30Z")]
    [InlineData("2026-10-13T07:00:00.5Z")]
    public void Normalize_StartUtc_IsOnAWholeMinute_LikeTheSlotsOfThePlanner(string start)
    {
        var input = Valid() with { StartUtc = DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal) };

        Assert.Equal(["startUtc"], Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingRules.Normalize(input, Version)).Fields);
    }

    [Fact]
    public void Normalize_StartUtc_AnUnspecifiedTime_IsTakenAsUtc()
    {
        var local = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Unspecified);

        // The API always sends instants; an unspecified time is taken as UTC (UtcDateTime.Normalize).
        Assert.Equal(new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc), ShowcaseBookingRules.Normalize(Valid() with { StartUtc = local }, Version).StartUtc);
    }

    [Fact]
    public void Normalize_NoConsent_IsAnErrorOfItsOwn()
    {
        var ex = Assert.Throws<DomainRuleException>(() => ShowcaseBookingRules.Normalize(Valid() with { PrivacyAccepted = false }, Version));

        Assert.Equal(ShowcaseBookingErrors.ConsentRequired, ex.Code);
        Assert.Equal("SupplierBookingConsentRequired", ex.MessageKey);
    }

    [Theory]
    [InlineData("2026-06-old")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2026-11-TEST")]
    public void Normalize_AnotherVersionOfTheNotice_IsOutdated(string? version)
    {
        var ex = Assert.Throws<DomainRuleException>(() => ShowcaseBookingRules.Normalize(Valid() with { PrivacyNoticeVersion = version }, Version));

        Assert.Equal(ShowcaseBookingErrors.ConsentOutdated, ex.Code);
        Assert.Equal("SupplierBookingConsentOutdated", ex.MessageKey);
    }

    [Fact]
    public void Normalize_TheFieldsAreCheckedBeforeTheConsent()
    {
        var ex = Assert.Throws<ShowcaseBookingRuleException>(
            () => ShowcaseBookingRules.Normalize(Valid() with { Email = "no", PrivacyAccepted = false }, Version));

        Assert.Equal(["email"], ex.Fields);
    }

    [Theory]
    [InlineData(null, "it")]
    [InlineData("", "it")]
    [InlineData("it", "it")]
    [InlineData("IT", "it")]
    [InlineData("en", "en")]
    [InlineData("EN-gb", "en")]
    [InlineData("en_US", "en")]
    [InlineData("fr", "it")]
    [InlineData("english", "it")]
    [InlineData("e", "it")]
    public void Locale_IsItalianOrEnglish_AndItalianForAnythingElse(string? value, string expected) =>
        Assert.Equal(expected, ServiceCustomerLocales.Normalize(value));

    [Fact]
    public void Normalize_TheConsentAddress_IsKeptShortEnoughForItsColumn()
    {
        var content = ShowcaseBookingRules.Normalize(Valid() with { ConsentIp = new string('1', 100) }, Version);

        Assert.Equal(ShowcaseBookingLimits.ConsentIpMaxLength, content.ConsentIp.Length);
        Assert.Equal(string.Empty, ShowcaseBookingRules.Normalize(Valid() with { ConsentIp = null }, Version).ConsentIp);
    }

    [Theory]
    [InlineData("Mario Rossi", "Mario R.")]
    [InlineData("  mario   rossi ", "mario R.")]
    [InlineData("Maria Grazia De Luca", "Maria L.")]
    [InlineData("Madonna", "Madonna")]
    [InlineData("Åsa Ödegård", "Åsa Ö.")]
    [InlineData("Anna 3", "Anna")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void AbbreviateName_IsTheFirstNameAndTheInitialOfTheLastOne(string? fullName, string expected) =>
        Assert.Equal(expected, ShowcaseBookingRules.AbbreviateName(fullName));

    [Theory]
    [InlineData("Mario@Example.IT", "mario@example.it")]
    [InlineData("  mario@example.it ", "mario@example.it")]
    public void NormalizeEmail_IsTrimmedAndLowercase(string email, string expected) =>
        Assert.Equal(expected, ShowcaseBookingRules.NormalizeEmail(email));
}
