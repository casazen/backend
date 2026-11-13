using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02 (NF-66, AC-NF66-2, AC-NF67-1): the rules of an organization with the exceptions of a property, the inheritance between them, and the version of the
/// instructions that grows with every change, in the unit of work of the change and only when something changed.
/// </summary>
public class AssistantRulesServiceTests
{
    private const string User = "auth0|admin";

    private static readonly Guid Org = Guid.Parse("11111111-aaaa-4aaa-8aaa-000000000001");
    private static readonly Guid OtherOrg = Guid.Parse("22222222-bbbb-4bbb-8bbb-000000000002");

    [Fact]
    public async Task GetAsync_AnOrganizationThatNeverSavedAnything_HasTheDefaults_AndNothingIsWritten()
    {
        await using var db = AssistantTestKit.NewDb();

        var rules = await AssistantTestKit.Rules(db).GetAsync(Org);

        Assert.False(rules.Enabled);
        Assert.Equal(AssistantTone.Neutral, rules.Tone);
        Assert.Equal(["it", "en"], rules.Languages);
        Assert.Equal(0.85m, rules.ConfidenceThreshold);
        Assert.Null(rules.Signature);
        Assert.Null(rules.HoursFrom);
        Assert.Empty(rules.AllowedTopics);
        Assert.Empty(rules.ForbiddenTopics);
        Assert.Equal(1, rules.InstructionVersion);
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_StoresTheRules_AndReadsThemBack_FromAnotherRequest()
    {
        var store = AssistantTestKit.NewStore();
        await using (var db = AssistantTestKit.NewDb(store))
        {
            var saved = await AssistantTestKit.Rules(db).SaveAsync(
                Org,
                AssistantTestKit.RulesInput(
                    forbidden: ["invoicerequest", "Parking"],
                    allowed: ["CheckIn", "wifi"],
                    from: new TimeOnly(8, 0),
                    to: new TimeOnly(22, 0)),
                User);

            Assert.True(saved.Enabled);
            Assert.Equal(AssistantTone.Friendly, saved.Tone);
            Assert.Equal(["InvoiceRequest", "Parking"], saved.ForbiddenTopics);
            Assert.Equal(2, saved.InstructionVersion);
        }

        await using var other = AssistantTestKit.NewDb(store);
        var rules = await AssistantTestKit.Rules(other).GetAsync(Org);
        Assert.Equal(["it", "en", "de"], rules.Languages);
        Assert.Equal("Marco, Casa Rossi", rules.Signature);
        Assert.Equal(0.9m, rules.ConfidenceThreshold);
        Assert.Equal(["CheckIn", "WiFi"], rules.AllowedTopics);
        Assert.Equal(new TimeOnly(8, 0), rules.HoursFrom);
        Assert.Equal(new TimeOnly(22, 0), rules.HoursTo);

        var row = await other.OrgAssistantRules.AsNoTracking().SingleAsync();
        Assert.Equal(Org, row.OrgId);
        Assert.Equal(User, row.UpdatedByUserId);
        Assert.Equal(AssistantTestKit.Now.UtcDateTime, row.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_TheVersionOfTheInstructionsGrowsOnlyWhenSomethingChanged()
    {
        await using var db = AssistantTestKit.NewDb();
        var service = AssistantTestKit.Rules(db);

        // The first change takes the defaults (version 1) to 2.
        Assert.Equal(2, (await service.SaveAsync(Org, AssistantTestKit.RulesInput(), User)).InstructionVersion);

        // The same rules again: the version stays.
        Assert.Equal(2, (await service.SaveAsync(Org, AssistantTestKit.RulesInput(), User)).InstructionVersion);

        // Each real change grows it by one.
        Assert.Equal(3, (await service.SaveAsync(Org, AssistantTestKit.RulesInput(tone: AssistantTone.Formal), User)).InstructionVersion);
        Assert.Equal(4, (await service.SaveAsync(Org, AssistantTestKit.RulesInput(tone: AssistantTone.Formal, threshold: 0.95m), User)).InstructionVersion);
        Assert.Equal(5, (await service.SaveAsync(Org, AssistantTestKit.RulesInput(tone: AssistantTone.Formal, threshold: 0.95m, enabled: false), User)).InstructionVersion);
        Assert.Equal(5, (await service.GetAsync(Org)).InstructionVersion);
        Assert.Single(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_WhatTheRequestDoesNotGive_IsTheDefault_AndTheFirstSaveOfTheDefaultsIsNoChange()
    {
        await using var db = AssistantTestKit.NewDb();

        var saved = await AssistantTestKit.Rules(db).SaveAsync(Org, new AssistantRulesInput(null, null, null, null, null, null, null, null, null), User);

        Assert.False(saved.Enabled);
        Assert.Equal(AssistantTone.Neutral, saved.Tone);
        Assert.Equal(["it", "en"], saved.Languages);
        Assert.Equal(0.85m, saved.ConfidenceThreshold);
        Assert.Empty(saved.AllowedTopics);
        Assert.Empty(saved.ForbiddenTopics);

        // The row exists (it says who saved), and nothing changed: the version is still the first.
        Assert.Equal(1, saved.InstructionVersion);
        Assert.Single(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_AnEmptyListOfLanguages_IsNeverTheDefaultNorEveryLanguage_ItIs422()
    {
        await using var db = AssistantTestKit.NewDb();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => AssistantTestKit.Rules(db).SaveAsync(Org, AssistantTestKit.RulesInput(languages: []), User));

        Assert.Equal("assistant_rules_invalid", error.Code);
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Declared: the one place where an empty list in a request means «all» (rule 2). <c>allowedTopics</c> empty or missing is <b>no restriction by the list</b>, the state of an organization that
    /// never saved its rules (and what <c>GET</c> gives back): it is not a 422, it is not «nothing allowed» (that would take an explicit flag, a later task of the feature), it does not touch the
    /// forbidden topics, and a property, which has no allowed list of its own, inherits it. The gate of US03 reads it so.
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnEmptyOrMissingListOfAllowedTopics_IsNoRestriction_TheDefaultItself_NeverNothingAllowed()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        Assert.Empty((await service.GetAsync(Org)).AllowedTopics);

        // Explicitly empty, and missing, over the defaults: accepted, stored as no restriction, and it is the default itself, so nothing changed and the version is still the first.
        var explicitlyEmpty = await service.SaveAsync(Org, new AssistantRulesInput(null, null, null, null, null, null, [], [], null), User);
        Assert.Empty(explicitlyEmpty.AllowedTopics);
        Assert.Equal(1, explicitlyEmpty.InstructionVersion);
        var missing = await service.SaveAsync(Org, new AssistantRulesInput(null, null, null, null, null, null, null, null, null), User);
        Assert.Empty(missing.AllowedTopics);
        Assert.Equal(1, missing.InstructionVersion);

        // A list restricts, for the organization and, inherited, for its properties.
        var restricted = await service.SaveAsync(Org, new AssistantRulesInput(null, null, null, null, null, null, ["Parking", "WiFi"], [], null), User);
        Assert.Equal(["Parking", "WiFi"], restricted.AllowedTopics);
        Assert.Equal(2, restricted.InstructionVersion);
        Assert.Equal(["Parking", "WiFi"], (await service.ResolveEffectiveAsync(Org, property.Id)).AllowedTopics);

        // The empty list is what lifts it: no restriction again (a change, so the version grows), never nothing allowed, and the forbidden topics keep their own say.
        var lifted = await service.SaveAsync(Org, new AssistantRulesInput(null, null, null, null, null, null, [], ["Pets"], null), User);
        Assert.Empty(lifted.AllowedTopics);
        Assert.Equal(["Pets"], lifted.ForbiddenTopics);
        Assert.Equal(3, lifted.InstructionVersion);
        var inherited = await service.ResolveEffectiveAsync(Org, property.Id);
        Assert.Empty(inherited.AllowedTopics);
        Assert.Equal(["Pets"], inherited.ForbiddenTopics);
    }

    [Theory]
    [InlineData("threshold", "assistant_threshold_invalid")]
    [InlineData("language", "assistant_rules_invalid")]
    [InlineData("signature", "assistant_rules_invalid")]
    [InlineData("hours", "assistant_rules_invalid")]
    [InlineData("topic", "assistant_rules_invalid")]
    [InlineData("tone", "assistant_rules_invalid")]
    public async Task SaveAsync_AnInvalidRequest_Is422_AndWritesNothing(string what, string code)
    {
        await using var db = AssistantTestKit.NewDb();
        var input = what switch
        {
            "threshold" => AssistantTestKit.RulesInput(threshold: 0.3m),
            "language" => AssistantTestKit.RulesInput(languages: ["it", "english"]),
            "signature" => AssistantTestKit.RulesInput(signature: new string('x', 301)),
            "hours" => AssistantTestKit.RulesInput(from: new TimeOnly(9, 0), to: null),
            "topic" => AssistantTestKit.RulesInput(forbidden: ["Gossip"]),
            _ => AssistantTestKit.RulesInput(tone: (AssistantTone)99),
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => AssistantTestKit.Rules(db).SaveAsync(Org, input, User));

        Assert.Equal(code, error.Code);
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    // ─── the version of the instructions, in the unit of work of the caller ───

    [Fact]
    public async Task BumpVersionAsync_TracksTheNewVersion_ItIsTheCallersSaveThatWritesIt()
    {
        var store = AssistantTestKit.NewStore();
        await using var db = AssistantTestKit.NewDb(store);

        await AssistantTestKit.Rules(db).BumpVersionAsync(Org);

        // Not saved by itself: another request sees nothing yet.
        await using (var other = AssistantTestKit.NewDb(store))
            Assert.Empty(await other.OrgAssistantRules.AsNoTracking().ToListAsync());

        await db.SaveChangesAsync();

        await using var after = AssistantTestKit.NewDb(store);
        var row = await after.OrgAssistantRules.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.InstructionVersion);
        Assert.False(row.Enabled);
    }

    [Fact]
    public async Task BumpVersionAsync_CalledTwiceInOneUnitOfWork_GrowsTheVersionTwice_OnOneRow()
    {
        var store = AssistantTestKit.NewStore();
        await using var db = AssistantTestKit.NewDb(store);
        var service = AssistantTestKit.Rules(db);

        await service.BumpVersionAsync(Org);
        await service.BumpVersionAsync(Org);
        await db.SaveChangesAsync();

        await using var after = AssistantTestKit.NewDb(store);
        Assert.Equal(3, (await after.OrgAssistantRules.AsNoTracking().SingleAsync()).InstructionVersion);
    }

    [Fact]
    public async Task BumpVersionAsync_OfAnOrganizationWithRules_GrowsItsVersionByOne_AndOnlyItsOwn()
    {
        await using var db = AssistantTestKit.NewDb();
        var service = AssistantTestKit.Rules(db);
        await service.SaveAsync(Org, AssistantTestKit.RulesInput(), User);
        await service.SaveAsync(OtherOrg, AssistantTestKit.RulesInput(tone: AssistantTone.Formal), User);
        db.ChangeTracker.Clear();

        await service.BumpVersionAsync(Org);
        await db.SaveChangesAsync();

        Assert.Equal(3, (await service.GetAsync(Org)).InstructionVersion);
        Assert.Equal(2, (await service.GetAsync(OtherOrg)).InstructionVersion);
    }

    [Fact]
    public async Task TwoChangesAtTheSameMoment_OnlyTheFirstWins_TheSecondIsAConflictWithNothingWritten()
    {
        var store = AssistantTestKit.NewStore();
        await using (var seed = AssistantTestKit.NewDb(store))
            await AssistantTestKit.Rules(seed).SaveAsync(Org, AssistantTestKit.RulesInput(), User);

        await using var first = AssistantTestKit.NewDb(store);
        await using var second = AssistantTestKit.NewDb(store);

        // The second request read the rules at version 2 (its growth of the version is tracked, not saved); the first one changes them, so they are at version 3; the second then
        // tries to save its own change on top of version 2.
        await AssistantTestKit.Rules(second).BumpVersionAsync(Org);
        await AssistantTestKit.Rules(first).SaveAsync(Org, AssistantTestKit.RulesInput(tone: AssistantTone.Formal), User);

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => AssistantTestKit.Knowledge(second).CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Parking, "Il parcheggio è gratuito."), User));

        // The change that lost writes nothing of the rules (on PostgreSQL the entry it carried is rolled back with it: one transaction).
        Assert.Equal("assistant_concurrent_change", error.Code);
        await using var after = AssistantTestKit.NewDb(store);
        var rules = await after.OrgAssistantRules.AsNoTracking().SingleAsync();
        Assert.Equal(3, rules.InstructionVersion);
        Assert.Equal(AssistantTone.Formal, rules.Tone);
    }

    // ─── the exceptions of a property and the inheritance ───

    [Fact]
    public async Task ResolveEffectiveAsync_AnOrganizationWithoutExceptions_IsTheOrganizationsRules()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        var organization = await service.SaveAsync(Org, AssistantTestKit.RulesInput(forbidden: ["Pets"]), User);

        var effective = await service.ResolveEffectiveAsync(Org, property.Id);

        AssertSame(organization, effective);
    }

    [Fact]
    public async Task ResolveEffectiveAsync_APropertyOverridesWhatItGives_AndFallsBackToTheOrganizationForTheRest()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        await service.SaveAsync(Org, AssistantTestKit.RulesInput(forbidden: ["Pets", "Parking"], from: new TimeOnly(8, 0), to: new TimeOnly(20, 0)), User);

        await service.SavePropertySettingsAsync(
            Org,
            property.Id,
            new PropertyAssistantSettingsInput(null, AssistantTone.Formal, "Anna, Trullo dei Sogni", new TimeOnly(22, 0), new TimeOnly(6, 0), 0.97m, ["Noise"]),
            User);
        var effective = await service.ResolveEffectiveAsync(Org, property.Id);

        Assert.Equal(AssistantTone.Formal, effective.Tone);
        Assert.Equal("Anna, Trullo dei Sogni", effective.Signature);
        Assert.Equal(new TimeOnly(22, 0), effective.HoursFrom);
        Assert.Equal(new TimeOnly(6, 0), effective.HoursTo);
        Assert.Equal(0.97m, effective.ConfidenceThreshold);

        // The property's list replaces the organization's: Pets and Parking are no longer forbidden here.
        Assert.Equal(["Noise"], effective.ForbiddenTopics);

        // What the property did not give is the organization's.
        Assert.True(effective.Enabled);
        Assert.Equal(["it", "en", "de"], effective.Languages);
    }

    [Fact]
    public async Task ResolveEffectiveAsync_ThePropertyCanOnlySwitchTheAssistantOff_NeverOnWhenTheOrganizationIsOff()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);

