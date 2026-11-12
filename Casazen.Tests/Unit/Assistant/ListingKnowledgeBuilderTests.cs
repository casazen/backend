using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.SiteProfile;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02 (NF-66, AC-NF66-1): the facts of a property are built from what the host entered, in Italian or English, and nothing else. They are pure and never stored, they
/// never read a Wi-Fi password or an access code, and they never say that something is absent.
/// </summary>
public class ListingKnowledgeBuilderTests
{
    private static Property Complete()
    {
        var property = new Property
        {
            OrgId = Guid.NewGuid(),
            Name = "Trullo dei Sogni",
            Address = "Via degli Ulivi 12",
            City = "Ostuni",
        };
        AssistantTestKit.FullListing(property);
        return property;
    }

    private static SitePropertyProfileDocument Profile(Property property) => SitePropertyProfileJson.Parse(property.SiteProfileJson);

    private static string[] Texts(IEnumerable<ListingFact> facts, KnowledgeTopic topic) =>
        [.. facts.Where(fact => fact.Topic == topic).Select(fact => fact.Text)];

    [Fact]
    public void Build_ACompleteListing_GivesTheFactsOfEveryTopicInItalian_WithTheFieldTheyCameFrom()
    {
        var property = Complete();

        var facts = ListingKnowledgeBuilder.Build(property, Profile(property), "it");

        Assert.Equal(
            [
                "Il check-in è possibile dalle 15:00 alle 20:30.",
                "Come funziona il check-in: In autonomia, con cassetta a codice",
            ],
            Texts(facts, KnowledgeTopic.CheckIn));
        Assert.Equal(["Il check-out va fatto entro le 10:00."], Texts(facts, KnowledgeTopic.CheckOut));
        Assert.Equal(["Nella struttura c'è il Wi-Fi."], Texts(facts, KnowledgeTopic.WiFi));
        Assert.Equal(["Parcheggio: Parcheggio gratuito, Garage."], Texts(facts, KnowledgeTopic.Parking));
        Assert.Equal(
            [
                "Regole della casa: Niente scarpe in casa.",
                "Nota sulle regole: Rispettate il vicinato",
                "Si può fumare solo all'aperto.",
                "Feste ed eventi non sono ammessi.",
                "I bambini sono benvenuti.",
            ],
            Texts(facts, KnowledgeTopic.HouseRules));
        Assert.Equal(["Gli orari di silenzio vanno dalle 22:00 alle 07:30."], Texts(facts, KnowledgeTopic.Hours));
        Assert.Equal(["Servizi disponibili: Wi-Fi, Aria condizionata, Piscina, Parcheggio gratuito, Garage."], Texts(facts, KnowledgeTopic.Amenities));
        Assert.Equal(
            [
                "L'indirizzo è Via degli Ulivi 12, Ostuni.",
                "Zona: Campagna di Ostuni",
                "Nelle vicinanze: 10 minuti dal mare",
                "Piano: Piano terra",
                "Spazi esterni: Giardino con uliveto",
            ],
            Texts(facts, KnowledgeTopic.Directions));
        Assert.Equal(["Gli animali sono ammessi su richiesta."], Texts(facts, KnowledgeTopic.Pets));

        // The screen says «from your listing» and points at the field.
        Assert.Equal(ListingFactSources.CheckInWindow, facts.First(f => f.Topic == KnowledgeTopic.CheckIn).SourceField);
        Assert.Equal(ListingFactSources.PetsPolicy, facts.Single(f => f.Topic == KnowledgeTopic.Pets).SourceField);
        Assert.Equal(ListingFactSources.QuietHours, facts.Single(f => f.Topic == KnowledgeTopic.Hours).SourceField);
        Assert.All(facts, fact => Assert.False(string.IsNullOrWhiteSpace(fact.SourceField)));

        // Facts come in the order of the topics.
        Assert.Equal(facts.Select(f => (int)f.Topic).Order(), facts.Select(f => (int)f.Topic));
    }

