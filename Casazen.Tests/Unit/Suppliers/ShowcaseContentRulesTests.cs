using System.Text.Json.Nodes;
using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-13a: the rules of the content of a supplier's showcase, one proof for each rule where a mistake would put a defect, a
/// foreign file or a lost draft on a public page: the color that white text must be able to read, the lengths of the editor's
/// counters, the services that may be shown, the images (the supplier's own, never SVG), and what a save does to the draft.
/// </summary>
public class ShowcaseContentRulesTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static string Own(string file) => $"https://storage.test/public/suppliers/{Org}/photos/{file}";

    private static ShowcaseContentContext Context(params string[] activeSlugs) =>
        new(url => url.StartsWith($"https://storage.test/public/suppliers/{Org}/", StringComparison.Ordinal), activeSlugs);

    private static ShowcaseContentDocument Apply(string patch, string? draft = null, ShowcaseContentContext? context = null) =>
        ShowcaseContentRules.ApplyPatch(draft, (JsonObject)JsonNode.Parse(patch)!, context ?? Context());

    private static DomainRuleException Refused(string patch, string? draft = null, ShowcaseContentContext? context = null) =>
        Assert.Throws<DomainRuleException>(() => Apply(patch, draft, context));

    private static IReadOnlyList<string> FieldsOf(DomainRuleException exception) =>
        Assert.IsAssignableFrom<IReadOnlyList<string>>(exception.Extensions["fields"]);

    // ─── The color ───────────────────────────────────────────────────────────────

    [Fact]
    public void Color_WhiteTextReadsAtFourPointFiveOrMore_IsKeptInUpperCase()
    {
        Assert.Equal("#0B5FA5", Apply("""{"color":"#0b5fa5"}""").Color);

        // 4.54:1 is the darkest-but-one gray that passes; 4.48:1 is the lightest that does not.
        Assert.Equal("#767676", Apply("""{"color":"#767676"}""").Color);
        Assert.True(ShowcaseColor.IsReadableWithWhite("#767676"));
        Assert.False(ShowcaseColor.IsReadableWithWhite("#777777"));
    }

    [Theory]
    [InlineData("#777777")]
    [InlineData("#FFFF00")]
    public void Color_WhiteTextCannotBeReadOnIt_IsRefusedAndAReadableOneIsProposed(string color)
    {
        var refused = Refused($$"""{"color":"{{color}}"}""");

        Assert.Equal(new[] { "color" }, FieldsOf(refused));
        var suggestion = Assert.IsType<string>(refused.Extensions["suggestedColor"]);
        Assert.True(ShowcaseColor.IsReadableWithWhite(suggestion));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#12345G")]
    public void Color_NotASixDigitHexColor_IsRefused(string color)
    {
        Assert.Equal(new[] { "color" }, FieldsOf(Refused($$"""{"color":"{{color}}"}""")));
    }

    // ─── Lengths and text ────────────────────────────────────────────────────────

    [Fact]
    public void Texts_AtTheLimitAreKept_OneMoreIsRefusedWithThePathOfTheField()
    {
        Assert.Equal(90, Apply($$"""{"tagline":"{{new string('a', 90)}}"}""").Tagline!.Length);
        Assert.Equal(new[] { "tagline" }, FieldsOf(Refused($$"""{"tagline":"{{new string('a', 91)}}"}""")));

        var answers = $$"""{"faq":[{"q":"a","a":"b"},{"q":"a","a":"b"},{"q":"a","a":"{{new string('x', 601)}}"}]}""";
        Assert.Equal(new[] { "faq[2].a" }, FieldsOf(Refused(answers)));
    }

    [Fact]
    public void Lists_OverTheirLimit_AreRefused()
    {
        var thirteenQuestions = string.Join(",", Enumerable.Repeat("""{"q":"a","a":"b"}""", ShowcaseContentLimits.FaqMax + 1));

        Assert.Contains("faq", FieldsOf(Refused($$"""{"faq":[{{thirteenQuestions}}]}""")));
    }

    [Fact]
    public void Text_IsPlain_MarkupIsRefusedAndALoneLessThanSignIsNot()
    {
        Assert.Equal(new[] { "bio" }, FieldsOf(Refused("""{"bio":"<script>alert(1)</script>"}""")));
        Assert.Equal("meno di 5 < 10 minuti", Apply("""{"bio":"meno di 5 < 10 minuti"}""").Bio);
    }

    [Fact]
    public void Text_IsCleaned_SpacesCollapseAndInvisibleCharactersGo()
    {
        var zeroWidthSpace = (char)0x200B;
        Assert.Equal("Pronti in 1 ora", Apply($$"""{"tagline":"  Pronti   in\n\n 1 ora{{zeroWidthSpace}} "}""").Tagline);
    }

    // ─── Services and images ─────────────────────────────────────────────────────

    [Fact]
    public void ServiceOrder_OnlyActiveServicesOfTheSupplier_EachOnce()
    {
        var context = Context("pulizia", "bucato");

        Assert.Equal(new[] { "pulizia", "bucato" }, Apply("""{"serviceOrder":["pulizia","bucato"]}""", context: context).ServiceOrder);

        // A draft service (not active), and a repeated one.
        var refused = Refused("""{"serviceOrder":["pulizia","bozza","pulizia"]}""", context: context);
        Assert.Equal(new[] { "serviceOrder[1]", "serviceOrder[2]" }, FieldsOf(refused));
    }

    [Fact]
    public void ServiceOrder_ASlugThatStoppedBeingActive_LeavesQuietlyWhenTheSaveIsAboutSomethingElse()
    {
        var draft = """{"serviceOrder":["pulizia","vecchio"]}""";

        var saved = Apply("""{"tagline":"Ciao"}""", draft, Context("pulizia"));

        Assert.Equal(new[] { "pulizia" }, saved.ServiceOrder);
        Assert.Equal("Ciao", saved.Tagline);
    }

    [Fact]
    public void Images_OnlyPngJpgWebpOfTheSuppliersOwnFolder_NeverSvgNeverAnotherSite()
    {
        Assert.Equal(Own("a.PNG"), Apply($$$"""{"cover":{"url":"{{{Own("a.PNG")}}}","alt":"Cucina"}}""").Cover!.Url);

        Assert.Equal(new[] { "cover.url" }, FieldsOf(Refused($$$"""{"cover":{"url":"{{{Own("a.svg")}}}"}}""")));
        Assert.Equal(new[] { "cover.url" }, FieldsOf(Refused("""{"cover":{"url":"https://evil.test/a.png"}}""")));
        Assert.Equal(new[] { "logo.url" }, FieldsOf(Refused($$$"""{"logo":{"mode":"image","url":"{{{Own("logo.svg")}}}"}}""")));
    }

    [Fact]
    public void Gallery_ABeforePhotoNeedsTheAfterPhoto()
    {
        var refused = Refused($$"""{"gallery":[{"beforeUrl":"{{Own("a.png")}}"}]}""");

        Assert.Equal(new[] { "gallery[0].photoUrl" }, FieldsOf(refused));
    }

    // ─── What a save does to the draft ───────────────────────────────────────────

    [Fact]
    public void Save_IsAMergePatch_ObjectsMergeListsReplaceNullClears()
    {
        var draft = """{"sections":{"faq":false},"values":["a","b"],"tagline":"x"}""";

        var saved = Apply("""{"sections":{"how":false},"values":["c"],"tagline":null}""", draft);

        Assert.False(saved.Sections.Faq);
        Assert.False(saved.Sections.How);
        Assert.True(saved.Sections.Works);
        Assert.Equal(new[] { "c" }, saved.Values);
        Assert.Null(saved.Tagline);
    }

    [Fact]
    public void Save_AMemberOutsideTheContract_IsRefusedOnThatMember_NoContactDataGetsIn()
    {
        Assert.Equal(new[] { "phone" }, FieldsOf(Refused("""{"phone":"3331234567"}""")));
        Assert.Equal(new[] { "cover.whatsapp" }, FieldsOf(Refused("""{"cover":{"whatsapp":"1"}}""")));
    }

    // ─── Draft and publication ───────────────────────────────────────────────────

    [Fact]
    public void Draft_MayBeIncomplete_APublicationDropsEmptyRowsAndRefusesHalfFilledOnes()
    {
        var incomplete = Apply("""{"faq":[{"q":"Domanda"}],"team":[{},{"name":"Ada","role":"Idraulico"}]}""");

        var refused = Assert.Throws<DomainRuleException>(() => ShowcaseContentRules.ForPublication(incomplete, []));
        Assert.Equal(new[] { "faq[0].a" }, FieldsOf(refused));

        var teamOnly = Apply("""{"team":[{},{"name":"Ada","role":"Idraulico"}]}""");
        Assert.Single(ShowcaseContentRules.ForPublication(teamOnly, []).Team);
    }

    [Fact]
    public void Publication_ShowsOnlyTheServicesThatAreActiveNow()
    {
        var draft = Apply("""{"serviceOrder":["pulizia","bucato"]}""", context: Context("pulizia", "bucato"));

        var published = ShowcaseContentRules.ForPublication(draft, ["pulizia"]);

        Assert.Equal(new[] { "pulizia" }, published.ServiceOrder);
    }

    [Fact]
    public void Changes_ListsTheMembersThatDifferFromWhatIsPublished_InTheOrderOfTheContract()
    {
        var published = new ShowcaseContentDocument { Tagline = "A" };
        var draft = new ShowcaseContentDocument { Tagline = "B", Color = "#0B5FA5" };

        Assert.Equal(new[] { "color", "tagline" }, ShowcaseContentRules.Changes(published, draft));
        Assert.Empty(ShowcaseContentRules.Changes(draft, draft));
    }
}
