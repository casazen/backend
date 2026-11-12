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
/// E05/F05/US01/T03 (NF-66, NF-67, AC-NF66-3, AC-NF67-3, PO-08): what the assistant does in each situation, for the organization and as the exceptions of a property: the default of the
/// catalog, the row of the organization over it, the row of the property over both, the start of the trust period, the templates, and the version of the instructions.
/// </summary>
public class AssistantSituationServiceTests
{
    private const string User = "auth0|admin";

    private static readonly Guid Org = Guid.Parse("11111111-aaaa-4aaa-8aaa-000000000001");
    private static readonly Guid OtherOrg = Guid.Parse("22222222-bbbb-4bbb-8bbb-000000000002");

    private static async Task<int> VersionAsync(AppDbContext db) => (await AssistantTestKit.Rules(db).GetAsync(Org)).InstructionVersion;

    private static EffectiveSituation Of(IEnumerable<EffectiveSituation> list, AssistantSituation situation) => list.Single(s => s.Situation == situation);

    // ─── the default ───

    [Fact]
    public async Task ListEffectiveAsync_NothingConfigured_GivesThe15SituationsOfTheCatalogInDraft_TheEmergencyInEscalate()
    {
        await using var db = AssistantTestKit.NewDb();

        var list = await AssistantTestKit.Situations(db).ListEffectiveAsync(Org, null);

        Assert.Equal(AssistantSituationCatalog.All.Select(i => i.Situation), list.Select(s => s.Situation));
        Assert.Equal(15, list.Count);
        Assert.All(list, s =>
        {
            Assert.Equal(AssistantSettingOrigin.Default, s.Origin);
            Assert.Null(s.ModeSince);
            Assert.False(s.HasTemplate);
            Assert.Null(s.Template);
            Assert.Equal(AssistantSituationCatalog.Of(s.Situation).Code, s.Code);
            Assert.Equal(AssistantSituationCatalog.Of(s.Situation).SourceTopic, s.Source);
            Assert.Equal(AssistantSituationCatalog.Of(s.Situation).AllowedModes, s.AllowedModes);
        });
        Assert.Equal(AssistantMode.Escalate, Of(list, AssistantSituation.Emergency).Mode);
        Assert.All(list.Where(s => s.Situation != AssistantSituation.Emergency), s => Assert.Equal(AssistantMode.Draft, s.Mode));

        // A read writes nothing: no row, no rules row, no version.
        Assert.Empty(await db.AssistantSituationSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ResolveAsync_ASituationNobodyConfigured_IsTheDefault_WithItsSourceAndNoTrustPeriod()
    {
        await using var db = AssistantTestKit.NewDb();

        var parking = await AssistantTestKit.Situations(db).ResolveAsync(Org, null, AssistantSituation.Parking);
        var emergency = await AssistantTestKit.Situations(db).ResolveAsync(Org, null, AssistantSituation.Emergency);
        var thanks = await AssistantTestKit.Situations(db).ResolveAsync(Org, null, AssistantSituation.ThankYou);

        Assert.Equal(new ResolvedSituation(AssistantMode.Draft, null, KnowledgeTopic.Parking, null), parking);
        Assert.Equal(new ResolvedSituation(AssistantMode.Escalate, null, KnowledgeTopic.Contacts, null), emergency);
        Assert.Equal(new ResolvedSituation(AssistantMode.Draft, null, null, null), thanks);
    }

    // ─── the organization's row ───

    [Fact]
    public async Task SaveAsync_ForTheOrganization_StartsTheTrustPeriodNow_AndGrowsTheVersion()
    {
        var store = AssistantTestKit.NewStore();
        await using var db = AssistantTestKit.NewDb(store);

        var saved = await AssistantTestKit.Situations(db).SaveAsync(
            Org, null, AssistantSituation.CheckInTime, AssistantMode.Automatic, "Il check-in è dalle {{check_in_time}}.", null, User);

        Assert.Equal(AssistantMode.Automatic, saved.Mode);
        Assert.Equal(AssistantSettingOrigin.Organization, saved.Origin);
        Assert.Equal(AssistantTestKit.Now.UtcDateTime, saved.ModeSince);
        Assert.True(saved.HasTemplate);
        Assert.Equal("Il check-in è dalle {{check_in_time}}.", saved.Template);
        Assert.Equal(KnowledgeTopic.CheckIn, saved.Source);

        await using var after = AssistantTestKit.NewDb(store);
        var row = await after.AssistantSituationSettings.AsNoTracking().SingleAsync();
        Assert.Equal(Org, row.OrgId);
        Assert.Null(row.PropertyId);
        Assert.Equal(User, row.UpdatedByUserId);
        Assert.Equal(2, await VersionAsync(after));

        var listed = Of(await AssistantTestKit.Situations(after).ListEffectiveAsync(Org, null), AssistantSituation.CheckInTime);
        Assert.Equal(AssistantMode.Automatic, listed.Mode);
        Assert.Equal(AssistantSettingOrigin.Organization, listed.Origin);
    }

    [Fact]
    public async Task SaveAsync_TheTrustPeriodMovesOnlyWithTheMode_ANewTextForTheAnswerDoesNotStartItAgain()
    {
        await using var db = AssistantTestKit.NewDb();
        var first = new FixedTimeProvider(AssistantTestKit.Now);
        var second = new FixedTimeProvider(AssistantTestKit.Now.AddDays(3));
        var third = new FixedTimeProvider(AssistantTestKit.Now.AddDays(9));

        await AssistantTestKit.Situations(db, first).SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Draft, null, null, User);
        var textChanged = await AssistantTestKit.Situations(db, second).SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Draft, "Il parcheggio è gratuito.", null, User);
        var modeChanged = await AssistantTestKit.Situations(db, third).SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Automatic, "Il parcheggio è gratuito.", null, User);

