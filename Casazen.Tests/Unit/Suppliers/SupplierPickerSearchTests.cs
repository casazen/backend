using Casazen.Web.DTOs.Supplier;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-REL: the order of the list a host chooses a supplier from. By default it is the ranking (the score of
/// <c>SupplierRanking</c>, never sent), ties go to the name, and the first supplier is marked recommended only when it is strictly ahead.
/// The other orders of the marketplace (name, price, response time) are unchanged and mark nobody.
/// </summary>
public class SupplierPickerSearchTests
{
    private static readonly Guid Alfa = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Beta = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Gamma = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static List<SupplierPickerDto> Suppliers() =>
    [
        new() { OrgId = Gamma, LegalName = "Gamma Srl", PriceFromCents = 3000, MedianResponseMinutes = 10 },
        new() { OrgId = Alfa, LegalName = "Alfa Srl", PriceFromCents = 5000, MedianResponseMinutes = 90 },
        new() { OrgId = Beta, LegalName = "Beta Srl", PriceFromCents = 4000, MedianResponseMinutes = 30 },
    ];

    private static string[] Names(IEnumerable<SupplierPickerDto> suppliers) => suppliers.Select(s => s.LegalName).ToArray();

    [Theory]
    [InlineData(null, SupplierPickerSort.Recommended)]
    [InlineData("", SupplierPickerSort.Recommended)]
    [InlineData("  ", SupplierPickerSort.Recommended)]
    [InlineData("recommended", SupplierPickerSort.Recommended)]
    [InlineData(" Recommended ", SupplierPickerSort.Recommended)]
    [InlineData("name", SupplierPickerSort.Name)]
    [InlineData("PRICE", SupplierPickerSort.Price)]
    [InlineData("response", SupplierPickerSort.Response)]
    public void TryParseSort_ReadsTheFourOrders_AndBlankIsTheRanking(string? value, SupplierPickerSort expected)
    {
        Assert.True(SupplierPickerSearch.TryParseSort(value, out var sort));
        Assert.Equal(expected, sort);
    }

    [Theory]
    [InlineData("rating")]
    [InlineData("score")]
    public void TryParseSort_AnythingElse_IsRefused(string value)
    {
        Assert.False(SupplierPickerSearch.TryParseSort(value, out _));
    }

    [Fact]
    public void Apply_Recommended_OrdersByScore_AndATieGoesToTheName()
    {
        var scores = new Dictionary<Guid, int> { [Alfa] = 70, [Beta] = 80, [Gamma] = 80 };

        var ordered = SupplierPickerSearch.Apply(Suppliers(), q: null, SupplierPickerSort.Recommended, services: null, scores);

        Assert.Equal(["Beta Srl", "Gamma Srl", "Alfa Srl"], Names(ordered));
        // Beta and Gamma tie at the top: nobody is "the" recommended one.
        Assert.DoesNotContain(ordered, s => s.Recommended);
    }

    [Fact]
    public void Apply_Recommended_MarksTheFirstSupplier_OnlyWhenItIsStrictlyAheadOfTheNext()
    {
        var scores = new Dictionary<Guid, int> { [Alfa] = 70, [Beta] = 85, [Gamma] = 80 };

        var ordered = SupplierPickerSearch.Apply(Suppliers(), q: null, SupplierPickerSort.Recommended, services: null, scores);

        Assert.Equal(["Beta Srl", "Gamma Srl", "Alfa Srl"], Names(ordered));
        Assert.Equal([true, false, false], ordered.Select(s => s.Recommended).ToArray());
    }

    [Fact]
    public void Apply_Recommended_ASingleSupplier_HasNobodyToBeRecommendedOver()
    {
        var only = new List<SupplierPickerDto> { new() { OrgId = Alfa, LegalName = "Alfa Srl" } };

        var ordered = SupplierPickerSearch.Apply(only, q: null, SupplierPickerSort.Recommended, services: null, new Dictionary<Guid, int> { [Alfa] = 90 });

        Assert.False(Assert.Single(ordered).Recommended);
    }

    [Fact]
    public void Apply_Recommended_WithoutScores_IsTheOrderByName_AndMarksNobody()
    {
        var ordered = SupplierPickerSearch.Apply(Suppliers(), q: null, SupplierPickerSort.Recommended);

        Assert.Equal(["Alfa Srl", "Beta Srl", "Gamma Srl"], Names(ordered));
        Assert.DoesNotContain(ordered, s => s.Recommended);
    }

    [Fact]
    public void Apply_Recommended_ASupplierWithoutAScore_CountsAsZero()
    {
        var scores = new Dictionary<Guid, int> { [Gamma] = 60 };

        var ordered = SupplierPickerSearch.Apply(Suppliers(), q: null, SupplierPickerSort.Recommended, services: null, scores);

        Assert.Equal(["Gamma Srl", "Alfa Srl", "Beta Srl"], Names(ordered));
        Assert.True(ordered[0].Recommended);
    }

    [Fact]
    public void Apply_TheOtherOrders_AreUnchangedAndMarkNobody_WhateverTheScores()
    {
        var scores = new Dictionary<Guid, int> { [Alfa] = 10, [Beta] = 50, [Gamma] = 99 };

        Assert.Equal(["Alfa Srl", "Beta Srl", "Gamma Srl"], Names(SupplierPickerSearch.Apply(Suppliers(), null, SupplierPickerSort.Name, null, scores)));
        Assert.Equal(["Gamma Srl", "Beta Srl", "Alfa Srl"], Names(SupplierPickerSearch.Apply(Suppliers(), null, SupplierPickerSort.Price, null, scores)));
        Assert.Equal(["Gamma Srl", "Beta Srl", "Alfa Srl"], Names(SupplierPickerSearch.Apply(Suppliers(), null, SupplierPickerSort.Response, null, scores)));
        Assert.All(
            new[] { SupplierPickerSort.Name, SupplierPickerSort.Price, SupplierPickerSort.Response },
            sort => Assert.DoesNotContain(SupplierPickerSearch.Apply(Suppliers(), null, sort, null, scores), s => s.Recommended));
    }

    [Fact]
    public void Apply_TheSearchKeepsTheRankingOfWhatItFinds()
    {
        var scores = new Dictionary<Guid, int> { [Alfa] = 70, [Beta] = 85, [Gamma] = 80 };

        var found = SupplierPickerSearch.Apply(Suppliers(), q: "srl", SupplierPickerSort.Recommended, services: null, scores);
        var narrowed = SupplierPickerSearch.Apply(Suppliers(), q: "alfa", SupplierPickerSort.Recommended, services: null, scores);

        Assert.Equal(["Beta Srl", "Gamma Srl", "Alfa Srl"], Names(found));
        Assert.Equal(["Alfa Srl"], Names(narrowed));
        Assert.False(Assert.Single(narrowed).Recommended);
    }
}
