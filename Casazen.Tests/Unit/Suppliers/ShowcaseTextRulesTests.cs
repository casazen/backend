using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// The checks of what the AI wrote for the showcase of a supplier (SP-13d, VT-D20): one proof for each rule where a mistake would put a claim the
/// supplier never made on a public page: a guarantee, a certification, an insurance, years, a time, a price, a rating, an unmeasured adjective, a
/// contact, a number nobody gave. The one exception is what the supplier wrote itself.
/// </summary>
public class ShowcaseTextRulesTests
{
    private static readonly string[] AllFields = [.. ShowcaseTextFields.All];

    private static readonly string[] NothingWritten = [];

    private static ShowcaseTextOutcome Check(string json, IEnumerable<string?>? supplierWritten = null, string[]? fields = null) =>
        ShowcaseTextRules.Check(json, fields ?? AllFields, supplierWritten ?? NothingWritten);

    // ─── What is kept ────────────────────────────────────────────────────────────

    [Fact]
    public void Check_PlainTextWithinTheLimits_IsKeptWhole()
    {
        var outcome = Check("""
            {
              "tagline": "Pulizie per case vacanza e appartamenti",
              "intro": "Pulizie tra un check-out e un check-in, a Cesano Maderno e dintorni.",
              "bio": "Ci occupiamo di pulizie di case vacanza e appartamenti.\n\nLavoriamo a Cesano Maderno e a Seveso.",
              "prep": ["Lascia le chiavi nella cassetta o al portiere.", "Metti al sicuro gli oggetti di valore."]
            }
            """);

        Assert.Equal("Pulizie per case vacanza e appartamenti", outcome.Tagline);
        Assert.Equal("Pulizie tra un check-out e un check-in, a Cesano Maderno e dintorni.", outcome.Intro);
        Assert.Equal("Ci occupiamo di pulizie di case vacanza e appartamenti.\n\nLavoriamo a Cesano Maderno e a Seveso.", outcome.Bio);
        Assert.Equal(2, outcome.Prep.Count);
        Assert.Empty(outcome.Rejected);
        Assert.False(outcome.IsEmpty);
    }

