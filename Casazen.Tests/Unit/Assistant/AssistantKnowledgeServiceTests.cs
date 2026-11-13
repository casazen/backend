using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02 (NF-66, AC-NF66-1, AC-NF67-1): the knowledge base of a property: the facts of the listing and the entries of the property and of the organization, the topics
/// that have nothing, the retrieval the draft pipeline uses, and the version of the instructions that grows with every change in the same save.
/// </summary>
public class AssistantKnowledgeServiceTests
{
    private const string User = "auth0|host";

    private static readonly Guid Org = Guid.Parse("11111111-aaaa-4aaa-8aaa-000000000001");
    private static readonly Guid OtherOrg = Guid.Parse("22222222-bbbb-4bbb-8bbb-000000000002");

    private static async Task<int> VersionAsync(AppDbContext db) => (await AssistantTestKit.Rules(db).GetAsync(Org)).InstructionVersion;

    // ─── the view ───

    [Fact]
    public async Task GetViewAsync_GivesTheFactsOfTheListing_TheEntriesOfThePropertyAndOfTheOrganization_AndNoGapWhenEveryTopicHasData()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org, configure: AssistantTestKit.FullListing);
        var knowledge = AssistantTestKit.Knowledge(db);
        var own = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.WiFi, "Rete TrulloOstuni, password Zefirino-2026"), User);
        var organizationWide = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Contacts, "Per un'emergenza chiama Marco."), User);

        var view = await knowledge.GetViewAsync(Org, property.Id, "it");

        Assert.Equal(property.Id, view.PropertyId);
        Assert.Equal("it", view.Language);
        Assert.Contains(view.Facts, fact => fact.Topic == KnowledgeTopic.CheckIn && fact.SourceField == ListingFactSources.CheckInWindow);
        Assert.Equal(2, view.Entries.Count);
        var shownOwn = Assert.Single(view.Entries, e => e.Id == own.Id);
        Assert.False(shownOwn.Inherited);
        Assert.Equal(property.Id, shownOwn.PropertyId);
        Assert.Equal("Rete TrulloOstuni, password Zefirino-2026", shownOwn.Text);
        var shownInherited = Assert.Single(view.Entries, e => e.Id == organizationWide.Id);
        Assert.True(shownInherited.Inherited);
        Assert.Null(shownInherited.PropertyId);

        // The listing covers every topic but the contacts, which the entry of the organization covers.
        Assert.Empty(view.TopicsWithoutData);
    }

    [Fact]
    public async Task GetViewAsync_TheTopicsWithNeitherAFactNorAnActiveEntry_AreTheGaps_AnInactiveEntryDoesNotFillOne()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var knowledge = AssistantTestKit.Knowledge(db);
        await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.Parking, "Parcheggio in strada.", isActive: false), User);
        await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.WiFi, "Rete TrulloOstuni."), User);

        var view = await knowledge.GetViewAsync(Org, property.Id, null);

        // The bare property has only its address: Directions has a fact, WiFi an entry, Parking an entry that is switched off.
        Assert.Equal(
            [KnowledgeTopic.CheckIn, KnowledgeTopic.CheckOut, KnowledgeTopic.Parking, KnowledgeTopic.HouseRules, KnowledgeTopic.Hours, KnowledgeTopic.Amenities,
             KnowledgeTopic.Pets, KnowledgeTopic.Contacts],
            view.TopicsWithoutData);
        Assert.Equal(2, view.Entries.Count);
        Assert.Contains(view.Entries, e => e.Topic == KnowledgeTopic.Parking && !e.IsActive);
    }

    [Fact]
    public async Task GetViewAsync_InEnglish_BuildsTheFactsInEnglish()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org, configure: AssistantTestKit.FullListing);

        var view = await AssistantTestKit.Knowledge(db).GetViewAsync(Org, property.Id, "en");

        Assert.Equal("en", view.Language);
        Assert.Contains(view.Facts, fact => fact.Text == "Check-out is by 10:00.");
    }

    // ─── the entries ───

    [Fact]
    public async Task CreateEntryAsync_WritesTheEntryUnderTheOrganization_AndGrowsTheVersionInTheSameSave()
    {
        var store = AssistantTestKit.NewStore();
        await using var db = AssistantTestKit.NewDb(store);
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);

        var created = await AssistantTestKit.Knowledge(db).CreateEntryAsync(Org, property.Id, AssistantTestKit.Faq(), User);

        Assert.Equal(property.Id, created.PropertyId);
        Assert.Equal(KnowledgeSource.Custom, created.Source);
        Assert.True(created.IsActive);
        Assert.False(created.Inherited);
        await using var after = AssistantTestKit.NewDb(store);
        var row = await after.PropertyKnowledgeEntries.AsNoTracking().SingleAsync();
        Assert.Equal(Org, row.OrgId);
        Assert.Equal(User, row.CreatedByUserId);
        Assert.Equal(AssistantTestKit.Now.UtcDateTime, row.CreatedAt);
        Assert.Equal("Posso arrivare prima?", row.Title);
        Assert.Equal(2, await VersionAsync(after));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task CreateEntryAsync_ATextOfOneToAThousandCharacters_IsAccepted(int length)
    {
        await using var db = AssistantTestKit.NewDb();

        var created = await AssistantTestKit.Knowledge(db).CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Faq, new string('a', length)), User);

        Assert.Equal(length, created.Text.Length);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("toolong")]
    [InlineData("notopic")]
    [InlineData("nokind")]
    [InlineData("faqwithoutquestion")]
    [InlineData("titletoolong")]
    [InlineData("titlewithnewline")]
    [InlineData("languagetoolong")]
    [InlineData("languageuppercase")]
    [InlineData("undefinedtopic")]
    public async Task CreateEntryAsync_AnInvalidEntry_Is422_AndWritesNothing_NotEvenTheVersion(string what)
    {
        await using var db = AssistantTestKit.NewDb();
        var input = what switch
        {
            "empty" => AssistantTestKit.Fact(KnowledgeTopic.Parking, string.Empty),
            "blank" => AssistantTestKit.Fact(KnowledgeTopic.Parking, "   \n  "),
            "toolong" => AssistantTestKit.Fact(KnowledgeTopic.Parking, new string('a', 1001)),
            "notopic" => new KnowledgeEntryInput(null, KnowledgeEntryKind.Fact, null, "x", null, null),
            "nokind" => new KnowledgeEntryInput(KnowledgeTopic.Parking, null, null, "x", null, null),
            "faqwithoutquestion" => new KnowledgeEntryInput(KnowledgeTopic.Parking, KnowledgeEntryKind.Faq, "  ", "Gratis.", null, null),
            "titletoolong" => new KnowledgeEntryInput(KnowledgeTopic.Parking, KnowledgeEntryKind.Faq, new string('q', 201), "Gratis.", null, null),
            "titlewithnewline" => new KnowledgeEntryInput(KnowledgeTopic.Parking, KnowledgeEntryKind.Faq, "Dove\nparcheggio?", "Gratis.", null, null),
            "languagetoolong" => AssistantTestKit.Fact(KnowledgeTopic.Parking, "Gratis.", language: "ita"),
            "languageuppercase" => AssistantTestKit.Fact(KnowledgeTopic.Parking, "Gratis.", language: "IT"),
            _ => new KnowledgeEntryInput((KnowledgeTopic)99, KnowledgeEntryKind.Fact, null, "x", null, null),
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => AssistantTestKit.Knowledge(db).CreateEntryAsync(Org, null, input, User));

        Assert.Equal("assistant_entry_invalid", error.Code);
        Assert.Empty(await db.PropertyKnowledgeEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateEntryAsync_AFaqNeedsItsQuestion_AFactOrARuleDoesNot_AndALanguageIsATwoLetterCode()
    {
        await using var db = AssistantTestKit.NewDb();
        var knowledge = AssistantTestKit.Knowledge(db);

        var fact = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.HouseRules, "Niente feste.", language: "it", kind: KnowledgeEntryKind.Rule), User);
        var faq = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Faq(), User);

        Assert.Null(fact.Title);
        Assert.Equal("it", fact.Language);
        Assert.Equal(KnowledgeEntryKind.Rule, fact.Kind);
        Assert.Equal("Posso arrivare prima?", faq.Title);
    }

    [Fact]
    public async Task CreateEntryAsync_ForAPropertyThatIsNotTheOrganizations_IsNotFound_AndWritesNothing()
    {
        await using var db = AssistantTestKit.NewDb();
        var foreign = await AssistantTestKit.AddPropertyAsync(db, OtherOrg);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => AssistantTestKit.Knowledge(db).CreateEntryAsync(Org, foreign.Id, AssistantTestKit.Faq(), User));

        Assert.Equal("property_not_found", error.Code);
        Assert.Empty(await db.PropertyKnowledgeEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateEntryAsync_ChangesTheEntry_AndGrowsTheVersion_ASaveOfTheSameValuesChangesNothing()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var knowledge = AssistantTestKit.Knowledge(db);
        var created = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Faq(), User);
        var later = new FixedTimeProvider(AssistantTestKit.Now.AddHours(3));
        var editor = AssistantTestKit.Knowledge(db, later);

        // The same values: nothing changes, not the version and not the time.
        var same = await editor.UpdateEntryAsync(Org, property.Id, created.Id, AssistantTestKit.Faq(), User);
        Assert.Equal(created.UpdatedAt, same.UpdatedAt);
        Assert.Equal(2, await VersionAsync(db));

        var changed = await editor.UpdateEntryAsync(
            Org,
            property.Id,
            created.Id,
            new KnowledgeEntryInput(KnowledgeTopic.CheckIn, KnowledgeEntryKind.Faq, "Posso arrivare prima delle 15?", "Dalle 13, avvisando.", "it", false),
            User);

        Assert.Equal("Dalle 13, avvisando.", changed.Text);
        Assert.False(changed.IsActive);
        Assert.Equal("it", changed.Language);
        Assert.Equal(AssistantTestKit.Now.AddHours(3).UtcDateTime, changed.UpdatedAt);
        Assert.Equal(created.CreatedAt, changed.CreatedAt);
        Assert.Equal(3, await VersionAsync(db));
    }

    [Fact]
    public async Task UpdateEntryAsync_AnEntryThatCameFromALesson_CanBeEditedAndSwitchedOff_AndStaysTheLessons()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var lesson = new PropertyKnowledgeEntry
        {
            OrgId = Org,
            PropertyId = property.Id,
            Topic = KnowledgeTopic.Parking,
            Kind = KnowledgeEntryKind.Rule,
            Source = KnowledgeSource.Lesson,
            LessonId = Guid.NewGuid(),
            Text = "Il parcheggio è gratuito solo nel fine settimana.",
            CreatedByUserId = User,
        };
        db.PropertyKnowledgeEntries.Add(lesson);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var edited = await AssistantTestKit.Knowledge(db).UpdateEntryAsync(
            Org,
            property.Id,
            lesson.Id,
            AssistantTestKit.Fact(KnowledgeTopic.Parking, "Il parcheggio è gratuito il sabato e la domenica.", isActive: false, kind: KnowledgeEntryKind.Rule),
            User);

        Assert.Equal(KnowledgeSource.Lesson, edited.Source);
        Assert.Equal(lesson.LessonId, edited.LessonId);
        Assert.False(edited.IsActive);
        Assert.Equal("Il parcheggio è gratuito il sabato e la domenica.", edited.Text);
    }

    [Fact]
    public async Task DeleteEntryAsync_RemovesTheEntry_AndGrowsTheVersion()
    {
        await using var db = AssistantTestKit.NewDb();
        var knowledge = AssistantTestKit.Knowledge(db);
        var created = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Faq(), User);

        await knowledge.DeleteEntryAsync(Org, null, created.Id);

        Assert.Empty(await db.PropertyKnowledgeEntries.AsNoTracking().ToListAsync());
        Assert.Equal(3, await VersionAsync(db));
    }

    [Fact]
    public async Task AnEntryIsAlwaysAskedForThroughItsOwnProperty_AnotherPropertysOrTheOrganizationsOrThePropertysThroughTheOrganizationIsNotFound()
    {
        await using var db = AssistantTestKit.NewDb();
        var first = await AssistantTestKit.AddPropertyAsync(db, Org, "Trullo");
        var second = await AssistantTestKit.AddPropertyAsync(db, Org, "Masseria");
        var knowledge = AssistantTestKit.Knowledge(db);
        var ofFirst = await knowledge.CreateEntryAsync(Org, first.Id, AssistantTestKit.Faq(), User);
        var ofOrganization = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Faq("Dove si parcheggia?", "In piazza."), User);
        var version = await VersionAsync(db);
        var edit = AssistantTestKit.Faq(text: "Cambiato.");

        // Each attempt is the same 404 as an id that does not exist, and changes nothing.
        foreach (var attempt in new Func<Task>[]
                 {
                     () => knowledge.UpdateEntryAsync(Org, second.Id, ofFirst.Id, edit, User),
                     () => knowledge.DeleteEntryAsync(Org, second.Id, ofFirst.Id),
                     () => knowledge.UpdateEntryAsync(Org, first.Id, ofOrganization.Id, edit, User),
                     () => knowledge.DeleteEntryAsync(Org, first.Id, ofOrganization.Id),
                     () => knowledge.UpdateEntryAsync(Org, null, ofFirst.Id, edit, User),
                     () => knowledge.DeleteEntryAsync(Org, null, ofFirst.Id),
                     () => knowledge.UpdateEntryAsync(Org, first.Id, Guid.NewGuid(), edit, User),
                 })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal("assistant_entry_not_found", error.Code);
        }

        Assert.Equal(2, await db.PropertyKnowledgeEntries.AsNoTracking().CountAsync());
        Assert.Equal("Posso arrivare prima?", (await db.PropertyKnowledgeEntries.AsNoTracking().SingleAsync(e => e.Id == ofFirst.Id)).Title);
        Assert.Equal(version, await VersionAsync(db));
    }

    [Fact]
    public async Task ListOrganizationEntriesAsync_ListsOnlyTheEntriesOfTheOrganizationWithNoProperty()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var knowledge = AssistantTestKit.Knowledge(db);
        await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Faq(), User);
        var wide = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Faq("Si fuma?", "Solo fuori."), User);
        await knowledge.CreateEntryAsync(OtherOrg, null, AssistantTestKit.Faq("Altrove?", "Altrove."), User);

        var listed = await knowledge.ListOrganizationEntriesAsync(Org);

        Assert.Equal([wide.Id], listed.Select(e => e.Id));
        Assert.All(listed, e => Assert.False(e.Inherited));
    }

    // ─── what the draft pipeline retrieves ───

    [Fact]
    public async Task RetrieveAsync_NoTopics_IsNoFacts_NeverAllOfThem()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org, configure: AssistantTestKit.FullListing);
        var knowledge = AssistantTestKit.Knowledge(db);
        await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.Parking, "Gratis."), User);

        Assert.Empty(await knowledge.RetrieveAsync(Org, property.Id, [], "it"));
    }

    [Fact]
    public async Task RetrieveAsync_GivesTheFactsOfTheAskedTopicsOnly_ListingFirst_ThenThePropertysEntries_ThenTheOrganizations()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org, configure: AssistantTestKit.FullListing);
        var knowledge = AssistantTestKit.Knowledge(db);
        var own = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.Parking, "Il garage è al piano -1."), User);
        var wide = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Parking, "In centro il parcheggio è a pagamento.", kind: KnowledgeEntryKind.Rule), User);
        await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.WiFi, "Rete TrulloOstuni."), User);

        var facts = await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.Parking, KnowledgeTopic.CheckOut], "it");

        Assert.Equal(
            [
                (KnowledgeTopic.CheckOut, null, ListingFactSources.CheckOutBy),
                (KnowledgeTopic.Parking, null, ListingFactSources.Amenities),
                (KnowledgeTopic.Parking, own.Id, null),
                (KnowledgeTopic.Parking, wide.Id, null),
            ],
            facts.Select(f => (f.Topic, f.EntryId, f.SourceField)));
        Assert.Equal([false, false, false, true], facts.Select(f => f.Inherited));
        Assert.Equal($"entry:{own.Id}", facts[2].SourceRef);
        Assert.Equal($"listing:{ListingFactSources.CheckOutBy}", facts[0].SourceRef);
    }

    [Fact]
    public async Task RetrieveAsync_AnOrganizationEntryApplies_UnlessThePropertyHasAnActiveEntryOfItsOwnWithTheSameTopicAndKind()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var knowledge = AssistantTestKit.Knowledge(db);
        var wideFact = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Pets, "Cani di piccola taglia."), User);
        var wideRule = await knowledge.CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Pets, "Mai sui letti.", kind: KnowledgeEntryKind.Rule), User);

        // No entry of its own: the organization's two apply.
        Assert.Equal(
            new[] { wideFact.Id, wideRule.Id }.Order(),
            (await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.Pets], "it")).Select(f => f.EntryId!.Value).Order());

        // An active fact of its own with the same topic: the organization's fact goes, its rule (another kind) stays.
        var ownFact = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.Pets, "Nessun animale."), User);
        Assert.Equal(
            [ownFact.Id, wideRule.Id],
            (await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.Pets], "it")).Select(f => f.EntryId!.Value));

        // Switched off, it does not count as an entry of its own, and is left out itself.
        await knowledge.UpdateEntryAsync(Org, property.Id, ownFact.Id, AssistantTestKit.Fact(KnowledgeTopic.Pets, "Nessun animale.", isActive: false), User);
        Assert.Equal(
            new[] { wideFact.Id, wideRule.Id }.Order(),
            (await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.Pets], "it")).Select(f => f.EntryId!.Value).Order());
    }

    [Fact]
    public async Task RetrieveAsync_InactiveEntriesAreLeftOut()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var knowledge = AssistantTestKit.Knowledge(db);
        await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.Contacts, "Marco 333.", isActive: false), User);

        Assert.Empty(await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.Contacts], "it"));
    }

    [Fact]
    public async Task RetrieveAsync_AnEntryInAnotherLanguage_IsUsedOnlyWhenTheTopicHasNoneInTheLanguageAskedOrInAny()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var knowledge = AssistantTestKit.Knowledge(db);
        var italian = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.CheckIn, "Il check-in è alle 15.", language: "it"), User);
        var english = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.CheckIn, "Check-in is at 3 pm.", language: "en"), User);
        var any = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.CheckIn, "Chiave nella cassetta.", kind: KnowledgeEntryKind.Rule), User);
        var onlyItalian = await knowledge.CreateEntryAsync(Org, property.Id, AssistantTestKit.Fact(KnowledgeTopic.Hours, "Silenzio dalle 22.", language: "it"), User);

        var inEnglish = await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.CheckIn, KnowledgeTopic.Hours], "en");
        var inItalian = await knowledge.RetrieveAsync(Org, property.Id, [KnowledgeTopic.CheckIn], "it");

        // English asked: the English entry and the one for any language; the Italian check-in entry is left out. The topic with an Italian entry only keeps it.
        Assert.DoesNotContain(inEnglish, f => f.EntryId == italian.Id);
        Assert.Contains(inEnglish, f => f.EntryId == any.Id);
        Assert.Contains(inEnglish, f => f.EntryId == onlyItalian.Id);
        Assert.Contains(inEnglish, f => f.EntryId == english.Id);

        // Italian asked: the Italian entry and the one for any language; the English one is left out.
        Assert.Contains(inItalian, f => f.EntryId == italian.Id);
        Assert.Contains(inItalian, f => f.EntryId == any.Id);
        Assert.DoesNotContain(inItalian, f => f.EntryId == english.Id);
    }

    [Fact]
    public async Task RetrieveAsync_ForAPropertyThatIsNotTheOrganizations_IsNotFound()
    {
        await using var db = AssistantTestKit.NewDb();
        var foreign = await AssistantTestKit.AddPropertyAsync(db, OtherOrg, configure: AssistantTestKit.FullListing);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => AssistantTestKit.Knowledge(db).RetrieveAsync(Org, foreign.Id, [KnowledgeTopic.CheckIn], "it"));

        Assert.Equal("property_not_found", error.Code);
    }
}
