using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.SiteProfile;
using Xunit;

namespace Casazen.Tests.Unit.SiteProfile;

/// <summary>
/// DB-11a: what the host writes for the property page is stored clean and within its limits, in Italian wherever there is a text, from
/// the fixed vocabularies; a mistake is a 422 with a stable code and the field that broke it, and nothing half-valid is kept. The labels the
/// site gives a house from its own data are decided here and nowhere else.
/// </summary>
public class SitePropertyProfileRulesTests
{
    private const int Year = 2026;

    private static SitePropertyProfileInput Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static LocalizedTextInput Text(string? it, string? en = null) => new(it, en);

    private static DomainRuleException Rejected(SitePropertyProfileInput input, TimeOnly? checkInFrom = null) =>
        Assert.Throws<DomainRuleException>(() => SitePropertyProfileRules.Normalize(input, checkInFrom, Year));

    [Fact]
    public void Normalize_AFullProfile_IsStoredCleanAndInCanonicalForm()
    {
        var values = SitePropertyProfileRules.Normalize(
            Empty with
            {
                FloorLevel = 0,
                RenovatedYear = 2021,
                EnergyClass = " d ",
                EnergyIndex = 148.456m,
                AreaKind = PropertyAreaKind.Country,
                ChildrenWelcome = true,
                CheckInTo = new TimeOnly(20, 0, 59),
                VideoUrl = "https://youtu.be/dQw4w9WgXcQ?si=x",
                TourUrl = "https://my.matterport.com/show?m=SxQL3iGyoDo&play=1",
                Zone = Text("  Campagna di Ostuni,\n Valle d'Itria\t", " Countryside near Ostuni "),
                RulesNote = Text("Il giardino è recintato.\r\n\r\n\r\n\r\nNiente feste.\u0007", "The garden is fenced."),
                Highlights = [new SiteHighlightInput(" Waves ", Text("Piscina privata", "Private pool"), Text("Solo per voi.", null))],
                SafetyNotes = [Text("Estintore"), Text("   "), Text("Rilevatore di fumo", "Smoke detector")],
                Tags = ["Piscina", " campagna ", "piscina", "mare"],
            },
            checkInFrom: new TimeOnly(15, 0),
            currentYear: Year);

        Assert.Equal(0, values.FloorLevel);
        Assert.Equal(2021, values.RenovatedYear);
        Assert.Equal("D", values.EnergyClass);
        Assert.Equal(148.46m, values.EnergyIndex);
        Assert.Equal(PropertyAreaKind.Country, values.AreaKind);
        Assert.True(values.ChildrenWelcome);
        Assert.Equal(new TimeOnly(20, 0), values.CheckInTo);
        Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", values.VideoUrl);
        Assert.Equal("https://my.matterport.com/show/?m=SxQL3iGyoDo", values.TourUrl);

        var document = values.Document;
        Assert.Equal(new LocalizedText("Campagna di Ostuni, Valle d'Itria", "Countryside near Ostuni"), document.Zone);
        Assert.Equal("Il giardino è recintato.\n\nNiente feste.", document.RulesNote!.It);
        Assert.Equal(new SiteHighlight("waves", new LocalizedText("Piscina privata", "Private pool"), new LocalizedText("Solo per voi.")), Assert.Single(document.Highlights));
        Assert.Equal(["Estintore", "Rilevatore di fumo"], document.SafetyNotes.Select(n => n.It));
        // The vocabulary's order, each once.
        Assert.Equal(["mare", "piscina", "campagna"], document.Tags);
    }

    [Fact]
    public void Normalize_ATextWithAnEnglishPartAndNoItalianOne_IsRefusedWithTheField()
    {
        var error = Rejected(Empty with { Near = Text("   ", "Ten minutes from the sea") });

        Assert.Equal(SitePropertyProfileErrorCodes.ItalianRequired, error.Code);
        Assert.Equal("near", error.Extensions["field"]);
    }

    public static TheoryData<SitePropertyProfileInput, string, string> Mistakes() => new()
    {
        { Empty with { Zone = Text(new string('a', SitePropertyProfileLimits.ZoneMaxLength + 1)) }, SitePropertyProfileErrorCodes.TextTooLong, "zone" },
        { Empty with { Highlights = [new("waves", Text("a"), null), new("sun", Text("b"), null), new("key", Text("c"), null), new("door", Text("d"), null)] }, SitePropertyProfileErrorCodes.TooManyHighlights, "highlights" },
        { Empty with { Highlights = [new("not-an-icon", Text("Piscina"), null)] }, SitePropertyProfileErrorCodes.HighlightIconUnknown, "highlights" },
        { Empty with { Tags = ["piscina", "con-sauna"] }, SitePropertyProfileErrorCodes.TagUnknown, "tags" },
        { Empty with { FloorLevel = SitePropertyProfileLimits.MaxFloorLevel + 1 }, SitePropertyProfileErrorCodes.FloorLevelInvalid, "floorLevel" },
        { Empty with { RenovatedYear = Year + 1 }, SitePropertyProfileErrorCodes.RenovatedYearInvalid, "renovatedYear" },
        { Empty with { EnergyClass = "H" }, SitePropertyProfileErrorCodes.EnergyClassInvalid, "energyClass" },
        { Empty with { EnergyIndex = 120m }, SitePropertyProfileErrorCodes.EnergyIndexWithoutClass, "energyIndex" },
        { Empty with { VideoUrl = "http://www.youtube.com/watch?v=dQw4w9WgXcQ" }, SitePropertyProfileErrorCodes.VideoUrlInvalid, "videoUrl" },
        { Empty with { TourUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }, SitePropertyProfileErrorCodes.TourUrlInvalid, "tourUrl" },
    };

    [Theory]
    [MemberData(nameof(Mistakes))]
    public void Normalize_ARuleBroken_IsRefusedWithTheStableCodeAndTheField(SitePropertyProfileInput input, string code, string field)
    {
        var error = Rejected(input);

        Assert.Equal(code, error.Code);
        Assert.Equal(field, error.Extensions["field"]);
    }

    [Theory]
    [InlineData(15, 0, 15, 0)]
    [InlineData(15, 0, 14, 0)]
    public void Normalize_ALatestArrivalNotAfterTheEarliest_IsRefused(int fromHour, int fromMinute, int toHour, int toMinute)
    {
        var error = Rejected(Empty with { CheckInTo = new TimeOnly(toHour, toMinute) }, new TimeOnly(fromHour, fromMinute));

        Assert.Equal(SitePropertyProfileErrorCodes.CheckInWindowInvalid, error.Code);
        Assert.Equal("checkInTo", error.Extensions["field"]);
    }

    [Theory]
    [InlineData("  a   b \t c  ", false, "a b c")]
    [InlineData("a\r\nb", false, "a b")]
    [InlineData("a\r\nb", true, "a\nb")]
    [InlineData("a\n\n\n\nb", true, "a\n\nb")]
    [InlineData("\n\n a \n\n", true, "a")]
    [InlineData("a\u0000b\u0007c", false, "abc")]
    [InlineData("   ", false, null)]
    [InlineData(null, false, null)]
    public void CleanText_DropsControlCharacters_CollapsesSpaces_AndKeepsAtMostOneBlankLineInAMultiLineText(string? raw, bool multiline, string? expected) =>
        Assert.Equal(expected, SitePropertyProfileRules.CleanText(raw, multiline));
}
