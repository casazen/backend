using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>BK-11 (A3-10): the readable booking code of "Le mie prenotazioni".</summary>
public class BookingCodesTests
{
    [Fact]
    public void New_ManyCodes_TenCharactersOfTheAlphabetAndNoRepeats()
    {
        var codes = Enumerable.Range(0, 5_000).Select(_ => BookingCodes.New()).ToList();

        Assert.All(codes, code => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{10}$", code));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        // Every character of the alphabet shows up: the code uses the whole alphabet, not a biased part of it.
        Assert.Equal(BookingCodes.Alphabet.Length, codes.SelectMany(c => c).Distinct().Count());
    }

    [Fact]
    public void NewBooking_Created_HasItsOwnCode()
    {
        var first = new Booking();
        var second = new Booking();

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{10}$", first.BookingCode);
        Assert.NotEqual(first.BookingCode, second.BookingCode);
    }

    [Fact]
    public void Format_StoredCode_TwoGroupsOfFive()
    {
        Assert.Equal("K7M4Q-9XP2H", BookingCodes.Format("K7M4Q9XP2H"));
    }

    [Theory]
    [InlineData("K7M4Q-9XP2H", "K7M4Q9XP2H")]
    [InlineData("  k7m4q 9xp2h ", "K7M4Q9XP2H")]
    [InlineData("K7M4Q–9XP2H", "K7M4Q9XP2H")]
    [InlineData("k7m4q-9xp2h", "K7M4Q9XP2H")]
    [InlineData("O1LI0-ABCDE", "01110ABCDE")]
    public void TryNormalize_WhatAGuestTypes_StoredForm(string input, string expected)
    {
        Assert.True(BookingCodes.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("K7M4Q-9XP2")]
    [InlineData("K7M4Q-9XP2HH")]
    [InlineData("K7M4Q-9XP2U")]
    [InlineData("K7M4Q-9XP2*")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    public void TryNormalize_NotACode_False(string? input)
    {
        Assert.False(BookingCodes.TryNormalize(input, out var code));
        Assert.Null(code);
    }
}