    [Fact]
    public void Check_AnswerInACodeFenceWithASentenceAround_StillReadsTheObject()
    {
        var outcome = Check("Ecco il testo:\n```json\n{\"tagline\": \"Giardinaggio per case vacanza\"}\n```\nSpero vada bene.", fields: [ShowcaseTextFields.Tagline]);

        Assert.Equal("Giardinaggio per case vacanza", outcome.Tagline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("non è un oggetto")]
    [InlineData("{ \"tagline\": ")]
    [InlineData("[\"tagline\"]")]
    public void Check_NotAJsonObject_DropsEveryMember(string content)
    {
        var outcome = Check(content);

        Assert.True(outcome.IsEmpty);
        Assert.Equal(AllFields, outcome.Rejected);
    }

    [Fact]
    public void Check_OnlyTheRequestedMembersAreRead()
    {
        var outcome = Check("""{"tagline": "Pulizie per case vacanza", "bio": "Lavoriamo a Seveso."}""", fields: [ShowcaseTextFields.Tagline]);

        Assert.Equal("Pulizie per case vacanza", outcome.Tagline);
        Assert.Null(outcome.Bio);
        Assert.Empty(outcome.Rejected);
    }

    [Fact]
    public void Check_AMemberThatIsMissingFromTheAnswer_IsReportedAsDropped()
    {
        var outcome = Check("""{"tagline": "Pulizie per case vacanza"}""", fields: [ShowcaseTextFields.Tagline, ShowcaseTextFields.Intro]);

        Assert.Equal("Pulizie per case vacanza", outcome.Tagline);
        Assert.Equal(new[] { ShowcaseTextFields.Intro }, outcome.Rejected);
    }

    // ─── The small corrections ───────────────────────────────────────────────────

    [Fact]
    public void Check_TheSentenceInLargeType_HasNoFullStopNoQuotesAndNoExclamationMark()
    {
        Assert.Equal("Pulizie per case vacanza", Check("""{"tagline": "«Pulizie per case vacanza!»"}""", fields: [ShowcaseTextFields.Tagline]).Tagline);
        Assert.Equal("Pulizie per case vacanza", Check("""{"tagline": "Pulizie per case vacanza."}""", fields: [ShowcaseTextFields.Tagline]).Tagline);
    }

    [Fact]
    public void Check_AnEmojiOrMarkup_IsDropped()
    {
        Assert.Null(Check("""{"intro": "Pulizie per case vacanza 🙂"}""", fields: [ShowcaseTextFields.Intro]).Intro);
        Assert.Null(Check("""{"intro": "Pulizie <b>per</b> case vacanza"}""", fields: [ShowcaseTextFields.Intro]).Intro);
    }

    // ─── The limits ──────────────────────────────────────────────────────────────

    [Fact]
    public void Check_ASentenceOrAnIntroductionOverItsLimit_IsDropped()
    {
        var longTagline = new string('a', ShowcaseContentLimits.TaglineMax + 1);
        var longIntro = new string('a', ShowcaseContentLimits.IntroMax + 1);

        var outcome = Check($$"""{"tagline": "{{longTagline}}", "intro": "{{longIntro}}"}""", fields: [ShowcaseTextFields.Tagline, ShowcaseTextFields.Intro]);

        Assert.Null(outcome.Tagline);
        Assert.Null(outcome.Intro);
        Assert.Equal(new[] { ShowcaseTextFields.Tagline, ShowcaseTextFields.Intro }, outcome.Rejected);
    }

    [Fact]
    public void Check_ALongBio_IsCutAtTheLastSentenceThatFits()
    {
        var sentence = "Lavoriamo a Seveso e a Cesano Maderno. ";
        var bio = string.Concat(Enumerable.Repeat(sentence, 30)).TrimEnd();

        var kept = Check($$"""{"bio": "{{bio}}"}""", fields: [ShowcaseTextFields.Bio]).Bio;

        Assert.NotNull(kept);
        Assert.True(kept!.Length <= ShowcaseContentLimits.BioMax);
        Assert.EndsWith(".", kept);
    }

    [Fact]
    public void Check_ALongBioWithoutAnEndOfSentenceInTheFirstHalf_IsDropped()
    {
        var bio = new string('a', ShowcaseContentLimits.BioMax / 2 - 10) + ". " + new string('a', ShowcaseContentLimits.BioMax);

        Assert.Null(Check($$"""{"bio": "{{bio}}"}""", fields: [ShowcaseTextFields.Bio]).Bio);
    }

    // ─── What the supplier did not say ───────────────────────────────────────────

    [Theory]
    [InlineData("Pulizie con garanzia di soddisfazione", "guarantee")]
    [InlineData("Siamo certificati ISO 9001", "certification")]
    [InlineData("Lavoriamo con una polizza assicurativa", "insurance")]
    [InlineData("Ottime recensioni dei clienti", "rating")]
    [InlineData("Pulizie in 24 ore", "time")]
    [InlineData("Interveniamo entro 2 ore dalla richiesta", "time")]
    [InlineData("Anni di esperienza nelle pulizie", "experience")]
    [InlineData("Dal 2015 al servizio degli host", "experience")]
    [InlineData("Prezzi imbattibili per le pulizie", "price")]
    [InlineData("Pulizie a 30 € l'ora", "price")]
    [InlineData("Un team professionale e puntuale", "quality")]
    [InlineData("Lavoriamo con cura e con passione", "quality")]
    [InlineData("Siamo un fornitore verificato", "platform")]
    [InlineData("Il partner ufficiale degli host", "platform")]
    [InlineData("Scrivici a pulizie@esempio.it", "contact")]
    [InlineData("Visita www.esempio.it", "contact")]
    [InlineData("Chiamaci al 3381234567", "contact")]
    [InlineData("Abbiamo tre squadre e 5 furgoni", "number")]
    public void FindUnsupportedClaim_AClaimTheSupplierDidNotMake_IsFound(string text, string kind)
    {
        Assert.Equal(kind, ShowcaseTextRules.FindUnsupportedClaim(text, NothingWritten));
    }

    [Theory]
    [InlineData("Pulizie tra un check-out e un check-in")]
    [InlineData("Ci occupiamo di pulizie di case vacanza e appartamenti a Seveso")]
    [InlineData("Lascia le chiavi nella cassetta e indica come accedere")]
    [InlineData("Taglio del prato e cura delle siepi")]
    public void FindUnsupportedClaim_PlainDescriptionOfTheJob_IsFine(string text)
    {
        Assert.Null(ShowcaseTextRules.FindUnsupportedClaim(text, NothingWritten));
    }

    /// <summary>
    /// The copy a model plausibly writes when it follows the prompt: what the job is and where, in plain words. The check is blunt on purpose, but
    /// it must not drop this, or the help would answer «unusable» to a supplier who did nothing wrong.
    /// </summary>
    [Fact]
    public void Check_PlainCopyOfAModelThatFollowsThePrompt_IsKeptWhole()
    {
        var outcome = Check("""
            {
              "tagline": "Pulizie e giardinaggio per case vacanza e appartamenti",
              "intro": "Ci occupiamo delle pulizie tra un ospite e l'altro e del giardino, a Cesano Maderno e dintorni.",
              "bio": "Siamo una piccola squadra che si occupa di pulizie di fine soggiorno e di giardinaggio per case vacanza e appartamenti.\n\nLavoriamo a Cesano Maderno, a Seveso e nei comuni vicini. Dopo ogni intervento lasciamo la casa pronta per l'ospite successivo.",
              "prep": [
                "Lascia le chiavi nella cassetta o al portiere e indica come accedere.",
                "Metti al sicuro gli oggetti di valore e i documenti.",
                "Segnala le stanze che non vanno toccate.",
                "Se c'è un giardino, indica dove si trovano gli attrezzi e l'acqua."
              ]
            }
            """);

        Assert.NotNull(outcome.Tagline);
        Assert.NotNull(outcome.Intro);
        Assert.NotNull(outcome.Bio);
        Assert.Equal(4, outcome.Prep.Count);
        Assert.Empty(outcome.Rejected);
    }

    [Fact]
    public void FindUnsupportedClaim_CaseAndAccentsDoNotMatter()
    {
        Assert.Equal("guarantee", ShowcaseTextRules.FindUnsupportedClaim("GARANZÌA totale", NothingWritten));
        Assert.Equal("quality", ShowcaseTextRules.FindUnsupportedClaim("Servizio Affidabile", NothingWritten));
    }

    [Fact]
    public void Check_ADropOfAMemberWithAClaim_LeavesTheOthersAlone()
    {
        var outcome = Check("""{"tagline": "Pulizie per case vacanza", "intro": "Garantiamo pulizie in 24 ore", "bio": "Lavoriamo a Seveso."}""");

        Assert.Equal("Pulizie per case vacanza", outcome.Tagline);
        Assert.Null(outcome.Intro);
        Assert.Equal("Lavoriamo a Seveso.", outcome.Bio);
        Assert.Contains(ShowcaseTextFields.Intro, outcome.Rejected);
        Assert.DoesNotContain(ShowcaseTextFields.Tagline, outcome.Rejected);
    }

    // ─── What the supplier said itself ───────────────────────────────────────────

    [Fact]
    public void Check_AClaimTheSupplierWroteItself_MayBeRepeated()
    {
        var written = new[] { "Siamo certificati ISO 9001 e lavoriamo dal 2015." };

        var outcome = Check("""{"bio": "Siamo certificati ISO 9001 e lavoriamo dal 2015 a Seveso."}""", written, [ShowcaseTextFields.Bio]);

        Assert.Equal("Siamo certificati ISO 9001 e lavoriamo dal 2015 a Seveso.", outcome.Bio);
    }

    [Fact]
    public void Check_AClaimTheSupplierWroteIsNotAnExcuseForAnother()
    {
        var written = new[] { "Siamo certificati ISO 9001." };

        // The certification is the supplier's own; the guarantee and the number of years are not.
        var outcome = Check("""{"bio": "Siamo certificati ISO 9001 e diamo la garanzia su ogni lavoro."}""", written, [ShowcaseTextFields.Bio]);

        Assert.Null(outcome.Bio);
    }

    [Fact]
    public void Check_TheNamesOfTheServices_CountAsWrittenBySupplier()
    {
        var written = new[] { "Pulizia professionale di fine soggiorno" };

        Assert.NotNull(Check("""{"intro": "Offriamo la pulizia professionale di fine soggiorno."}""", written, [ShowcaseTextFields.Intro]).Intro);
        Assert.Null(Check("""{"intro": "Offriamo la pulizia professionale di fine soggiorno."}""", NothingWritten, [ShowcaseTextFields.Intro]).Intro);
    }

    [Fact]
    public void Check_ContactDataIsNeverAllowed_EvenIfTheSupplierTypedIt()
    {
        var written = new[] { "Scrivici a info@esempio.it" };

        Assert.Null(Check("""{"intro": "Scrivici a info@esempio.it"}""", written, [ShowcaseTextFields.Intro]).Intro);
    }

    // ─── The tips "prima che arriviamo" ──────────────────────────────────────────

    [Fact]
    public void Check_TheTips_DropOnlyTheBadOnesAndTheRepeats()
    {
        var outcome = Check(
            """
            {"prep": [
              "Lascia le chiavi alla portineria.",
              "Garantiamo l'arrivo in 24 ore",
              "- Metti al sicuro gli oggetti di valore",
              "1. Segnala le stanze da non toccare",
              "lascia le chiavi alla portineria."
            ]}
            """,
            fields: [ShowcaseTextFields.Prep]);

        Assert.Equal(
            new[] { "Lascia le chiavi alla portineria.", "Metti al sicuro gli oggetti di valore", "Segnala le stanze da non toccare" },
            outcome.Prep);
        Assert.Empty(outcome.Rejected);
    }

    [Fact]
    public void Check_TheTips_AreAtMostSix()
    {
        var items = string.Join(",", Enumerable.Range(0, 9).Select(i => $"\"Consiglio utile {(char)('a' + i)}\""));

        var outcome = Check($$"""{"prep": [{{items}}]}""", fields: [ShowcaseTextFields.Prep]);

        Assert.Equal(ShowcaseContentLimits.PrepMax, outcome.Prep.Count);
    }

    [Fact]
    public void Check_TheTips_AllBad_AreReportedAsDropped()
    {
        var outcome = Check("""{"prep": ["Garantiamo tutto", "Subito alle 9 in punto"]}""", fields: [ShowcaseTextFields.Prep]);

        Assert.Empty(outcome.Prep);
        Assert.Equal(new[] { ShowcaseTextFields.Prep }, outcome.Rejected);
    }

    // ─── The members and the answers of the request ──────────────────────────────

    [Fact]
    public void Fields_NoneNamedOrNoneKnown_MeansAllOfThem()
    {
        Assert.Equal(ShowcaseTextFields.All, ShowcaseTextFields.Normalize(null));
        Assert.Equal(ShowcaseTextFields.All, ShowcaseTextFields.Normalize([]));
        Assert.Equal(ShowcaseTextFields.All, ShowcaseTextFields.Normalize(["guarantee", "certs"]));
    }

    [Fact]
    public void Fields_AreKnownOnesOnce_InTheOrderOfTheEditor()
    {
        Assert.Equal(new[] { ShowcaseTextFields.Bio, ShowcaseTextFields.Prep }, ShowcaseTextFields.Normalize(["PREP", " bio ", "bio", "guarantee", null]));
    }

    [Fact]
    public void Answers_AreCleanedAndCutAtTheirLimit_AndBlankOnesAreMissing()
    {
        var cleaned = new ShowcaseTextAnswers("  Due   righe\ncosì  ", new string('a', ShowcaseTextAnswers.YearsMax + 20), "   ", null).Cleaned();

        Assert.Equal("Due righe così", cleaned.About);
        Assert.Equal(ShowcaseTextAnswers.YearsMax, cleaned.Years!.Length);
        Assert.Null(cleaned.Specialties);
        Assert.Null(cleaned.Differentiator);
        Assert.Equal(new[] { ShowcaseTextAnswerKeys.About, ShowcaseTextAnswerKeys.Years }, cleaned.Keys());
        Assert.False(cleaned.IsEmpty);
        Assert.True(ShowcaseTextAnswers.None.IsEmpty);
    }
}
