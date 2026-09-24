using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SU-03 (A4-05, A6-03): the single list of service category codes and its validation.</summary>
public class ServiceCategoriesTests
{
    [Fact]
    public void All_IsTheUnionOfTheCategoriesOfTheThreeClients()
    {
        // Host web: cleaning, maintenance, plumbing, laundry. App: cleaning, maintenance, linen, check-in.
        // Supplier wizard (Italian labels): Pulizie, Manutenzione, Giardinaggio, Eventi, Noleggio, Escursioni.
        Assert.Equal(
            new[] { "cleaning", "maintenance", "plumbing", "laundry", "linen", "check-in", "gardening", "events", "rental", "excursions" },
            ServiceCategories.All);
    }

    [Fact]
    public void All_CodesAreDistinctLowercaseAndTrimmed()
    {
        Assert.Equal(ServiceCategories.All.Count, ServiceCategories.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ServiceCategories.All, code => Assert.Equal(code.Trim().ToLowerInvariant(), code));
    }

    [Theory]
    [InlineData("cleaning", "cleaning")]
    [InlineData(" Cleaning ", "cleaning")]
    [InlineData("CHECK-IN", "check-in")]
    [InlineData("linen\t", "linen")]
    public void Require_KnownCode_ReturnsNormalizedCode(string value, string expected)
    {
        Assert.Equal(expected, ServiceCategories.Require(value));
    }

    [Theory]
    [InlineData("Pulizie")]
    [InlineData("Manutenzione")]
    [InlineData("checkin")]
    [InlineData("cleaning<script>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Require_ItalianLabelUnknownOrEmptyValue_ThrowsInvalidServiceCategory(string? value)
    {
        var ex = Assert.Throws<DomainRuleException>(() => ServiceCategories.Require(value));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        Assert.Equal(ServiceCategories.InvalidCategoryMessageKey, ex.MessageKey);
    }

    [Fact]
    public void Require_LongInvalidValue_EchoesAtMostFiftyCharacters()
    {
        var ex = Assert.Throws<DomainRuleException>(() => ServiceCategories.Require(new string('x', 500)));

        Assert.Equal(50, Assert.IsType<string>(Assert.Single(ex.MessageArgs)).Length);
    }

    [Fact]
    public void RequireAll_DuplicatesAndCaseVariants_ReturnsDistinctCodesInFirstOccurrenceOrder()
    {
        var codes = ServiceCategories.RequireAll(["maintenance", "Cleaning", "cleaning ", "maintenance"]);

        Assert.Equal(new[] { "maintenance", "cleaning" }, codes);
    }

    [Fact]
    public void RequireAll_OneInvalidValue_Throws()
    {
        var ex = Assert.Throws<DomainRuleException>(() => ServiceCategories.RequireAll(["cleaning", "Pulizie"]));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        Assert.Equal("Pulizie", Assert.Single(ex.MessageArgs));
    }

    [Theory]
    [InlineData("cleaning", true)]
    [InlineData("Cleaning", false)]
    [InlineData("Pulizie", false)]
    [InlineData(null, false)]
    public void IsKnown_ExactCodeOnly(string? value, bool expected)
    {
        Assert.Equal(expected, ServiceCategories.IsKnown(value));
    }
}
