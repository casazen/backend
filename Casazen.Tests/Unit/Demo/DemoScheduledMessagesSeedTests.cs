using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

/// <summary>
/// E05/F07/US01/T02: the scheduled messages the showcase writes for the demo host: the ready routine «Messaggio di arrivo» with the real id of the template it sends, and one message in each state the host meets on
/// the timeline of a stay (planned, held back, skipped by the owner). The rows are written by the real seed code over the stays of the catalog (the markers the reset leaves) and are the rows the plan would have
/// written itself, at the time it would have written them: the plan finds nothing to add or to change. They are marked as showcase data, the reset deletes only those of the org it is run for, and nothing is invented
/// for a template the host already had.
/// </summary>
public class DemoScheduledMessagesSeedTests
{
    private static readonly Guid OrgId = ScheduledMessageRig.OrgA;

    private static readonly Guid OtherOrgId = ScheduledMessageRig.OrgB;

    private const string OwnerUserId = "auth0|titolare-demo";

    private static DemoEnvironmentService Seeder(AppDbContext db) => new(
        db,
        Mock.Of<IFileStorage>(),
        Mock.Of<IPropertyComplianceStatusService>(),
        Options.Create(new ComplianceOptions()),
        Mock.Of<IDemoEnvironmentGate>(),
        NullLogger<DemoEnvironmentService>.Instance,
        new FixedTimeProvider(ScheduledMessageRig.Now),
        Mock.Of<IServiceScopeFactory>(),
        Mock.Of<ICrmContactIndex>(),
        Mock.Of<IFeatureFlags>(),
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Development));

    private static DemoRoutineTemplate TheRoutine => Assert.Single(DemoShowcaseCatalog.Routines, r => r.Scenario == DemoShowcaseCatalog.Scenario.ScheduledMessages);

    private static DemoHostMessageTemplate TheTemplate => Assert.Single(DemoShowcaseCatalog.HostMessageTemplates, t => t.Key == TheRoutine.MessageTemplateKey);

    /// <summary>The stays of the three messages, as the reset creates them (a direct stay of one house, the check-in as many days from now as the catalog says), with the markers it leaves.</summary>
    private static async Task<ScheduledMessageRig> ShowcaseAsync(Guid orgId = default)
    {
        var rig = new ScheduledMessageRig();
        var org = orgId == default ? OrgId : orgId;
        var house = await rig.SeedPropertyAsync(org, "Casa Trastevere (TEST)");
        var today = RomeCalendar.DateInRome(ScheduledMessageRig.Now.UtcDateTime);
        await using var db = rig.NewDb();
        db.TestDataMarkers.Add(Marker(org, TestDataEntities.Property, house.Id, "casa-trastevere"));
        foreach (var message in DemoShowcaseCatalog.ScheduledMessages)
        {
            var template = DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings).Single(b => b.Key == message.BookingKey);
            var arrival = today.AddDays(template.CheckInOffsetDays).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var booking = await rig.SeedBookingAsync(house, BookingStatus.Confirmed, BookingSource.Direct, arrival);
            db.TestDataMarkers.Add(Marker(org, TestDataEntities.Booking, booking.Id, message.BookingKey));
        }

        await db.SaveChangesAsync();
        return rig;
    }

    private static TestDataMarker Marker(Guid orgId, string entityType, Guid entityId, string templateKey) => new()
    {
        OrgId = orgId,
        EntityType = entityType,
        EntityId = entityId,
        Origin = TestDataOrigins.Showcase,
        TemplateKey = templateKey,
    };

    /// <summary>What the reset does for these three things, in its order: the templates, the routine, the messages.</summary>
    private static async Task SeedAllAsync(AppDbContext db, Guid orgId)
    {
        var seeder = Seeder(db);
        foreach (var template in DemoShowcaseCatalog.HostMessageTemplates)
            await seeder.CreateHostMessageTemplateAsync(orgId, template, CancellationToken.None);
        await seeder.CreateRoutineAsync(orgId, TheRoutine, CancellationToken.None);
        foreach (var message in DemoShowcaseCatalog.ScheduledMessages)
            await seeder.CreateScheduledMessageAsync(orgId, OwnerUserId, message, CancellationToken.None);
    }

    [Fact]
    public void TheCatalog_HasTheReadyRoutineAndOneMessageInEachState_OnDirectStaysThatTheCatalogHas_AllSayingTest()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.ScheduledMessages, DemoShowcaseCatalog.Scenarios);
        var routine = TheRoutine;
        Assert.False(routine.IsTestMode);
        Assert.True(routine.Active);
        Assert.Contains("TEST", routine.Name, StringComparison.Ordinal);
        Assert.Equal((RoutineEventType.BeforeCheckin, 1), (routine.EventType, routine.DaysBefore));
        var action = Assert.Single(routine.Actions);
        Assert.Equal(RoutineActionType.MessageToGuest, action.Type);
        Assert.Contains(DemoShowcaseCatalog.MessageTemplatePlaceholderId, action.ConfigJson, StringComparison.Ordinal);
        Assert.Contains("TEST", TheTemplate.Body, StringComparison.Ordinal);

        // The template uses the variables the plan fills: the arrival (how to get in, the link, the code, the address).
        Assert.Contains(MessageTemplateRenderer.CheckInMethodVariable, MessageTemplateRenderer.VariablesOf(TheTemplate.Body));
        Assert.Contains(MessageTemplateRenderer.AccessCodeVariable, MessageTemplateRenderer.VariablesOf(TheTemplate.Body));

        var messages = DemoShowcaseCatalog.ScheduledMessages;
        Assert.Equal(messages.Count, messages.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [ScheduledMessageStatus.Scheduled, ScheduledMessageStatus.Skipped, ScheduledMessageStatus.Blocked],
            messages.Select(m => m.Status).Order());
        Assert.All(messages, message =>
        {
            Assert.Equal(DemoShowcaseCatalog.Scenario.ScheduledMessages, message.Scenario);
            Assert.Equal(routine.Key, message.RoutineKey);

            // The stay is one of the house the catalog says, a direct one that is confirmed and still to come (a message is for a guest the host can write to).
            var house = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == message.PropertyKey);
            var stay = Assert.Single(house.Bookings, b => b.Key == message.BookingKey);
            Assert.Equal((BookingSource.Direct, BookingStatus.Confirmed), (stay.Source, stay.Status));
            Assert.True(stay.CheckInOffsetDays > 1, $"{message.Key}: the stay is too near for the message to be planned");
            Assert.False(stay.NoShow);
            Assert.Single(DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings), b => b.Key == message.BookingKey);
        });

        // A message held back says what it waits for: the code of the lock. The house has its check-in method written (the variable that comes before in the text) and the showcase gives it no code, so it is the
        // true reason, the one the dispatch would find.
        var held = Assert.Single(messages, m => m.Status == ScheduledMessageStatus.Blocked);
        Assert.Equal(ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.AccessCodeVariable), held.Reason);
        Assert.NotNull(Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == held.PropertyKey).SiteProfile?.CheckInMethod);
        Assert.Equal(ScheduledMessageReasons.Person, Assert.Single(messages, m => m.Status == ScheduledMessageStatus.Skipped).Reason);
        Assert.Null(Assert.Single(messages, m => m.Status == ScheduledMessageStatus.Scheduled).Reason);
    }

    [Fact]
    public async Task TheReset_WritesTheRoutineWithTheRealIdOfItsTemplate_AndTheThreeMessages_MarkedAsShowcaseData()
    {
        var rig = await ShowcaseAsync();
        await using var db = rig.NewDb();

        await SeedAllAsync(db, OrgId);

        // The routine sends the template the showcase wrote, not the placeholder of the catalog, and is a routine the plan reads.
        var template = await db.MessageTemplates.AsNoTracking().SingleAsync(t => t.OrgId == OrgId && t.Name == TheTemplate.Name);
        var routine = await db.Routines.AsNoTracking().SingleAsync(r => r.OrgId == OrgId);
        Assert.True(routine.Active);
        Assert.False(routine.TestMode);
        Assert.DoesNotContain(DemoShowcaseCatalog.MessageTemplatePlaceholderId, routine.ActionsJson, StringComparison.Ordinal);
        Assert.True(RoutineAction.TryParseList(routine.ActionsJson, out var actions));
        Assert.Equal(template.Id.ToString(), Assert.Single(actions).GetString("templateId"));
        Assert.True(RoutineActionRules.HasRequiredFields(actions[0]));

        var rows = await db.ScheduledMessages.AsNoTracking().Where(m => m.OrgId == OrgId).OrderBy(m => m.PlannedAtUtc).ToListAsync();
        Assert.Equal(DemoShowcaseCatalog.ScheduledMessages.Count, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(routine.Id, row.RoutineId);
            Assert.Equal(routine.Name, row.RoutineName);
            Assert.Equal(template.Id.ToString(), row.TemplateRef);
            Assert.Equal(0, row.ActionIndex);
        });
        var planned = Assert.Single(rows, r => r.Status == ScheduledMessageStatus.Scheduled);
        var held = Assert.Single(rows, r => r.Status == ScheduledMessageStatus.Blocked);
        var skipped = Assert.Single(rows, r => r.Status == ScheduledMessageStatus.Skipped);
        Assert.Equal(ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.AccessCodeVariable), held.BlockedReason);
        Assert.Equal((ScheduledMessageReasons.Person, OwnerUserId), (skipped.SkipReason, skipped.SkippedByUserId));
        Assert.NotNull(skipped.SkippedAt);
        Assert.Null(planned.BlockedReason);
        Assert.Null(planned.SkipReason);

        // Every row is marked as showcase data, with the key of its template: the reset finds them by it.
        var markers = await db.TestDataMarkers.AsNoTracking().Where(m => m.OrgId == OrgId && m.EntityType == TestDataEntities.ScheduledMessage).ToListAsync();
        Assert.Equal(rows.Select(r => r.Id).Order(), markers.Select(m => m.EntityId).Order());
        Assert.All(markers, m => Assert.Equal(TestDataOrigins.Showcase, m.Origin));
        Assert.Equal(DemoShowcaseCatalog.ScheduledMessages.Select(m => m.Key).Order(StringComparer.Ordinal), markers.Select(m => m.TemplateKey!).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The rows of the showcase are the ones the plan would have written, at the time it would have written them: planning each stay again writes nothing and changes nothing, so the demo shows the timeline the
    /// product makes (the day before the check-in, at its time on the clock of the house) and the first run of the hourly plan does not duplicate or move a message.
    /// </summary>
    [Fact]
    public async Task ThePlan_FindsTheRowsOfTheShowcaseWhereItWouldHaveWrittenThem_AndChangesNothing()
    {
        var rig = await ShowcaseAsync();
        await using (var db = rig.NewDb())
            await SeedAllAsync(db, OrgId);
        var before = await ScheduledMessagesAsync(rig);
        var stays = await StaysAsync(rig);

        foreach (var stay in stays)
        {
            await using var db = rig.NewDb();
            Assert.Equal(0, await rig.Service(db).PlanBookingAsync(stay));
        }

        var after = await ScheduledMessagesAsync(rig);
        Assert.Equal(before.Select(r => (r.Id, r.Status, r.PlannedAtUtc)), after.Select(r => (r.Id, r.Status, r.PlannedAtUtc)));
        var planned = Assert.Single(after, r => r.Status == ScheduledMessageStatus.Scheduled);

        // The planned one is for the stay of the nearest check-in: the day before at 15:00 in Rome (14:00 UTC).
        var nearest = DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings).Single(b => b.Key == DemoShowcaseCatalog.ScheduledMessages.Single(m => m.Status == ScheduledMessageStatus.Scheduled).BookingKey);
        var checkIn = RomeCalendar.DateInRome(ScheduledMessageRig.Now.UtcDateTime).AddDays(nearest.CheckInOffsetDays);
        Assert.Equal(checkIn.AddDays(-1).ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc), planned.PlannedAtUtc);
    }

    [Fact]
    public async Task TheReset_DeletesOnlyTheShowcaseMessagesOfTheOrg_NotAMessageOfTheHost_NorThoseOfAnotherOrg_AndWritesTheSameAgain()
    {
        var rig = await ShowcaseAsync();
        await using var db = rig.NewDb();
        await SeedAllAsync(db, OrgId);

        // A message of the host (a stay of its own, no marker) and the showcase of another org.
        var routine = await db.Routines.AsNoTracking().SingleAsync(r => r.OrgId == OrgId);
        var ownStay = await rig.SeedBookingAsync(await db.Properties.AsNoTracking().SingleAsync(p => p.OrgId == OrgId));
        var hostRow = await rig.SeedRowAsync(ownStay, routine, await db.MessageTemplates.AsNoTracking().FirstAsync(t => t.OrgId == OrgId), ScheduledMessageStatus.Sent);
        var otherShowcase = new ScheduledMessage { OrgId = OtherOrgId, PropertyId = Guid.NewGuid(), BookingId = Guid.NewGuid(), RoutineName = "Altro (TEST)", TemplateRef = Guid.NewGuid().ToString(), PlannedAtUtc = ScheduledMessageRig.Now.UtcDateTime };
        db.ScheduledMessages.Add(otherShowcase);
        db.TestDataMarkers.Add(Marker(OtherOrgId, TestDataEntities.ScheduledMessage, otherShowcase.Id, "arrivo-pianificato"));
        await db.SaveChangesAsync();

        await Seeder(db).RemoveShowcaseScheduledMessagesAsync(OrgId, CancellationToken.None);

        var left = await db.ScheduledMessages.AsNoTracking().OrderBy(m => m.OrgId).ToListAsync();
        Assert.Equal(new[] { hostRow.Id, otherShowcase.Id }.Order(), left.Select(m => m.Id).Order());
        Assert.Empty(await db.TestDataMarkers.AsNoTracking().Where(m => m.OrgId == OrgId && m.EntityType == TestDataEntities.ScheduledMessage).ToListAsync());
        Assert.Single(await db.TestDataMarkers.AsNoTracking().Where(m => m.OrgId == OtherOrgId && m.EntityType == TestDataEntities.ScheduledMessage).ToListAsync());

        // Run again after the removal (the routine and the template of the first run are still there): the same three messages are written, once each.
        foreach (var message in DemoShowcaseCatalog.ScheduledMessages)
            await Seeder(db).CreateScheduledMessageAsync(OrgId, OwnerUserId, message, CancellationToken.None);

        Assert.Equal(DemoShowcaseCatalog.ScheduledMessages.Count, await db.ScheduledMessages.AsNoTracking().CountAsync(m => m.OrgId == OrgId && m.Id != hostRow.Id));
    }

    [Fact]
    public async Task ATemplateTheHostAlreadyHad_IsNeverReplaced_SoTheRoutineThatSendsItAndItsMessagesAreNotWritten()
    {
        var rig = await ShowcaseAsync();
        await rig.SeedTemplateAsync(OrgId, "Il mio testo (TEST)", name: TheTemplate.Name, language: TheTemplate.Language);
        await using var db = rig.NewDb();

        await SeedAllAsync(db, OrgId);

        Assert.Equal("Il mio testo (TEST)", (await db.MessageTemplates.AsNoTracking().SingleAsync(t => t.OrgId == OrgId && t.Name == TheTemplate.Name)).Body);
        Assert.Empty(await db.Routines.AsNoTracking().Where(r => r.OrgId == OrgId).ToListAsync());
        Assert.Empty(await db.ScheduledMessages.AsNoTracking().Where(m => m.OrgId == OrgId).ToListAsync());
    }

    private static async Task<List<ScheduledMessage>> ScheduledMessagesAsync(ScheduledMessageRig rig)
    {
        await using var db = rig.NewDb();
        return await db.ScheduledMessages.AsNoTracking().OrderBy(m => m.PlannedAtUtc).ThenBy(m => m.Id).ToListAsync();
    }

    private static async Task<List<Guid>> StaysAsync(ScheduledMessageRig rig)
    {
        await using var db = rig.NewDb();
        return await db.Bookings.AsNoTracking().OrderBy(b => b.CheckInDate).Select(b => b.Id).ToListAsync();
    }
}
