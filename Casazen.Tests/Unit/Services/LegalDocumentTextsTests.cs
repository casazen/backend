using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LEGAL-TEXTS: the drafts of the Terms of Service, Privacy notice and DPA shipped with the API load, their
/// placeholders are replaced from the configuration, a missing mandatory value keeps a document unpublished
/// (fail-closed, decision D9), Italian and English have the same sections, and the re-acceptance (PL-02) follows the
/// version only. The texts are drafts written by an AI agent: they still need a lawyer's review.
/// </summary>
public class LegalDocumentTextsTests
{
    private static readonly LegalDocumentKind[] Kinds = [LegalDocumentKind.Tos, LegalDocumentKind.Privacy, LegalDocumentKind.Dpa];
    private static readonly string[] Languages = ["it", "en"];

    // ─── The shipped drafts load and their values are replaced ───────────────────────────────────

    [Theory]
    [InlineData(LegalDocumentKind.Tos, "it")]
    [InlineData(LegalDocumentKind.Tos, "en")]
    [InlineData(LegalDocumentKind.Privacy, "it")]
    [InlineData(LegalDocumentKind.Privacy, "en")]
    [InlineData(LegalDocumentKind.Dpa, "it")]
    [InlineData(LegalDocumentKind.Dpa, "en")]
    public void GetText_CompleteConfiguration_PublishesTheDraftInTheRequestedLanguage(LegalDocumentKind kind, string language)
    {
        var text = Service(LegalTextFixtures.CompleteConfiguration()).GetText(kind, language);

        Assert.NotNull(text);
        Assert.Equal(language, text.Language);
        Assert.StartsWith("<h2>1.", text.Html);
        Assert.Contains("Test Rentals S.r.l.", text.Html);
        Assert.Contains("privacy@test-rentals.test", text.Html);
        Assert.Contains("https://app.test/legale/", text.Html);
    }

    [Theory]
    [InlineData(LegalDocumentKind.Tos)]
    [InlineData(LegalDocumentKind.Privacy)]
    [InlineData(LegalDocumentKind.Dpa)]
    public void GetText_CompleteConfiguration_LeavesNoPlaceholderNorDraftNoticeToTheReader(LegalDocumentKind kind)
    {
        var service = Service(LegalTextFixtures.CompleteConfiguration());

        foreach (var language in Languages)
        {
            var html = service.GetText(kind, language)!.Html;

            Assert.DoesNotContain("{{", html);
            Assert.DoesNotContain("}}", html);
            Assert.DoesNotContain("#if", html);
            // The draft notice is metadata of the file (an HTML comment): it is not part of the page shown to hosts.
            Assert.DoesNotContain("agente AI", html);
            Assert.DoesNotContain("AI agent", html);
            Assert.DoesNotContain("<!--", html);
        }
    }

    [Fact]
    public void GetText_Terms_ShowsTheRealPlansOfTheCatalogueWithConfiguredPrices()
    {
        var service = Service(LegalTextFixtures.CompleteConfiguration());

        var italian = service.GetText(LegalDocumentKind.Tos, "it")!.Html;
        var english = service.GetText(LegalDocumentKind.Tos, "en")!.Html;

        foreach (var plan in PlanCatalog.All)
        {
            Assert.Contains($"<td>{plan.DisplayName}</td>", italian);
            Assert.Contains($"<td>{plan.DisplayName}</td>", english);
        }

        Assert.Contains("<td>29,00 €</td>", italian);
        Assert.Contains("<td>79,00 €</td>", italian);
        Assert.Contains("<td>199,00 €</td>", italian);
        Assert.Contains("<td>€29.00</td>", english);
        Assert.Contains("<td>€199.00</td>", english);
        // The limits come from PlanCatalog: 3 and 50 properties, the last tier unlimited.
        Assert.Contains("<td>3</td>", italian);
        Assert.Contains("<td>50</td>", italian);
        Assert.Contains("<td>illimitati</td>", italian);
        Assert.Contains("<td>unlimited</td>", english);
    }

    [Fact]
    public void GetText_Terms_PlanWithoutConfiguredPrice_SendsTheReaderToThePriceShownAtPurchase()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("Billing:Display:Pro:PriceMonthly");

        var italian = Service(settings).GetText(LegalDocumentKind.Tos, "it")!.Html;