        // The organization is off and the property says «on»: off.
        await service.SaveAsync(Org, AssistantTestKit.RulesInput(enabled: false), User);
        await service.SavePropertySettingsAsync(Org, property.Id, new PropertyAssistantSettingsInput(true, null, null, null, null, null, null), User);
        Assert.False((await service.ResolveEffectiveAsync(Org, property.Id)).Enabled);

        // The organization is on and the property says nothing: on. The property says «off»: off.
        await service.SaveAsync(Org, AssistantTestKit.RulesInput(enabled: true), User);
        Assert.True((await service.ResolveEffectiveAsync(Org, property.Id)).Enabled);
        await service.SavePropertySettingsAsync(Org, property.Id, new PropertyAssistantSettingsInput(false, null, null, null, null, null, null), User);
        Assert.False((await service.ResolveEffectiveAsync(Org, property.Id)).Enabled);
    }

    [Fact]
    public async Task ResolveEffectiveAsync_AnEmptyListOfForbiddenTopicsOfTheProperty_MeansNothingIsForbiddenThere_NotTheOrganizations()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        await service.SaveAsync(Org, AssistantTestKit.RulesInput(forbidden: ["Pets"]), User);

        await service.SavePropertySettingsAsync(Org, property.Id, new PropertyAssistantSettingsInput(null, null, null, null, null, null, []), User);

        Assert.Empty((await service.ResolveEffectiveAsync(Org, property.Id)).ForbiddenTopics);
        Assert.Equal(["Pets"], (await service.GetAsync(Org)).ForbiddenTopics);
    }

    [Fact]
    public async Task DeletePropertySettingsAsync_ThePropertyFollowsTheOrganizationAgain_AndTheVersionGrows()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        await service.SaveAsync(Org, AssistantTestKit.RulesInput(tone: AssistantTone.Friendly), User);
        await service.SavePropertySettingsAsync(Org, property.Id, new PropertyAssistantSettingsInput(null, AssistantTone.Formal, null, null, null, null, null), User);
        var before = (await service.GetAsync(Org)).InstructionVersion;
        Assert.Equal(AssistantTone.Formal, (await service.ResolveEffectiveAsync(Org, property.Id)).Tone);

        await service.DeletePropertySettingsAsync(Org, property.Id);

        Assert.Equal(AssistantTone.Friendly, (await service.ResolveEffectiveAsync(Org, property.Id)).Tone);
        Assert.Equal(before + 1, (await service.GetAsync(Org)).InstructionVersion);
        Assert.Empty(await db.PropertyAssistantSettings.AsNoTracking().ToListAsync());

        // Nothing left to remove is not an error, and moves nothing.
        await service.DeletePropertySettingsAsync(Org, property.Id);
        Assert.Equal(before + 1, (await service.GetAsync(Org)).InstructionVersion);
    }

    [Fact]
    public async Task SavePropertySettingsAsync_NoFieldAtAll_RemovesTheExceptions_AndAPropertyWithNoneHasNoRow()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        var none = new PropertyAssistantSettingsInput(null, null, null, null, null, null, null);

        // Nothing to say and nothing stored: no row is made.
        Assert.Equal(new PropertyAssistantSettingsItem(property.Id, null, null, null, null, null, null, null, null), await service.SavePropertySettingsAsync(Org, property.Id, none, User));
        Assert.Empty(await db.PropertyAssistantSettings.AsNoTracking().ToListAsync());

        await service.SavePropertySettingsAsync(Org, property.Id, new PropertyAssistantSettingsInput(false, null, null, null, null, null, null), User);
        Assert.Single(await db.PropertyAssistantSettings.AsNoTracking().ToListAsync());

        await service.SavePropertySettingsAsync(Org, property.Id, none, User);
        Assert.Empty(await db.PropertyAssistantSettings.AsNoTracking().ToListAsync());
        Assert.Null((await service.GetPropertySettingsAsync(Org, property.Id)).Enabled);
    }

    [Fact]
    public async Task SavePropertySettingsAsync_ASaveThatChangesNothing_DoesNotGrowTheVersion()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Rules(db);
        var settings = new PropertyAssistantSettingsInput(null, AssistantTone.Formal, null, null, null, 0.9m, ["Pets"]);

        await service.SavePropertySettingsAsync(Org, property.Id, settings, User);
        var version = (await service.GetAsync(Org)).InstructionVersion;
        await service.SavePropertySettingsAsync(Org, property.Id, settings, User);

        Assert.Equal(2, version);
        Assert.Equal(version, (await service.GetAsync(Org)).InstructionVersion);
    }

    [Theory]
    [InlineData("threshold", "assistant_threshold_invalid")]
    [InlineData("topic", "assistant_rules_invalid")]
    [InlineData("hours", "assistant_rules_invalid")]
    [InlineData("tone", "assistant_rules_invalid")]
    public async Task SavePropertySettingsAsync_AnInvalidRequest_Is422_AndWritesNothing(string what, string code)
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var input = what switch
        {
            "threshold" => new PropertyAssistantSettingsInput(null, null, null, null, null, 1.2m, null),
            "topic" => new PropertyAssistantSettingsInput(null, null, null, null, null, null, ["Gossip"]),
            "hours" => new PropertyAssistantSettingsInput(null, null, null, new TimeOnly(9, 0), new TimeOnly(9, 0), null, null),
            _ => new PropertyAssistantSettingsInput(null, (AssistantTone)42, null, null, null, null, null),
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => AssistantTestKit.Rules(db).SavePropertySettingsAsync(Org, property.Id, input, User));

        Assert.Equal(code, error.Code);
        Assert.Empty(await db.PropertyAssistantSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task APropertyThatIsNotTheOrganizations_IsNotFound_ForReadSaveAndDelete_AndNothingIsWritten()
    {
        await using var db = AssistantTestKit.NewDb();
        var foreign = await AssistantTestKit.AddPropertyAsync(db, OtherOrg);
        var service = AssistantTestKit.Rules(db);
        var anything = new PropertyAssistantSettingsInput(false, null, null, null, null, null, null);

        foreach (var attempt in new Func<Task>[]
                 {
                     () => service.GetPropertySettingsAsync(Org, foreign.Id),
                     () => service.SavePropertySettingsAsync(Org, foreign.Id, anything, User),
                     () => service.DeletePropertySettingsAsync(Org, foreign.Id),
                     () => service.GetPropertySettingsAsync(Org, Guid.NewGuid()),
                 })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal("property_not_found", error.Code);
        }

        Assert.Empty(await db.PropertyAssistantSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    // The lists of a record are compared by reference: the rules are compared member by member.
    private static void AssertSame(EffectiveAssistantRules expected, EffectiveAssistantRules actual)
    {
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Tone, actual.Tone);
        Assert.Equal(expected.Languages, actual.Languages);
        Assert.Equal(expected.Signature, actual.Signature);
        Assert.Equal(expected.HoursFrom, actual.HoursFrom);
        Assert.Equal(expected.HoursTo, actual.HoursTo);
        Assert.Equal(expected.AllowedTopics, actual.AllowedTopics);
        Assert.Equal(expected.ForbiddenTopics, actual.ForbiddenTopics);
        Assert.Equal(expected.ConfidenceThreshold, actual.ConfidenceThreshold);
        Assert.Equal(expected.InstructionVersion, actual.InstructionVersion);
    }

    [Fact]
    public void EffectiveRules_TheDefaultsAreTheOnesOfTheBrief_AndACopyOfTheRowIsNeverTheRowsList()
    {
        Assert.Equal(0.85m, EffectiveAssistantRules.Defaults.ConfidenceThreshold);
        Assert.False(EffectiveAssistantRules.Defaults.Enabled);

        var row = new OrgAssistantRules { ForbiddenTopics = ["Pets"] };
        var effective = EffectiveAssistantRules.Of(row);
        row.ForbiddenTopics.Add("Parking");

        Assert.Equal(["Pets"], effective.ForbiddenTopics);
    }
}
