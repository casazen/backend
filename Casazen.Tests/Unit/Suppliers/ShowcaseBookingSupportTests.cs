using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-10: the codes and the texts (Italian and English) of the booking from a supplier's public showcase.</summary>
public class ShowcaseBookingSupportTests
{
    [Fact]
    public void MessageKeys_EveryKeyOfTheBooking_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        var keys = ShowcaseBookingErrors.MessageKeys.Append(ShowcaseBookingErrors.RateLimitedMessageKey).Append("SupplierBookingTokenRequired");
        Assert.All(keys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Fact]
    public void MessageKeys_AreUnique_OneForEveryRefusalOfTheBooking()
    {
        Assert.Equal(ShowcaseBookingErrors.MessageKeys.Count, ShowcaseBookingErrors.MessageKeys.Distinct().Count());
        Assert.Equal(8, ShowcaseBookingErrors.MessageKeys.Count);
    }

    [Fact]
    public void MessageKeys_TheInvalidBookingNamesItsFields()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.Contains("{0}", italian["SupplierBookingInvalid"]);
        Assert.Contains("{0}", english["SupplierBookingInvalid"]);
    }

    [Fact]
    public void Codes_AreUniqueSnakeCase_AndTheOnesTheFrontendBranchesOnArePresent()
    {
        var codes = typeof(ShowcaseBookingErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) } && !f.Name.EndsWith("MessageKey", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^supplier_booking(_[a-z]+)+$", code));
        foreach (var expected in new[]
                 {
                     "supplier_booking_invalid", "supplier_booking_offline", "supplier_booking_outside_zone",
                     "supplier_booking_consent_required", "supplier_booking_consent_outdated", "supplier_booking_link_invalid",
                     "supplier_booking_link_expired", "supplier_booking_supplier_unavailable",
                 })
        {
            Assert.Contains(expected, codes);
        }
    }

    [Fact]
    public void Fields_AreTheCamelCaseNamesOfTheRequestBody()
    {
        var fields = typeof(ShowcaseBookingFields)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(fields.Count, fields.Distinct().Count());
        Assert.All(fields, field => Assert.Matches("^[a-z][A-Za-z]*$", field));
        Assert.Contains("clientRequestId", fields);
        Assert.Contains("startUtc", fields);
        Assert.Contains("email", fields);
    }

    [Fact]
    public void Errors_TheUnhappyAnswersAreOfTheRightKind()
    {
        Assert.IsType<Casazen.Core.Exceptions.DomainRuleException>(ShowcaseBookingErrors.OfflineSupplier());
        Assert.IsType<Casazen.Core.Exceptions.DomainRuleException>(ShowcaseBookingErrors.OutsideSupplierZone());
        Assert.IsType<Casazen.Core.Exceptions.DomainRuleException>(ShowcaseBookingErrors.NoConsent());
        Assert.IsType<Casazen.Core.Exceptions.DomainRuleException>(ShowcaseBookingErrors.OldConsent());
        Assert.IsType<Casazen.Core.Exceptions.DomainRuleException>(ShowcaseBookingErrors.InactiveSupplier());
        Assert.IsType<Casazen.Core.Exceptions.NotFoundException>(ShowcaseBookingErrors.InvalidLink());
        Assert.IsType<Casazen.Core.Exceptions.DomainConflictException>(ShowcaseBookingErrors.ExpiredLink());
        Assert.IsType<ShowcaseBookingRuleException>(ShowcaseBookingErrors.InvalidFields(["email"]));
    }

    [Fact]
    public void Limits_TheTokenOfTheLink_FitsInTheBodyAndInTheCheck()
    {
        // A token is 43 characters (32 bytes in base64url); the limit leaves room and stops an oversized value.
        Assert.True(ShowcaseBookingLimits.TokenMaxLength >= 43);
        Assert.True(ShowcaseBookingLimits.TokenMaxLength <= 256);
        Assert.Equal(43, ShowcaseBookingTokens.New().Length);
        Assert.Equal(3, ShowcaseBookingLimits.MaxUnverifiedHoldsPerEmail);
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No SharedResources for {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }
}