        Assert.Contains("<td>quello indicato al momento dell'acquisto</td>", italian.Replace("&#39;", "'"));
        Assert.Contains("<td>29,00 €</td>", italian);
    }

    [Fact]
    public void GetText_Terms_UsesTheGracePeriodOfTheBillingConfiguration()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();

        Assert.Contains("tolleranza di 7 giorni", Service(settings).GetText(LegalDocumentKind.Tos, "it")!.Html);

        settings["Billing:PastDueGraceDays"] = "14";
        Assert.Contains("tolleranza di 14 giorni", Service(settings).GetText(LegalDocumentKind.Tos, "it")!.Html);
        Assert.Contains("grace period of 14 days", Service(settings).GetText(LegalDocumentKind.Tos, "en")!.Html);
    }

    [Fact]
    public void GetText_NegotiableTerms_AreTakenFromTheConfiguration()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Terms:LiabilityCapMonths"] = "6";
        settings["Legal:Terms:GoverningCourt"] = "Bologna";
        settings["Legal:Dpa:BreachNotificationHours"] = "24";

        var service = Service(settings);

        Assert.Contains("nei 6 mesi precedenti", service.GetText(LegalDocumentKind.Tos, "it")!.Html);
        Assert.Contains("Foro di Bologna", service.GetText(LegalDocumentKind.Tos, "it")!.Html);
        Assert.Contains("entro 24 ore", service.GetText(LegalDocumentKind.Dpa, "it")!.Html);
        Assert.Contains("within 24 hours", service.GetText(LegalDocumentKind.Dpa, "en")!.Html);
    }

    [Fact]
    public void GetText_ValueWithMarkup_IsEncodedNotInterpreted()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Controller:Name"] = "Rossi & Figli <b>S.r.l.</b>";

        var html = Service(settings).GetText(LegalDocumentKind.Privacy, "it")!.Html;

        Assert.Contains("Rossi &amp; Figli &lt;b&gt;S.r.l.&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>S.r.l.</b>", html);
    }

    // ─── The placeholder engine ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_CommentMentioningPlaceholders_IsRemovedAndHarmless()
    {
        var result = LegalDocumentTemplate.Render(
            "<!-- the {{...}} placeholders are filled from configuration -->\n<p>{{Controller.Name}}</p>",
            Variables());

        Assert.True(result.IsComplete);
        Assert.Equal("\n<p>Test Rentals S.r.l.</p>", result.Html);
    }

    [Fact]
    public void Render_UnknownPlaceholder_IsAProblemNeverAnEmptyString()
    {
        var result = LegalDocumentTemplate.Render("<p>{{Controller.Nmae}}</p>{{#if Nothing.Here}}x{{/if}}", Variables());

        Assert.False(result.IsComplete);
        Assert.Contains("unknown placeholder 'Controller.Nmae'", result.Problems);
        Assert.Contains("unknown placeholder 'Nothing.Here'", result.Problems);
    }

    [Fact]
    public void Render_NestedOrUnclosedCondition_IsMalformedAndNotComplete()
    {
        var nested = LegalDocumentTemplate.Render(
            "{{#if Controller.DpoEmail}}a{{#if Controller.Name}}b{{/if}}c{{/if}}", Variables());
        var unclosed = LegalDocumentTemplate.Render("{{#if Controller.Name}}never closed", Variables());

        Assert.False(nested.IsComplete);
        Assert.False(unclosed.IsComplete);
        Assert.Contains(nested.Problems, problem => problem.StartsWith("malformed placeholder syntax", StringComparison.Ordinal));
        Assert.Contains(unclosed.Problems, problem => problem.StartsWith("malformed placeholder syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_Condition_ShowsTheThenPartWithAValueAndTheElsePartWithout()
    {
        const string template = "{{#if Controller.DpoEmail}}DPO {{Controller.DpoEmail}}{{#else}}no DPO{{/if}}";

        Assert.Equal("no DPO", LegalDocumentTemplate.Render(template, Variables()).Html);
        Assert.Equal(
            "DPO dpo@test-rentals.test",
            LegalDocumentTemplate.Render(template, Variables(("Legal:Controller:DpoEmail", "dpo@test-rentals.test"))).Html);
    }

    [Fact]
    public void Render_MissingValueOutsideACondition_NamesTheVariableOnly()
    {
        var result = LegalDocumentTemplate.Render("<p>{{Controller.Pec}} {{Terms.GoverningCourt}}</p>", Variables(("Legal:Controller:Pec", "")));

        Assert.False(result.IsComplete);
        Assert.Equal(new[] { "Legal__Controller__Pec" }, result.MissingConfiguration);
        Assert.DoesNotContain("Milano", string.Join(" ", result.MissingConfiguration.Concat(result.Problems)));
    }

    // ─── Fail-closed (D9): no text, no placeholder, and the app says why ──────────────────────────

    [Theory]
    [InlineData("Legal:Controller:Name", "Legal__Controller__Name")]
    [InlineData("Legal:Controller:Address", "Legal__Controller__Address")]
    [InlineData("Legal:Controller:VatId", "Legal__Controller__VatId")]
    [InlineData("Legal:Controller:Pec", "Legal__Controller__Pec")]
    [InlineData("Legal:Controller:PrivacyEmail", "Legal__Controller__PrivacyEmail")]
    public void GetText_MandatoryControllerValueMissing_NoDocumentIsPublishedAndTheVariableIsNamed(string key, string variable)
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove(key);
        // The VAT number falls back to the billing one: make sure that is not what keeps the document published.
        settings.Remove("Billing:VatNumber");
        var service = Service(settings);

        foreach (var kind in Kinds)
        {
            foreach (var language in Languages)
                Assert.Null(service.GetText(kind, language));

            var publication = service.GetPublication(kind);
            Assert.False(publication.IsPublished);
            Assert.True(publication.TextFileFound);
            Assert.Contains(variable, publication.MissingConfiguration);
            Assert.Contains(variable, publication.Describe());
        }
    }

    [Fact]
    public void GetText_GoverningCourtMissing_OnlyTheDocumentsThatNeedItAreUnpublished()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("Legal:Terms:GoverningCourt");
        var service = Service(settings);

        Assert.False(service.GetPublication(LegalDocumentKind.Tos).IsPublished);
        Assert.False(service.GetPublication(LegalDocumentKind.Dpa).IsPublished);
        Assert.True(service.GetPublication(LegalDocumentKind.Privacy).IsPublished);
        Assert.Null(service.GetText(LegalDocumentKind.Tos, "it"));
        Assert.NotNull(service.GetText(LegalDocumentKind.Privacy, "it"));
    }

    [Theory]
    [InlineData("TODO")]
    [InlineData("YOUR_COMPANY_NAME")]
    [InlineData("[ragione sociale]")]
    [InlineData("Acme ...")]
    public void GetText_PlaceholderValue_IsTreatedAsNotConfigured(string placeholder)
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Controller:Name"] = placeholder;

        var service = Service(settings);

        Assert.Null(service.GetText(LegalDocumentKind.Privacy, "it"));
        Assert.Contains("Legal__Controller__Name", service.GetPublication(LegalDocumentKind.Privacy).MissingConfiguration);
    }

    [Theory]
    [InlineData("Legal:Controller:PrivacyEmail", "not an email")]
    [InlineData("Legal:Terms:ChangeNoticeDays", "abc")]
    [InlineData("Legal:Terms:ChangeNoticeDays", "0")]
    [InlineData("Legal:Dpa:BreachNotificationHours", "-5")]
    [InlineData("Legal:Terms:LiabilityCapMonths", "")]
    public void GetText_InvalidValue_IsTreatedAsNotConfigured(string key, string value)
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings[key] = value;
        var service = Service(settings);

        Assert.False(
            service.GetPublication(LegalDocumentKind.Tos).IsPublished
            && service.GetPublication(LegalDocumentKind.Privacy).IsPublished
            && service.GetPublication(LegalDocumentKind.Dpa).IsPublished,
            $"{key}={value} must keep at least one document unpublished");
    }

    [Fact]
    public void GetText_NoPublicSiteUrl_NoDocumentIsPublished()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("App:PublicSiteBaseUrl");

        var service = Service(settings);

        foreach (var kind in Kinds)
            Assert.Contains("App__PublicSiteBaseUrl", service.GetPublication(kind).MissingConfiguration);
    }

    [Fact]
    public void GetText_VatIdOnlyInBillingConfiguration_IsUsed()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("Legal:Controller:VatId");
        settings["Billing:VatNumber"] = "IT10987654321";

        var html = Service(settings).GetText(LegalDocumentKind.Tos, "it")!.Html;

        Assert.Contains("IT10987654321", html);
    }

    [Fact]
    public void GetText_VersionWithoutAFile_IsNotPublishedAndSaysSo()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Documents:Tos:Version"] = "2026-06-v1";

        var service = Service(settings);
        var publication = service.GetPublication(LegalDocumentKind.Tos);

        Assert.Null(service.GetText(LegalDocumentKind.Tos, "it"));
        Assert.False(publication.TextFileFound);
        Assert.False(publication.IsPublished);
        Assert.Contains("no text file for this version", publication.Describe());
        Assert.True(service.GetPublication(LegalDocumentKind.Privacy).IsPublished);
    }

    [Fact]
    public void GetPublication_ExternalCopyConfigured_CountsAsPublished()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Documents:Tos:Version"] = "2026-06-v1";
        settings["Legal:Documents:Tos:DocumentUrl"] = "https://legal.test/tos.pdf";

        Assert.True(Service(settings).GetPublication(LegalDocumentKind.Tos).IsPublished);
    }

    [Fact]
    public void GetPublication_OptionalValuesMissing_DoNotBlockAndTheirTextIsAbsent()
    {
        var service = Service(LegalTextFixtures.CompleteConfiguration());

        var privacy = service.GetText(LegalDocumentKind.Privacy, "it")!.Html;
        var terms = service.GetText(LegalDocumentKind.Tos, "it")!.Html;

        Assert.True(service.GetPublication(LegalDocumentKind.Privacy).IsPublished);
        Assert.DoesNotContain("responsabile della protezione dei dati è raggiungibile", privacy);
        Assert.DoesNotContain("numero REA", terms);

        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Controller:DpoEmail"] = "dpo@test-rentals.test";
        settings["Legal:Controller:ReaNumber"] = "MI-1234567";
        var configured = Service(settings);

        Assert.Contains("dpo@test-rentals.test", configured.GetText(LegalDocumentKind.Privacy, "it")!.Html);
        Assert.Contains("numero REA MI-1234567", configured.GetText(LegalDocumentKind.Tos, "it")!.Html);
    }

    // ─── Retention: the periods in force, or the honest "none" ────────────────────────────────────

    [Fact]
    public void GetText_NoRetentionConfigured_SaysThatNothingIsDeletedAutomatically()
    {
        var service = Service(LegalTextFixtures.CompleteConfiguration());

        foreach (var kind in new[] { LegalDocumentKind.Privacy, LegalDocumentKind.Dpa })
        {
            var italian = service.GetText(kind, "it")!.Html;
            var english = service.GetText(kind, "en")!.Html;

            Assert.Contains("nessun periodo applicato", italian);
            Assert.Contains("il Servizio non cancella nulla in automatico", italian);
            Assert.Contains("no period applied", english);
            Assert.Contains("the Service does not delete anything automatically", english);
            Assert.DoesNotMatch(@"\d+ anni", italian);
        }
    }

    [Fact]
    public void GetText_RetentionConfiguredWithSource_ShowsTheConfiguredPeriodOnly()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Gdpr:Retention:AlloggiatiData:Years"] = "5";
        settings["Gdpr:Retention:AlloggiatiData:Source"] = "decision of the product owner";
        settings["Gdpr:Retention:DocumentScans:Months"] = "1";
        settings["Gdpr:Retention:DocumentScans:Days"] = "15";
        settings["Gdpr:Retention:DocumentScans:Source"] = "decision of the product owner";
        // A period without a source is not applied by the retention job, so the text must not announce it.
        settings["Gdpr:Retention:FiscalData:Years"] = "10";

        var service = Service(settings);
        var italian = service.GetText(LegalDocumentKind.Dpa, "it")!.Html;
        var english = service.GetText(LegalDocumentKind.Dpa, "en")!.Html;

        Assert.Contains("5 anni dopo il check-out del soggiorno", italian);
        Assert.Contains("1 mese, 15 giorni dopo l'ultimo check-out", italian.Replace("&#39;", "'"));
        Assert.Contains("5 years after the check-out of the stay", english);
        Assert.Contains("1 month, 15 days after the guest", english.Replace("&#39;", "'"));
        Assert.DoesNotContain("10 anni", italian);
        Assert.DoesNotContain("10 years", english);
    }

    [Fact]
    public void GetText_RetentionValueNotANumber_ClaimsNoPeriodInsteadOfFailing()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Gdpr:Retention:AlloggiatiData:Years"] = "five";
        settings["Gdpr:Retention:AlloggiatiData:Source"] = "decision of the product owner";

        var html = Service(settings).GetText(LegalDocumentKind.Dpa, "it")!.Html;

        Assert.Contains("nessun periodo applicato", html);
    }

    // ─── Structure: Italian and English say the same, in the same order ───────────────────────────

    [Theory]
    [InlineData("tos")]
    [InlineData("privacy")]
    [InlineData("dpa")]
    public void Documents_ItalianAndEnglish_HaveTheSameSections(string folder)
    {
        var italian = ReadRaw(folder, "it");
        var english = ReadRaw(folder, "en");

        Assert.Equal(Headings(italian), Headings(english));
        Assert.Equal(ParagraphNumbers(italian), ParagraphNumbers(english));
        Assert.Equal(Count(italian, "<li>"), Count(english, "<li>"));
        Assert.Equal(Count(italian, "<tr>"), Count(english, "<tr>"));
        Assert.Equal(Count(italian, "<table>"), Count(english, "<table>"));
        Assert.Equal(Count(italian, "<ul>"), Count(english, "<ul>"));
        Assert.Equal(
            LegalDocumentTemplate.TokensIn(italian).Order().ToArray(),
            LegalDocumentTemplate.TokensIn(english).Order().ToArray());
        Assert.Equal(Links(italian), Links(english));
    }

    [Theory]
    [InlineData("tos")]
    [InlineData("privacy")]
    [InlineData("dpa")]
    public void Documents_Sections_AreNumberedWithoutGapsOrDuplicates(string folder)
    {
        foreach (var language in Languages)
        {
            var numbers = ParagraphNumbers(ReadRaw(folder, language));
            var sections = Headings(ReadRaw(folder, language));

            Assert.Equal(Enumerable.Range(1, sections.Count).Select(n => n.ToString()), sections);
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
            // Every article's paragraphs are consecutive from .1: no missing "3.4" between "3.3" and "3.5".
            foreach (var group in numbers.GroupBy(n => n.Split('.')[0]))
            {
                var minor = group.Select(n => n.Split('.').Length > 1 ? int.Parse(n.Split('.')[1]) : 0).Where(m => m > 0).ToList();
                Assert.Equal(Enumerable.Range(1, minor.Count), minor);
            }
        }
    }

    [Theory]
    [InlineData("tos")]
    [InlineData("dpa")]
    public void Documents_InternalArticleReferences_PointToExistingClauses(string folder)
    {
        // "art. 15" or "art. 8.4" inside the document must exist: a renumbering that leaves a stale reference fails here.
        // References to the GDPR, the civil code, the consumer code and the other documents are not internal.
        foreach (var language in Languages)
        {
            var raw = ReadRaw(folder, language);
            var existing = Headings(raw).Concat(ParagraphNumbers(raw)).ToHashSet();

            var references = Regex.Matches(
                raw,
                @"\bart\. (?<n>\d+(?:\.\d+)?)(?!\d)(?!.{0,40}?(?:GDPR|del codice civile|del Codice del consumo|of the Italian|dei Termini|of the Terms))");

            Assert.NotEmpty(references);
            foreach (Match reference in references)
                Assert.Contains(reference.Groups["n"].Value, existing);
        }
    }

    [Fact]
    public void Terms_SpecificApprovalList_NamesOnlyExistingClauses()
    {
        // Arts. 1341-1342 c.c.: the clauses the Customer approves specifically are listed by number (ToS 17.6).
        foreach (var language in Languages)
        {
            var raw = ReadRaw("tos", language);
            var existing = Headings(raw).Concat(ParagraphNumbers(raw)).ToHashSet();
            var approval = Regex.Match(raw, @"<p><strong>17\.6</strong>(?<list>.*?)</p>", RegexOptions.Singleline).Groups["list"].Value;
            // After the colon the list is "2.2 (description); 3.6 and 3.7 (description); …": the descriptions hold no digits.
            var list = approval[(approval.IndexOf(':') + 1)..];
            var listed = Regex.Matches(list, @"(?<![\d.])(?<n>\d+(?:\.\d+)?)(?![\d])").Select(m => m.Groups["n"].Value).ToList();

            Assert.True(listed.Count >= 12, $"{language}: the approval list should name the onerous clauses, found {listed.Count}");
            Assert.All(listed, number => Assert.Contains(number, existing));
            // The clauses that limit liability, suspend, withdraw, change unilaterally and fix the forum must be on the list.
            foreach (var required in new[] { "3.6", "6.6", "12", "13.3", "14", "15.1", "16.2" })
                Assert.Contains(required, listed);
        }
    }

    [Theory]
    [InlineData("tos")]
    [InlineData("privacy")]
    [InlineData("dpa")]
    public void Documents_EveryFile_CarriesTheDraftNoticeAsMetadata(string folder)
    {
        var italian = ReadRaw(folder, "it").TrimStart();
        var english = ReadRaw(folder, "en").TrimStart();

        Assert.StartsWith("<!--", italian);
        Assert.Contains("Bozza redatta da un agente AI su incarico del PO: richiede revisione di un legale prima dell'uso in produzione.", italian);
        Assert.StartsWith("<!--", english);
        Assert.Contains("Draft written by an AI agent on behalf of the product owner: it requires review by a lawyer before use in production.", english);
        Assert.Contains(LegalTextFixtures.DraftVersion, italian);
        Assert.Contains(LegalTextFixtures.DraftVersion, english);
    }

    [Theory]
    [InlineData("tos")]
    [InlineData("privacy")]
    [InlineData("dpa")]
    public void Documents_EveryToken_IsKnownAndEveryCatalogueTierIsInTheTerms(string folder)
    {
        var known = LegalVariables.Create(LegalConfiguration(LegalTextFixtures.CompleteConfiguration()), "it").Tokens.ToHashSet();

        foreach (var language in Languages)
        {
            var tokens = LegalDocumentTemplate.TokensIn(ReadRaw(folder, language));
            Assert.DoesNotContain(tokens, token => !known.Contains(token));
        }

        if (folder != "tos")
            return;

        var termsTokens = LegalDocumentTemplate.TokensIn(ReadRaw("tos", "it"));
        foreach (var plan in PlanCatalog.All)
        {
            Assert.Contains($"Plans.{plan.Tier}.Name", termsTokens);
            Assert.Contains($"Plans.{plan.Tier}.MaxProperties", termsTokens);
            Assert.Contains($"Plans.{plan.Tier}.PriceMonthly", termsTokens);
        }
    }

    [Fact]
    public void Documents_DoNotDuplicateTheSubprocessorList_TheyLinkTheDynamicPage()
    {
        foreach (var folder in new[] { "tos", "privacy", "dpa" })
        {
            foreach (var language in Languages)
            {
                var raw = ReadRaw(folder, language);

                Assert.Contains("{{Site.BaseUrl}}/legale/sub-responsabili", raw);
                // Whole words: "Export" is not the provider "Expo".
                foreach (var provider in new[] { "Supabase", "Auth0", "Resend", "Expo", "Railway", "Vercel", "DeepSeek", "SendGrid" })
                    Assert.DoesNotMatch($@"\b{provider}\b", raw);
            }
        }
    }

    [Fact]
    public void Documents_CitedStatutes_AreOnlyTheVerifiedOnes()
    {
        // Every statute the drafts cite was checked against a public source (see the runbook): a new reference needs a
        // verification first, so this list changes only together with the runbook's table of verified sources.
        var allowedActs = new HashSet<string> { "206/2005", "2016/679", "2019/1150" };
        var allowedCivilCodeArticles = new HashSet<string> { "1229", "1341", "1342" };

        foreach (var folder in new[] { "tos", "privacy", "dpa" })
        {
            foreach (var language in Languages)
            {
                var text = ReadRaw(folder, language);

                foreach (Match act in Regex.Matches(text, @"(?:D\.Lgs\.|Legislative Decree|Regolamento \(UE\)|Regulation \(EU\))\s*(?<ref>\d+/\d+)"))
                    Assert.Contains(act.Groups["ref"].Value, allowedActs);

                foreach (Match article in Regex.Matches(text, @"(?:artt?\.|arts?\.)\s*(?<a>\d+)(?:\s*(?:e|and)\s*(?<b>\d+))?\s*(?:del codice civile|of the Italian Civil Code)"))
                {
                    Assert.Contains(article.Groups["a"].Value, allowedCivilCodeArticles);
                    if (article.Groups["b"].Success)
                        Assert.Contains(article.Groups["b"].Value, allowedCivilCodeArticles);
                }
            }
        }

        var terms = ReadRaw("tos", "it");
        Assert.Contains("art. 3 del Codice del consumo (D.Lgs. 206/2005)", terms);
        Assert.Contains("art. 1229 del codice civile", terms);
        Assert.Contains("artt. 1341 e 1342 del codice civile", terms);
        Assert.Contains("Regolamento (UE) 2019/1150", terms);
        Assert.Contains("Regulation (EU) 2019/1150", ReadRaw("tos", "en"));
    }

    // ─── Activation: the deploy never forces the re-acceptance by itself ──────────────────────────

    [Fact]
    public void AppSettings_DefaultVersions_DoNotActivateTheDrafts()
    {
        // The drafts ship with the API but are activated by configuration (Railway: Legal__Documents__{Tos|Privacy|Dpa}__Version):
        // changing a version locks every host out until they accept it again (PL-02), so the deploy must not do it.
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(LegalTextFixtures.RepositoryRoot(), "Casazen.Web", "appsettings.json")));
        var documents = settings.RootElement.GetProperty("Legal").GetProperty("Documents");

        foreach (var key in new[] { "Tos", "Privacy", "Dpa" })
            Assert.NotEqual(LegalTextFixtures.DraftVersion, documents.GetProperty(key).GetProperty("Version").GetString());
    }

    [Fact]
    public void AppSettings_ProposedTerms_RespectTheMinimumNoticesOfRegulation20191150()
    {
        // Regulation (EU) 2019/1150, if it applies to the Service: 15 days to notify changes, 30 days to terminate.
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(LegalTextFixtures.RepositoryRoot(), "Casazen.Web", "appsettings.json")));
        var terms = settings.RootElement.GetProperty("Legal").GetProperty("Terms");

        Assert.True(terms.GetProperty("ChangeNoticeDays").GetInt32() >= 15);
        Assert.True(terms.GetProperty("TerminationNoticeDays").GetInt32() >= 30);
        // Company data and the governing court have no default: a lawyer or the product owner provides them.
        foreach (var key in new[] { "Name", "Address", "VatId", "Pec", "PrivacyEmail" })
            Assert.Equal(string.Empty, settings.RootElement.GetProperty("Legal").GetProperty("Controller").GetProperty(key).GetString());
        Assert.Equal(string.Empty, terms.GetProperty("GoverningCourt").GetString());
    }

    [Fact]
    public void WebProject_ShipsTheLegalDocumentsWithTheApi()
    {
        var project = File.ReadAllText(Path.Combine(LegalTextFixtures.RepositoryRoot(), "Casazen.Web", "Casazen.Web.csproj"));

        Assert.Contains(@"LegalDocuments\**\*.html", project);
        foreach (var folder in new[] { "tos", "privacy", "dpa" })
        {
            foreach (var language in Languages)
                Assert.True(File.Exists(Path.Combine(LegalTextFixtures.ShippedRoot(), folder, $"{LegalTextFixtures.DraftVersion}.{language}.html")));
        }
    }

    [Fact]
    public void Evaluate_TheDraftVersionIsActivated_HostsMustAcceptAgainOnlyBecauseTheVersionChanged()
    {
        var accepted = Snapshot("2026-06-v1");

        // Before the activation (current default): the hosts who accepted 2026-06-v1 stay in.
        var beforeSettings = LegalTextFixtures.CompleteConfiguration();
        foreach (var key in new[] { "Tos", "Privacy", "Dpa" })
            beforeSettings[$"Legal:Documents:{key}:Version"] = "2026-06-v1";
        var before = Service(beforeSettings);
        Assert.True(HostOnboardingGate.Evaluate(accepted, before).ConsentsAccepted);

        // Activation (versions set): the same hosts must accept the new version, and only then are back in.
        var after = Service(LegalTextFixtures.CompleteConfiguration());
        Assert.False(HostOnboardingGate.Evaluate(accepted, after).ConsentsAccepted);
        Assert.True(HostOnboardingGate.Evaluate(Snapshot(LegalTextFixtures.DraftVersion), after).ConsentsAccepted);
    }

    [Fact]
    public void Evaluate_ControllerDataOrCurrentTextChange_SameVersion_NoNewAcceptance()
    {
        var accepted = Snapshot(LegalTextFixtures.DraftVersion);

        var original = Service(LegalTextFixtures.CompleteConfiguration());
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Controller:Name"] = "Altra Denominazione S.p.A.";
        settings["Legal:Controller:Address"] = "Via Nuova 2, 00100 Roma (RM)";
        settings["Legal:Terms:ChangeNoticeDays"] = "45";
        var changed = Service(settings);

        Assert.True(HostOnboardingGate.Evaluate(accepted, original).ConsentsAccepted);
        Assert.True(HostOnboardingGate.Evaluate(accepted, changed).ConsentsAccepted);
        Assert.Contains("Altra Denominazione S.p.A.", changed.GetText(LegalDocumentKind.Privacy, "it")!.Html);
    }

    [Fact]
    public void Evaluate_DocumentNotPublishedForMissingValues_VersionStillGatesTheHosts()
    {
        // Fail-closed hides the text, not the consent: the gate follows the configured version, and the health check
        // and the startup log tell the product owner what to set before announcing the new version.
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("Legal:Controller:Name");
        var service = Service(settings);

        Assert.False(service.GetPublication(LegalDocumentKind.Tos).IsPublished);
        Assert.False(HostOnboardingGate.Evaluate(Snapshot("2026-06-v1"), service).ConsentsAccepted);
        Assert.True(HostOnboardingGate.Evaluate(Snapshot(LegalTextFixtures.DraftVersion), service).ConsentsAccepted);
    }

    // ─── Health check: degraded, with the variables to set and never their values ─────────────────

    [Fact]
    public async Task CheckHealthAsync_AllDocumentsPublished_IsHealthy()
    {
        var check = new LegalDocumentsHealthCheck(Service(LegalTextFixtures.CompleteConfiguration()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_DocumentsNotPublished_IsDegradedAndNamesVariablesNotValues()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings.Remove("Legal:Controller:Pec");
        settings["Legal:Controller:Name"] = "Secret Company Name S.r.l.";
        settings["Legal:Documents:Dpa:Version"] = "2026-06-v1";
        var check = new LegalDocumentsHealthCheck(Service(settings));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Legal__Controller__Pec", result.Description);
        Assert.Contains("Tos 2026-10-v1", result.Description);
        Assert.Contains("Dpa 2026-06-v1: no text file for this version", result.Description);
        Assert.DoesNotContain("Secret Company Name", result.Description);
        Assert.DoesNotContain("privacy@test-rentals.test", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_DefaultConfiguration_IsDegradedUntilTheTextsAreActivated()
    {
        var check = new LegalDocumentsHealthCheck(Service(new Dictionary<string, string?>
        {
            ["Legal:ContentPath"] = LegalTextFixtures.ShippedRoot(),
            ["Legal:Documents:Tos:Version"] = "2026-06-v1",
            ["Legal:Documents:Privacy:Version"] = "2026-06-v1",
            ["Legal:Documents:Dpa:Version"] = "2026-06-v1",
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("no text file for this version", result.Description);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static LegalDocumentService Service(Dictionary<string, string?> settings) =>
        new(LegalConfiguration(settings), NullLogger<LegalDocumentService>.Instance);

    private static IConfiguration LegalConfiguration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static LegalVariables Variables(params (string Key, string Value)[] overrides)
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        foreach (var (key, value) in overrides)
            settings[key] = value;
        return LegalVariables.Create(LegalConfiguration(settings), "it");
    }

    private static UserAuthorizationSnapshot Snapshot(string acceptedVersion) =>
        new(
            Exists: true,
            IsActive: true,
            Role: UserRole.PropertyOwner,
            SupplierOrgId: null,
            Memberships: [],
            OrgId: Guid.NewGuid(),
            OnboardingCompletedAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            AcceptedConsents: new HashSet<string>
            {
                UserAuthorizationSnapshot.ConsentKey(ConsentType.Tos, acceptedVersion),
                UserAuthorizationSnapshot.ConsentKey(ConsentType.Privacy, acceptedVersion),
                UserAuthorizationSnapshot.ConsentKey(ConsentType.Dpa, acceptedVersion),
            });

    private static string ReadRaw(string folder, string language) =>
        File.ReadAllText(Path.Combine(LegalTextFixtures.ShippedRoot(), folder, $"{LegalTextFixtures.DraftVersion}.{language}.html"));

    private static List<string> Headings(string raw) =>
        Regex.Matches(raw, @"<h2>(?<n>\d+)\.").Select(m => m.Groups["n"].Value).ToList();

    private static List<string> ParagraphNumbers(string raw) =>
        Regex.Matches(raw, @"<p><strong>(?<n>\d+(?:\.\d+)*)</strong>").Select(m => m.Groups["n"].Value).ToList();

    private static List<string> Links(string raw) =>
        Regex.Matches(raw, @"href=""(?<h>[^""]+)""").Select(m => m.Groups["h"].Value).ToList();

    private static int Count(string raw, string tag) => Regex.Matches(raw, Regex.Escape(tag)).Count;
}