        Assert.Equal(AssistantTestKit.Now.UtcDateTime, textChanged.ModeSince);
        Assert.Equal(AssistantTestKit.Now.AddDays(9).UtcDateTime, modeChanged.ModeSince);
        var row = await db.AssistantSituationSettings.AsNoTracking().SingleAsync();
        Assert.Equal(AssistantTestKit.Now.AddDays(9).UtcDateTime, row.ModeSince);
        Assert.Equal(AssistantTestKit.Now.AddDays(9).UtcDateTime, row.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_TheSameValuesAgain_ChangeNothing_NotTheVersionNorTheTime()
    {
        await using var db = AssistantTestKit.NewDb();
        await AssistantTestKit.Situations(db).SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, "Ciao {{guest_name}}.", null, User);
        var version = await VersionAsync(db);

        var again = await AssistantTestKit.Situations(db, new FixedTimeProvider(AssistantTestKit.Now.AddDays(2)))
            .SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, "Ciao {{guest_name}}.", null, User);

        Assert.Equal(version, await VersionAsync(db));
        Assert.Equal(AssistantTestKit.Now.UtcDateTime, again.ModeSince);
        Assert.Equal(AssistantTestKit.Now.UtcDateTime, (await db.AssistantSituationSettings.AsNoTracking().SingleAsync()).UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_EachSituationHasOneRowForTheOrganization_ASecondSaveChangesTheRow()
    {
        await using var db = AssistantTestKit.NewDb();
        var service = AssistantTestKit.Situations(db);

        await service.SaveAsync(Org, null, AssistantSituation.WiFi, AssistantMode.Draft, null, null, User);
        await service.SaveAsync(Org, null, AssistantSituation.WiFi, AssistantMode.Escalate, null, KnowledgeTopic.Contacts, User);
        await service.SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, null, null, User);

        var rows = await db.AssistantSituationSettings.AsNoTracking().OrderBy(r => r.Situation).ToListAsync();
        Assert.Equal([AssistantSituation.WiFi, AssistantSituation.Pets], rows.Select(r => r.Situation));
        Assert.Equal(AssistantMode.Escalate, rows.Single(r => r.Situation == AssistantSituation.WiFi).Mode);
        Assert.Equal(KnowledgeTopic.Contacts, rows.Single(r => r.Situation == AssistantSituation.WiFi).ResponseSource);
    }

