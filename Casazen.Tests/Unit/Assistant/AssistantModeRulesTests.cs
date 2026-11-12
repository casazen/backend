using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Assistant;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T03 (NF-67, AC-NF67-3): the rule of the modes. A situation that carries a sensitive category (money, cancellation and refund, complaint, damage, safety, personal data),
/// and the one that matches none, can be escalated or drafted and never goes automatic, whoever asks and by whichever way it is asked.
/// </summary>
public class AssistantModeRulesTests
{
    private const string Host = "auth0|host";

    /// <summary>The situations that never go automatic: the seven that carry a sensitive category by nature, and <c>Other</c>.</summary>
    private static readonly AssistantSituation[] NeverAutomatic =
    [
        AssistantSituation.InvoiceRequest,
        AssistantSituation.Malfunction,
        AssistantSituation.Noise,
        AssistantSituation.DiscountRequest,
        AssistantSituation.CancellationOrRefund,
        AssistantSituation.Complaint,
        AssistantSituation.Emergency,
        AssistantSituation.Other,
    ];

    private static readonly AssistantMode[] AutomaticModes = [AssistantMode.Automatic, AssistantMode.AutomaticWithExceptions];

    private static readonly AssistantMode[] PrudentModes = [AssistantMode.Escalate, AssistantMode.Draft];

    /// <summary>
    /// Money, privacy and safety rule: every situation with a sensitive category refuses <c>Automatic</c> and <c>AutomaticWithExceptions</c> from the rule itself, from the service
    /// (for the organization and as the exception of a property) and from the API (the endpoints of the organization and of a property), saves nothing, and reads as a draft even when a
    /// row that breaks the rule was written by hand. A situation that is not sensitive can be automatic, so the refusals are the rule's and not a mode nobody can set. Written, not run
    /// locally against PostgreSQL: everything here runs on the services, the controllers and the rule over the InMemory database.
    /// </summary>
    [Fact]
    public async Task AssistantModeRules_SensitiveSituation_NeverAutomatic_Test()
    {
        // The set is the table's: a situation that becomes sensitive in the catalog is in this list, and one that is not in it is not sensitive.
        Assert.Equal(
            NeverAutomatic.Order(),
            AssistantSituationCatalog.All.Where(info => info.IsSensitive || info.Situation == AssistantSituation.Other).Select(info => info.Situation).Order());

        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = AssistantTestKit.Situations(db);
        var administrator = AssistantTestKit.Administrator(world);
        var authorization = AssistantTestKit.Authorization(world, administrator: true);
        var controller = new AssistantSituationsController(service, AssistantTestKit.Properties(db), AssistantTestKit.Org(world.OrgId), authorization)
        {
            ControllerContext = AssistantTestKit.ContextOf(administrator),
        };

        foreach (var situation in NeverAutomatic)
        {
            var code = AssistantSituationCatalog.Of(situation).Code;
            foreach (var mode in AutomaticModes)
            {
                // The rule.
                Assert.False(AssistantModeRules.IsAllowed(situation, mode));
                Assert.Equal(AssistantModeRules.ModeNotAllowedCode, Assert.Throws<DomainRuleException>(() => AssistantModeRules.Validate(situation, mode)).Code);

                // The service, for the organization and for a property.
                foreach (var propertyId in new Guid?[] { null, world.Granted.Id })
                {
                    var refused = await Assert.ThrowsAsync<DomainRuleException>(
                        () => service.SaveAsync(world.OrgId, propertyId, situation, mode, "Buongiorno {{guest_name}}", KnowledgeTopic.HouseRules, Host));
                    Assert.Equal("assistant_mode_not_allowed", refused.Code);
                }

                // The API: the organization's endpoint, and a property's.
                var request = new SaveAssistantSituationRequest { Mode = mode };
                Assert.Equal(
                    "assistant_mode_not_allowed",
                    (await Assert.ThrowsAsync<DomainRuleException>(() => controller.SaveForOrganization(code, request, CancellationToken.None))).Code);
                Assert.Equal(
                    "assistant_mode_not_allowed",
                    (await Assert.ThrowsAsync<DomainRuleException>(() => controller.SaveForProperty(world.Granted.Id, code, request, CancellationToken.None))).Code);
            }

            // The prudent modes are allowed.
            foreach (var mode in PrudentModes)
            {
                Assert.True(AssistantModeRules.IsAllowed(situation, mode));
                AssistantModeRules.Validate(situation, mode);
            }
        }

        // Nothing was saved by any of the refusals, and the version of the instructions never moved.
        Assert.Empty(await db.AssistantSituationSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrgAssistantRules.AsNoTracking().ToListAsync());

        // The positive control: a situation that is not sensitive can be automatic, in both modes, for the organization and for a property.
        foreach (var plain in AssistantSituationCatalog.All.Where(info => !NeverAutomatic.Contains(info.Situation)))
        {
            foreach (var mode in AutomaticModes)
            {
                Assert.True(AssistantModeRules.IsAllowed(plain.Situation, mode));
                Assert.Equal(mode, (await service.SaveAsync(world.OrgId, null, plain.Situation, mode, null, null, Host)).Mode);
                Assert.Equal(mode, (await service.SaveAsync(world.OrgId, world.Granted.Id, plain.Situation, mode, null, null, Host)).Mode);
            }
        }

        // A row that breaks the rule, written by hand (or by a version that did not know it), is read as a draft and never as an automatic one.
        var stale = world.Hidden.Id;
        db.AssistantSituationSettings.AddRange(
            new AssistantSituationSetting { OrgId = world.OrgId, Situation = AssistantSituation.Complaint, Mode = AssistantMode.Automatic, ModeSince = AssistantTestKit.Now.UtcDateTime },
            new AssistantSituationSetting { OrgId = world.OrgId, PropertyId = stale, Situation = AssistantSituation.Emergency, Mode = AssistantMode.AutomaticWithExceptions, ModeSince = AssistantTestKit.Now.UtcDateTime });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(AssistantMode.Draft, (await service.ResolveAsync(world.OrgId, null, AssistantSituation.Complaint)).Mode);
        Assert.Equal(AssistantMode.Draft, (await service.ResolveAsync(world.OrgId, stale, AssistantSituation.Emergency)).Mode);
        Assert.Equal(AssistantMode.Draft, (await service.ListEffectiveAsync(world.OrgId, null)).Single(s => s.Situation == AssistantSituation.Complaint).Mode);
        Assert.Equal(AssistantMode.Draft, AssistantModeRules.Effective(AssistantSituation.Complaint, AssistantMode.Automatic));
        Assert.Equal(AssistantMode.Escalate, AssistantModeRules.Effective(AssistantSituation.Complaint, AssistantMode.Escalate));
    }