    [Fact]
    public void Build_InEnglish_UsesTheEnglishFrameAndTheEnglishTextsTheHostWrote_ItalianWhenItWroteNone()
    {
        var property = Complete();

        var facts = ListingKnowledgeBuilder.Build(property, Profile(property), "en");

        Assert.Equal(
            ["Check-in is possible from 15:00 to 20:30.", "How check-in works: Self check-in with a key box"],
            Texts(facts, KnowledgeTopic.CheckIn));
        Assert.Equal(["Check-out is by 10:00."], Texts(facts, KnowledgeTopic.CheckOut));
        Assert.Equal(["Parking: Free parking, Garage."], Texts(facts, KnowledgeTopic.Parking));
        Assert.Equal(["Quiet hours are from 22:00 to 07:30."], Texts(facts, KnowledgeTopic.Hours));
        Assert.Equal(["Pets are allowed on request."], Texts(facts, KnowledgeTopic.Pets));

        // The note on the rules has no English text: the Italian one is used, in the English frame.
        Assert.Contains("Note on the rules: Rispettate il vicinato", Texts(facts, KnowledgeTopic.HouseRules));
        Assert.Contains("Area: Ostuni countryside", Texts(facts, KnowledgeTopic.Directions));
        Assert.Contains("Nearby: 10 minuti dal mare", Texts(facts, KnowledgeTopic.Directions));
        Assert.Contains("The address is Via degli Ulivi 12, Ostuni.", Texts(facts, KnowledgeTopic.Directions));
        Assert.Contains("Amenities: Wi-Fi, Air conditioning, Pool, Free parking, Garage.", Texts(facts, KnowledgeTopic.Amenities));
    }