    // ─── the exceptions of a property, and the inheritance ───

    [Fact]
    public async Task APropertyFollowsTheOrganization_UntilItHasAnExceptionOfItsOwn_AndGoesBackWhenItIsRemoved()
    {
        await using var db = AssistantTestKit.NewDb();
        var withException = await AssistantTestKit.AddPropertyAsync(db, Org, "Trullo");
        var following = await AssistantTestKit.AddPropertyAsync(db, Org, "Masseria");
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Automatic, "Parcheggio gratuito.", null, User);

        // Everyone follows the organization.
        Assert.Equal(AssistantSettingOrigin.Organization, Of(await service.ListEffectiveAsync(Org, withException.Id), AssistantSituation.Parking).Origin);

        // The exception of one property is a whole row: it does not take the template of the organization's.
        await service.SaveAsync(Org, withException.Id, AssistantSituation.Parking, AssistantMode.Draft, null, KnowledgeTopic.Directions, User);
        var own = Of(await service.ListEffectiveAsync(Org, withException.Id), AssistantSituation.Parking);
        Assert.Equal(AssistantMode.Draft, own.Mode);
        Assert.Equal(AssistantSettingOrigin.Property, own.Origin);
        Assert.False(own.HasTemplate);
        Assert.Equal(KnowledgeTopic.Directions, own.Source);

        // The other property and the organization itself are as they were.
        var other = Of(await service.ListEffectiveAsync(Org, following.Id), AssistantSituation.Parking);
        Assert.Equal(AssistantMode.Automatic, other.Mode);
        Assert.Equal(AssistantSettingOrigin.Organization, other.Origin);
        Assert.Equal("Parcheggio gratuito.", other.Template);
        Assert.Equal(AssistantMode.Automatic, Of(await service.ListEffectiveAsync(Org, null), AssistantSituation.Parking).Mode);

        // The exception of the property is only for the property: the pipeline reads it for that property and the organization's for the other.
        Assert.Equal(AssistantMode.Draft, (await service.ResolveAsync(Org, withException.Id, AssistantSituation.Parking)).Mode);
        Assert.Equal(AssistantMode.Automatic, (await service.ResolveAsync(Org, following.Id, AssistantSituation.Parking)).Mode);

        // Removing the exception restores the organization's value.
        await service.DeleteExceptionAsync(Org, withException.Id, AssistantSituation.Parking);
        var restored = Of(await service.ListEffectiveAsync(Org, withException.Id), AssistantSituation.Parking);
        Assert.Equal(AssistantMode.Automatic, restored.Mode);
        Assert.Equal(AssistantSettingOrigin.Organization, restored.Origin);
        Assert.Equal("Parcheggio gratuito.", restored.Template);
        Assert.Single(await db.AssistantSituationSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ResolveAsync_ReadsTheTemplateInClear_TheSourceOfTheRowOrTheSituations_AndTheStartOfTheTrustPeriod()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(Org, null, AssistantSituation.CheckInTime, AssistantMode.Draft, "Il check-in è dalle {{check_in_time}}.", null, User);
        await service.SaveAsync(Org, property.Id, AssistantSituation.WiFi, AssistantMode.AutomaticWithExceptions, null, KnowledgeTopic.Amenities, User);

        var checkIn = await service.ResolveAsync(Org, property.Id, AssistantSituation.CheckInTime);
        var wifi = await service.ResolveAsync(Org, property.Id, AssistantSituation.WiFi);

        Assert.Equal(new ResolvedSituation(AssistantMode.Draft, "Il check-in è dalle {{check_in_time}}.", KnowledgeTopic.CheckIn, AssistantTestKit.Now.UtcDateTime), checkIn);
        Assert.Equal(new ResolvedSituation(AssistantMode.AutomaticWithExceptions, null, KnowledgeTopic.Amenities, AssistantTestKit.Now.UtcDateTime), wifi);
    }

