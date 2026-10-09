using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

/// <summary>
/// DB-03: the limits of the minimum stay and of the weekend surcharge are written once (<see cref="PropertyStayRules"/>) and
/// every place that bounds them (entity, create and update forms, CHECK constraints) must say the same thing.
/// </summary>
public class PropertyStayRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(30, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(31, false)]
    public void IsValidMinNights_NoMinimumOrOneToThirtyNights(int? minNights, bool expected)
    {
        Assert.Equal(expected, PropertyStayRules.IsValidMinNights(minNights));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("15", true)]
    [InlineData("12.5", true)]
    [InlineData("100", true)]
    [InlineData("-0.01", false)]
    [InlineData("100.01", false)]
    public void IsValidWeekendSurcharge_ZeroToOneHundredPercent(string percent, bool expected)
    {
        Assert.Equal(expected, PropertyStayRules.IsValidWeekendSurcharge(decimal.Parse(percent, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("12.345", "12.35")]
    [InlineData("12.344", "12.34")]
    [InlineData("0.005", "0.01")]
    [InlineData("15", "15")]
    public void NormalizeWeekendSurcharge_KeepsTwoDecimalsHalfAwayFromZero(string input, string expected)
    {
        var normalized = PropertyStayRules.NormalizeWeekendSurcharge(decimal.Parse(input, CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), normalized);
    }

    [Fact]
    public void MinNights_EveryPlaceThatBoundsIt_UsesTheSameLimits()
    {
        foreach (var (type, name) in new[]
        {
            (typeof(Property), nameof(Property.MinNights)),
            (typeof(CreatePropertyRequest), nameof(CreatePropertyRequest.MinNights)),
            (typeof(UpdatePropertyRequest), nameof(UpdatePropertyRequest.MinNights)),
        })
        {
            var range = RangeOf(type, name);
            Assert.Equal(PropertyStayRules.MinMinNights, Convert.ToInt32(range.Minimum, CultureInfo.InvariantCulture));
            Assert.Equal(PropertyStayRules.MaxMinNights, Convert.ToInt32(range.Maximum, CultureInfo.InvariantCulture));
            Assert.Equal("PropertyMinNightsRange", range.ErrorMessage);
        }
    }

    [Fact]
    public void WeekendSurchargePercent_EveryPlaceThatBoundsIt_UsesTheSameLimits()
    {
        foreach (var (type, name) in new[]
        {
            (typeof(Property), nameof(Property.WeekendSurchargePercent)),
            (typeof(CreatePropertyRequest), nameof(CreatePropertyRequest.WeekendSurchargePercent)),
            (typeof(UpdatePropertyRequest), nameof(UpdatePropertyRequest.WeekendSurchargePercent)),
        })
        {
            var range = RangeOf(type, name);
            Assert.Equal(0m, decimal.Parse(range.Minimum.ToString()!, CultureInfo.InvariantCulture));
            Assert.Equal(PropertyStayRules.MaxWeekendSurchargePercent, decimal.Parse(range.Maximum.ToString()!, CultureInfo.InvariantCulture));
            Assert.Equal("PropertyWeekendSurchargeRange", range.ErrorMessage);
        }
    }

    [Fact]
    public void MinimumStay_CannotExceedTheLongestShortTermLet()
    {
        // 30 nights: the longest "locazione breve"; a longer minimum would make the property unbookable by the guests.
        Assert.Equal(30, PropertyStayRules.MaxMinNights);
        Assert.Equal(1, PropertyStayRules.MinMinNights);
    }

    private static RangeAttribute RangeOf(Type type, string property) =>
        type.GetProperty(property)!.GetCustomAttribute<RangeAttribute>()
        ?? throw new InvalidOperationException($"{type.Name}.{property} has no [Range].");
}