    [Fact]
    public void Validate_AValueThatIsNoMode_IsRefusedWhateverTheSituation()
    {
        foreach (var info in AssistantSituationCatalog.All)
        {
            Assert.False(AssistantModeRules.IsAllowed(info.Situation, (AssistantMode)9));
            Assert.Equal(AssistantModeRules.ModeNotAllowedCode, Assert.Throws<DomainRuleException>(() => AssistantModeRules.Validate(info.Situation, (AssistantMode)9)).Code);
        }
    }

    [Fact]
    public void Effective_APlainSituationKeepsItsStoredMode_EveryOneOfTheFour()
    {
        foreach (var mode in Enum.GetValues<AssistantMode>())
            Assert.Equal(mode, AssistantModeRules.Effective(AssistantSituation.Parking, mode));
    }

    [Fact]
    public void TrustPeriodElapsed_AnUnknownStartIsNeverAnElapsedPeriod_FailClosed()
    {
        var now = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var fortnight = TimeSpan.FromDays(14);

        // No start (a situation nobody saved has no row): never elapsed, whatever the period and however late it is asked.
        Assert.False(AssistantModeRules.TrustPeriodElapsed(null, now, fortnight));
        Assert.False(AssistantModeRules.TrustPeriodElapsed(null, now.AddYears(50), fortnight));
        Assert.False(AssistantModeRules.TrustPeriodElapsed(null, now, TimeSpan.Zero));

        // A known start: elapsed from the moment the period is full and not before, and never for a start that is still to come.
        Assert.False(AssistantModeRules.TrustPeriodElapsed(now.AddDays(-14).AddTicks(1), now, fortnight));
        Assert.True(AssistantModeRules.TrustPeriodElapsed(now.AddDays(-14), now, fortnight));
        Assert.True(AssistantModeRules.TrustPeriodElapsed(now.AddDays(-30), now, fortnight));
        Assert.False(AssistantModeRules.TrustPeriodElapsed(now.AddDays(1), now, fortnight));
    }

    [Fact]
    public void IsAutomatic_IsTheTwoModesThatSendByThemselves()
    {
        Assert.True(AssistantModeRules.IsAutomatic(AssistantMode.Automatic));
        Assert.True(AssistantModeRules.IsAutomatic(AssistantMode.AutomaticWithExceptions));
        Assert.False(AssistantModeRules.IsAutomatic(AssistantMode.Draft));
        Assert.False(AssistantModeRules.IsAutomatic(AssistantMode.Escalate));
    }
}