    [Fact]
    public async Task DeleteExceptionAsync_GrowsTheVersion_AndNothingToRemoveIsNotAnError()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(Org, property.Id, AssistantSituation.Pets, AssistantMode.Escalate, null, null, User);
        var version = await VersionAsync(db);

        await service.DeleteExceptionAsync(Org, property.Id, AssistantSituation.Pets);
        Assert.Equal(version + 1, await VersionAsync(db));

        await service.DeleteExceptionAsync(Org, property.Id, AssistantSituation.Pets);
        Assert.Equal(version + 1, await VersionAsync(db));
    }

    // ─── the templates ───

    [Fact]
    public async Task SaveAsync_ATemplateWithAnUnknownVariable_IsRefusedWithTheNameOfTheVariable_AndNothingIsSaved()
    {
        await using var db = AssistantTestKit.NewDb();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => AssistantTestKit.Situations(db).SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Draft, "Ciao {{guest_nam}}, il parcheggio è libero.", null, User));

        Assert.Equal("assistant_template_invalid", error.Code);
        Assert.Contains("guest_nam", error.MessageArgs.Select(a => a.ToString()));
        Assert.Empty(await db.AssistantSituationSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("Ciao {{")]
    [InlineData("Ciao }} amico")]
    [InlineData("Ciao {{ }}")]
    public async Task SaveAsync_ABraceThatBelongsToNoVariable_IsRefused(string template)
    {
        await using var db = AssistantTestKit.NewDb();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => AssistantTestKit.Situations(db).SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Draft, template, null, User));

        Assert.Equal("assistant_template_invalid", error.Code);
    }

    [Fact]
    public async Task SaveAsync_ATemplateWithEveryVariable_IsFilledInWithRealValuesWhenItIsSaved_AndKeptAsWritten()
    {
        await using var db = AssistantTestKit.NewDb();
        const string template = "Buongiorno {{guest_name}}, la prenotazione {{booking_code}} a {{property_name}} va dal {{check_in_date}} al {{check_out_date}}: arrivo dalle {{check_in_time}}, partenza entro le {{ check_out_time }}. {{host_name}}";

        var saved = await AssistantTestKit.Situations(db).SaveAsync(Org, null, AssistantSituation.CheckInTime, AssistantMode.Draft, template, null, User);

        // The text is stored with its variables: they are filled in when a message is written, never when the template is saved.
        Assert.Equal(template, saved.Template);
        Assert.Equal(template, (await db.AssistantSituationSettings.AsNoTracking().SingleAsync()).ResponseTemplate);
    }

    [Fact]
    public async Task SaveAsync_ATemplateThatIsTooLong_IsRefused_OneOf1000CharactersIsKept()
    {
        await using var db = AssistantTestKit.NewDb();
        var service = AssistantTestKit.Situations(db);

        var kept = await service.SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, new string('a', 1000), null, User);
        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(Org, null, AssistantSituation.Directions, AssistantMode.Draft, new string('a', 1001), null, User));

        Assert.Equal(1000, kept.Template!.Length);
        Assert.Equal("assistant_template_invalid", error.Code);
    }

    [Fact]
    public async Task SaveAsync_ABlankTemplate_IsNoTemplate_AndSavingItClearsTheOneThatWasThere()
    {
        await using var db = AssistantTestKit.NewDb();
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, "Cani ammessi.", null, User);

        var cleared = await service.SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, "  \n ", null, User);

        Assert.False(cleared.HasTemplate);
        Assert.Null(cleared.Template);
        Assert.Null((await db.AssistantSituationSettings.AsNoTracking().SingleAsync()).ResponseTemplate);
    }

    // ─── what is refused ───

    [Fact]
    public async Task SaveAsync_ASituationThatIsNoneOfThe15_IsNotFound_ASourceThatIsNoTopicIsRefused_AndNothingIsWritten()
    {
        await using var db = AssistantTestKit.NewDb();
        var service = AssistantTestKit.Situations(db);

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => service.SaveAsync(Org, null, (AssistantSituation)55, AssistantMode.Draft, null, null, User));
        var source = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, null, (KnowledgeTopic)77, User));

        Assert.Equal("assistant_situation_not_found", missing.Code);
        Assert.Equal("assistant_situation_invalid", source.Code);
        Assert.Empty(await db.AssistantSituationSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task APropertyThatIsNotTheOrganizations_IsNotFound_ForListSaveAndDelete_AndNothingIsWritten()
    {
        await using var db = AssistantTestKit.NewDb();
        var foreign = await AssistantTestKit.AddPropertyAsync(db, OtherOrg);
        var service = AssistantTestKit.Situations(db);

        foreach (var attempt in new Func<Task>[]
                 {
                     () => service.ListEffectiveAsync(Org, foreign.Id),
                     () => service.SaveAsync(Org, foreign.Id, AssistantSituation.Pets, AssistantMode.Draft, null, null, User),
                     () => service.DeleteExceptionAsync(Org, foreign.Id, AssistantSituation.Pets),
                     () => service.ListEffectiveAsync(Org, Guid.NewGuid()),
                 })
        {
            Assert.Equal("property_not_found", (await Assert.ThrowsAsync<NotFoundException>(attempt)).Code);
        }

        Assert.Empty(await db.AssistantSituationSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheRowsOfAnotherOrganization_AreNeverRead_NeverChanged_NeverRemoved()
    {
        await using var db = AssistantTestKit.NewDb();
        var ofB = await AssistantTestKit.AddPropertyAsync(db, OtherOrg, "Villa B");
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(OtherOrg, null, AssistantSituation.Parking, AssistantMode.Automatic, "Posto riservato a B.", null, User);
        await service.SaveAsync(OtherOrg, ofB.Id, AssistantSituation.Pets, AssistantMode.Escalate, null, null, User);

        // Organization A sees the defaults, and resolves the defaults, for the property of B too.
        var listed = await service.ListEffectiveAsync(Org, null);
        Assert.All(listed, s => Assert.Equal(AssistantSettingOrigin.Default, s.Origin));
        Assert.Equal(new ResolvedSituation(AssistantMode.Draft, null, KnowledgeTopic.Parking, null), await service.ResolveAsync(Org, null, AssistantSituation.Parking));
        Assert.Equal(new ResolvedSituation(AssistantMode.Draft, null, KnowledgeTopic.Pets, null), await service.ResolveAsync(Org, ofB.Id, AssistantSituation.Pets));

        // And a save of A's creates a row of A's, leaving B's alone.
        await service.SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Escalate, null, null, User);
        Assert.Equal(AssistantMode.Automatic, (await service.ResolveAsync(OtherOrg, null, AssistantSituation.Parking)).Mode);
        Assert.Equal("Posto riservato a B.", (await service.ResolveAsync(OtherOrg, null, AssistantSituation.Parking)).Template);
        Assert.Equal(AssistantMode.Escalate, (await service.ResolveAsync(Org, null, AssistantSituation.Parking)).Mode);
        Assert.Equal(3, await db.AssistantSituationSettings.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task EveryChangeGrowsTheVersionOnce_AReadNever()
    {
        await using var db = AssistantTestKit.NewDb();
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);
        var service = AssistantTestKit.Situations(db);

        await service.ListEffectiveAsync(Org, null);
        await service.ResolveAsync(Org, property.Id, AssistantSituation.Pets);
        Assert.Equal(1, await VersionAsync(db));

        await service.SaveAsync(Org, null, AssistantSituation.Pets, AssistantMode.Draft, null, null, User);
        Assert.Equal(2, await VersionAsync(db));
        await service.SaveAsync(Org, property.Id, AssistantSituation.Pets, AssistantMode.Escalate, null, null, User);
        Assert.Equal(3, await VersionAsync(db));
        await service.SaveAsync(Org, property.Id, AssistantSituation.Pets, AssistantMode.Escalate, null, null, User);
        Assert.Equal(3, await VersionAsync(db));
        await service.DeleteExceptionAsync(Org, property.Id, AssistantSituation.Pets);
        Assert.Equal(4, await VersionAsync(db));
    }
}
