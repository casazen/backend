using Casazen.Core.OrgTeam;
using Xunit;

namespace Casazen.Tests.Unit.OrgTeam;

/// <summary>AM-02b: what a member can ask access to, and what its note becomes on the way to the email of the administrators.</summary>
public class OrgAccessRequestRulesTests
{
    [Fact]
    public void Areas_AreTheFourAreasAndThePagesOfAClosedList()
    {
        Assert.Equal(
            [
                "account", "short-rent", "long-rent", "supplier",
                "people", "properties", "suppliers", "billing", "organization", "activity", "integrations", "security",
                "payments", "reports", "prices",
            ],
            OrgAccessRequestRules.Areas);
    }

    [Fact]
    public void Areas_EveryOneIsAShortCode_SoItCanBeWrittenInTheActivityLog()
    {
        Assert.All(OrgAccessRequestRules.Areas, area => Assert.True(OrgActivityCatalog.IsSafeDetailValue(area), area));
    }

    [Theory]
    [InlineData("billing", "billing")]
    [InlineData(" Billing ", "billing")]
    [InlineData("SHORT-RENT", "short-rent")]
    public void TryNormalizeArea_ACodeOfTheList_IsCanonical(string value, string expected)
    {
        Assert.True(OrgAccessRequestRules.TryNormalizeArea(value, out var area));
        Assert.Equal(expected, area);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("billing,people")]
    [InlineData("short_rent")]
    [InlineData("Mario Rossi")]
    public void TryNormalizeArea_AnythingElse_IsRefused(string? value)
    {
        Assert.False(OrgAccessRequestRules.TryNormalizeArea(value, out _));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Mi serve per le prenotazioni", "Mi serve per le prenotazioni")]
    [InlineData("  due   spazi  ", "due spazi")]
    [InlineData("riga uno\r\nriga due\nriga tre\triga quattro", "riga uno riga due riga tre riga quattro")]
    [InlineData("a\u0000b\u0007c", "a b c")]
    [InlineData("accento è ù, emoji \U0001F600", "accento è ù, emoji \U0001F600")]
    public void NormalizeNote_BecomesOneLineWithNoControlOrInvisibleCharacter(string? note, string expected)
    {
        Assert.Equal(expected, OrgAccessRequestRules.NormalizeNote(note));
    }

    [Theory]
    [InlineData(0x200B)] // zero width space
    [InlineData(0x200E)] // left-to-right mark
    [InlineData(0x202E)] // right-to-left override: reverses what follows in a mail client
    [InlineData(0x202C)] // pop directional formatting
    [InlineData(0x2066)] // left-to-right isolate
    [InlineData(0x2028)] // line separator
    [InlineData(0x00A0)] // no-break space
    [InlineData(0xE000)] // private use
    public void NormalizeNote_AnInvisibleOrDirectionalCharacter_BecomesAPlainSpace(int codePoint)
    {
        var note = "uno" + char.ConvertFromUtf32(codePoint) + "due";

        Assert.Equal("uno due", OrgAccessRequestRules.NormalizeNote(note));
    }

    [Fact]
    public void NormalizeNote_ALongerNote_IsCutAtTheLimit_NeverInsideAnEmoji()
    {
        var text = new string('a', OrgAccessRequestRules.MaxNoteLength - 1) + "\U0001F600" + "tail";

        var note = OrgAccessRequestRules.NormalizeNote(text);

        Assert.True(note.Length <= OrgAccessRequestRules.MaxNoteLength);
        Assert.Equal(new string('a', OrgAccessRequestRules.MaxNoteLength - 1), note);
        Assert.DoesNotContain(note, char.IsSurrogate);
    }

    [Fact]
    public void NormalizeNote_ANoteOfExactlyTheLimit_IsKept()
    {
        var text = new string('x', OrgAccessRequestRules.MaxNoteLength);

        Assert.Equal(text, OrgAccessRequestRules.NormalizeNote(text));
    }

    [Fact]
    public void TheLimits_AreWhatTheRunbookSays()
    {
        Assert.Equal(200, OrgAccessRequestRules.MaxNoteLength);
        Assert.Equal(3, OrgAccessRequestRules.DefaultDailyLimit);
        Assert.Equal(TimeSpan.FromHours(24), OrgAccessRequestRules.LimitWindow);
        Assert.Equal("OrgTeam:AccessRequestDailyLimit", OrgAccessRequestRules.DailyLimitConfigKey);
        Assert.Equal(12, OrgActivityRules.DefaultRetentionMonths);
        Assert.Equal("OrgTeam:ActivityRetentionMonths", OrgActivityRules.RetentionMonthsConfigKey);
    }
}