    [Theory]
    [InlineData(null, "it")]
    [InlineData("", "it")]
    [InlineData("it", "it")]
    [InlineData("fr", "it")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en")]
    public void ResolveLanguage_IsEnglishForEnglish_ItalianForEverythingElse(string? asked, string expected) =>
        Assert.Equal(expected, ListingKnowledgeBuilder.ResolveLanguage(asked));

    [Fact]
    public void Build_NeverReadsAWifiPassword_OnlyThatTheAmenityIsThere()
    {
        var property = Complete();

        var all = string.Join(" | ", ListingKnowledgeBuilder.Build(property, Profile(property), "it").Select(f => f.Text))
                  + string.Join(" | ", ListingKnowledgeBuilder.Build(property, Profile(property), "en").Select(f => f.Text));

        // The listing has no password to read, and the builder has no way to be given one or an access code: it takes a property and its profile and nothing else.
        Assert.DoesNotContain("password", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("codice", all.Replace("con cassetta a codice", string.Empty), StringComparison.OrdinalIgnoreCase);
        var build = typeof(ListingKnowledgeBuilder).GetMethod(nameof(ListingKnowledgeBuilder.Build))!;
        Assert.Equal(
            new[] { typeof(Property), typeof(SitePropertyProfileDocument), typeof(string) },
            build.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void Build_NeverSaysThatSomethingIsAbsent_ATopicWithNothingEnteredHasNoFact()
    {
        // A bare property: the host ticked no amenity and answered no question. «No Wi-Fi» is a claim the listing does not make.
        var bare = new Property { OrgId = Guid.NewGuid(), Name = "Casa", Address = "Via Roma 1", City = "Lecce" };

        var facts = ListingKnowledgeBuilder.Build(bare, null, "it");

        Assert.Equal([KnowledgeTopic.Directions], facts.Select(f => f.Topic).Distinct());
        Assert.Equal(["L'indirizzo è Via Roma 1, Lecce."], facts.Select(f => f.Text));
        Assert.DoesNotContain(facts, f => f.Text.Contains("non", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            [KnowledgeTopic.CheckIn, KnowledgeTopic.CheckOut, KnowledgeTopic.WiFi, KnowledgeTopic.Parking, KnowledgeTopic.HouseRules, KnowledgeTopic.Hours,
             KnowledgeTopic.Amenities, KnowledgeTopic.Pets, KnowledgeTopic.Contacts],
            ListingKnowledgeBuilder.TopicsWithoutData(facts.Select(f => f.Topic)));
    }

    [Fact]
    public void Build_AHalfOpenCheckInWindow_SaysOnlyWhatIsKnown()
    {
        var fromOnly = new Property { CheckInFrom = new TimeOnly(16, 5), Address = "x", City = "y" };
        var toOnly = new Property { CheckInTo = new TimeOnly(23, 0), Address = "x", City = "y" };

        Assert.Equal(["Il check-in è possibile a partire dalle 16:05."], Texts(ListingKnowledgeBuilder.Build(fromOnly, null, "it"), KnowledgeTopic.CheckIn));
        Assert.Equal(["Check-in is possible from 16:05 onwards."], Texts(ListingKnowledgeBuilder.Build(fromOnly, null, "en"), KnowledgeTopic.CheckIn));
        Assert.Equal(["Il check-in è possibile fino alle 23:00."], Texts(ListingKnowledgeBuilder.Build(toOnly, null, "it"), KnowledgeTopic.CheckIn));
    }

    [Theory]
    [InlineData(PetsPolicy.No, "Gli animali non sono ammessi.", "Pets are not allowed.")]
    [InlineData(PetsPolicy.OnRequest, "Gli animali sono ammessi su richiesta.", "Pets are allowed on request.")]
    [InlineData(PetsPolicy.Yes, "Gli animali sono benvenuti.", "Pets are welcome.")]
    public void Build_Pets_FollowTheAnswerOfTheWizard(PetsPolicy policy, string italian, string english)
    {
        var property = new Property { PetsPolicy = policy, Address = "x", City = "y" };

        Assert.Equal([italian], Texts(ListingKnowledgeBuilder.Build(property, null, "it"), KnowledgeTopic.Pets));
        Assert.Equal([english], Texts(ListingKnowledgeBuilder.Build(property, null, "en"), KnowledgeTopic.Pets));
    }

    [Fact]
    public void Build_APropertyWithNoAnswerAboutPets_FallsBackToTheAmenity_OrSaysNothing()
    {
        var withAmenity = new Property { Amenities = [PropertyAmenity.PetFriendly], Address = "x", City = "y" };
        var without = new Property { Address = "x", City = "y" };

        var fact = Assert.Single(ListingKnowledgeBuilder.Build(withAmenity, null, "it"), f => f.Topic == KnowledgeTopic.Pets);
        Assert.Equal(ListingFactSources.Amenities, fact.SourceField);
        Assert.DoesNotContain(ListingKnowledgeBuilder.Build(without, null, "it"), f => f.Topic == KnowledgeTopic.Pets);
    }

    [Fact]
    public void Build_Children_TheStructuredAnswerWins_TheOlderSiteProfileAnswerIsTheFallback()
    {
        var structured = new Property { ChildrenPolicy = ChildrenPolicy.OnRequest, ChildrenWelcome = true, Address = "x", City = "y" };
        var older = new Property { ChildrenWelcome = false, Address = "x", City = "y" };

        Assert.Equal(["I bambini sono ammessi su richiesta."], Texts(ListingKnowledgeBuilder.Build(structured, null, "it"), KnowledgeTopic.HouseRules));
        Assert.Equal(["La casa non è adatta ai bambini."], Texts(ListingKnowledgeBuilder.Build(older, null, "it"), KnowledgeTopic.HouseRules));
    }

    [Fact]
    public void TopicsWithoutData_IsEveryTopicButTheFaqs_MinusTheOnesThatHaveData()
    {
        Assert.DoesNotContain(KnowledgeTopic.Faq, ListingKnowledgeBuilder.Topics);
        Assert.Equal(10, ListingKnowledgeBuilder.Topics.Count);

        Assert.Equal(
            ListingKnowledgeBuilder.Topics.Where(t => t is not (KnowledgeTopic.Parking or KnowledgeTopic.Contacts)),
            ListingKnowledgeBuilder.TopicsWithoutData([KnowledgeTopic.Parking, KnowledgeTopic.Contacts, KnowledgeTopic.Faq]));
        Assert.Empty(ListingKnowledgeBuilder.TopicsWithoutData(ListingKnowledgeBuilder.Topics));
    }
}
