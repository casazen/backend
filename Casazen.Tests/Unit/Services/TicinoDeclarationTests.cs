using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Configurations;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.SwissCompliance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E30/F02/US02/T01 (NF-128, casazen/backend#979): a Ticino day closes with a declaration «I uploaded the file» as it closes with a receipt, the file of a closed day is generated again only on
/// an explicit confirmation (the portal may create duplicates), every generation leaves an audit row with no personal data, and the hosts of the property learn a declaration live.
/// The rules that cost if they fail are the three proofs: another organization never declares nor sees a declaration (<c>SwissComplianceControllerTests.Declare_OtherOrganizationForbidden_Test</c>
/// for the web layer, <see cref="Declaration_IsIsolatedPerOrganization_AndAnotherOrgsPropertyIsNotFound_Test"/> for the data), the regeneration guard
/// (<see cref="Regenerate_DeclaredDay_RequiresConfirmation_Test"/>) and the audit row without personal data (<see cref="FileEvent_NoPersonalData_Test"/>).
/// </summary>
public class TicinoDeclarationTests
{
    private const string Anna = "auth0|anna";
    private const string Bruno = "auth0|bruno";
    private static readonly DateTime Day = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly ArrivalDay = DateOnly.FromDateTime(Day);
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero));
    private static readonly DateTime Now = Clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Declare_IsIdempotent_TheSecondCallAnswersTheFirstDeclaration_AndTellsNobodyAgain()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddUser(db, Anna, "Anna", "Rossi");
        AddUser(db, Bruno, "Bruno", "Verdi");
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var push = new RecordingPush();
        var first = new TicinoNotificationService(db, Clock, pushNotifications: push);

        var later = new TicinoNotificationService(db, new FixedTimeProvider(Clock.GetUtcNow().AddMinutes(5)), pushNotifications: push);

        var created = await first.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        var repeated = await later.DeclareDayAsync(property.Id, ArrivalDay, Bruno);

        Assert.True(created.Created);
        Assert.False(repeated.Created);
        Assert.Equal((Now, "Anna Rossi"), (repeated.DeclaredAtUtc, repeated.DeclaredBy));
        var row = Assert.Single(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal((property.OrgId, property.Id, ArrivalDay, Anna), (row.OrgId, row.PropertyId, row.ArrivalDay, row.DeclaredByUserId));
        Assert.Equal("portal", row.Channel);
        Assert.Null(row.ReceiptId);
        Assert.Null(row.RegimeCode);
        Assert.Single(push.Sent);
    }

    [Fact]
    public async Task Declare_ClosesTheDay_ThePendingListNoLongerHoldsIt_AndTheDayShowsWhoAndWhen()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddUser(db, Anna, "Anna", "Rossi");
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        var pendingBefore = await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay);
        var dayBefore = await service.GetDayAsync(property.Id, ArrivalDay);
        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        var pendingAfter = await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay);
        var everyOrgAfter = await service.ListPendingDaysAsync(null, null, ArrivalDay);
        var dayAfter = await service.GetDayAsync(property.Id, ArrivalDay);

        Assert.Equal(ArrivalDay, Assert.Single(pendingBefore).Day);
        Assert.False(dayBefore.AlreadyDeclared);
        Assert.Null(dayBefore.DeclaredAtUtc);
        Assert.Null(dayBefore.DeclaredBy);
        Assert.Equal(ArrivalDay, Assert.Single(dayBefore.PendingDays).Day);

        Assert.Empty(pendingAfter);
        Assert.Empty(everyOrgAfter);
        Assert.Empty(dayAfter.PendingDays);
        Assert.True(dayAfter.AlreadyDeclared);
        Assert.Equal((Now, "Anna Rossi"), (dayAfter.DeclaredAtUtc, dayAfter.DeclaredBy));
        Assert.Equal(0, dayAfter.ReceiptCount);
    }

    [Fact]
    public async Task Declare_AccountThatNoLongerExists_ShowsNoName_ButStillClosesTheDay()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        var declaration = await service.DeclareDayAsync(property.Id, ArrivalDay, "auth0|erased");

        Assert.Null(declaration.DeclaredBy);
        Assert.Equal("auth0|erased", (await db.SwissNotificationDeclarations.AsNoTracking().SingleAsync()).DeclaredByUserId);
        Assert.Empty(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay));
        Assert.Null((await service.GetDayAsync(property.Id, ArrivalDay)).DeclaredBy);
    }

    [Fact]
    public async Task Declare_AFutureDay_IsRefused_BecauseItWouldSilenceTheAlertsOfAnArrivalThatHasNotHappened()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: new RecordingPush());

        var tomorrow = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareDayAsync(property.Id, ArrivalDay.AddDays(1), Anna));
        var today = await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);

        Assert.Equal("ticino_declaration_day_in_future", tomorrow.Code);
        Assert.True(today.Created);
        Assert.Equal(ArrivalDay, Assert.Single(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync()).ArrivalDay);
    }

    [Theory]
    [InlineData("IT")]
    [InlineData("CH", "ZH")]
    public async Task Declare_AndUndo_ForAPropertyThatIsNotInTicino_AreRefused(string country, string canton = "TI")
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, country, canton);
        var service = new TicinoNotificationService(db, Clock);

        var declare = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareDayAsync(property.Id, ArrivalDay, Anna));
        var undo = await Assert.ThrowsAsync<DomainRuleException>(() => service.UndeclareDayAsync(property.Id, ArrivalDay, Anna));

        Assert.Equal("property_not_in_ticino", declare.Code);
        Assert.Equal("property_not_in_ticino", undo.Code);
        Assert.Empty(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Undo_RemovesTheDeclaration_TheDayIsToNotifyAgain_AndRepeatingItIsHarmless()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddUser(db, Anna, "Anna", "Rossi");
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var push = new RecordingPush();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

        var nothingToUndo = await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);
        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        var undone = await service.UndeclareDayAsync(property.Id, ArrivalDay, Bruno);
        var undoneAgain = await service.UndeclareDayAsync(property.Id, ArrivalDay, Bruno);

        Assert.False(nothingToUndo);
        Assert.True(undone);
        Assert.False(undoneAgain);
        Assert.Empty(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal(ArrivalDay, Assert.Single(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay)).Day);
        var day = await service.GetDayAsync(property.Id, ArrivalDay);
        Assert.False(day.AlreadyDeclared);
        Assert.Null(day.DeclaredAtUtc);

        Assert.Equal(
            [PushTypes.TicinoNotificationDeclared, PushTypes.TicinoNotificationDeclared],
            push.Sent.Select(p => p.Payload.Type).ToArray());
    }

    [Fact]
    public async Task Undo_ADeclaredDayCanBeDeclaredAgain_AndEveryEventHasItsOwnPushKey()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var push = new RecordingPush();
        var time = new MovableTimeProvider(Clock.GetUtcNow());
        var service = new TicinoNotificationService(db, time, pushNotifications: push);

        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);
        time.Advance(TimeSpan.FromMinutes(1));
        var again = await service.DeclareDayAsync(property.Id, ArrivalDay, Bruno);

        Assert.True(again.Created);
        Assert.Equal(time.GetUtcNow().UtcDateTime, again.DeclaredAtUtc);
        Assert.Equal(3, push.Sent.Count);
        Assert.Equal(3, push.Sent.Select(p => p.Key).Distinct().Count());
        Assert.All(push.Sent, p => Assert.InRange(p.Key.Length, 1, InAppNotification.DeliveryKeyMaxLength));
    }

    [Fact]
    public async Task Undo_ACoHostWhoUndoesTheSameDeclarationAtTheSameMoment_GetsTheIdempotentAnswer_AndTellsNobody()
    {
        var store = Guid.NewGuid().ToString();
        Property property;
        await using (var setup = NewDb(store))
        {
            property = await SeedPropertyAsync(setup);
            setup.SwissNotificationDeclarations.Add(
                new SwissNotificationDeclaration
                {
                    OrgId = property.OrgId,
                    PropertyId = property.Id,
                    ArrivalDay = ArrivalDay,
                    DeclaredByUserId = Bruno,
                    DeclaredAt = Now.AddSeconds(-1),
                });
            await setup.SaveChangesAsync();
        }

        await using var db = new VanishingRowDbContext(store, () => RemoveEveryDeclaration(store));
        var push = new RecordingPush();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

        var undone = await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);

        Assert.False(undone);
        Assert.Empty(push.Sent);
        await using var check = NewDb(store);
        Assert.Empty(await check.SwissNotificationDeclarations.AsNoTracking().ToListAsync());

        Assert.Empty(db.ChangeTracker.Entries<SwissNotificationDeclaration>());
    }

    [Fact]
    public async Task Receipt_AlsoClosesTheDay_AndStillDoesAfterTheDeclarationIsUndone()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);
        var pendingBefore = await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay);

        AddReceipt(db, property, "Police");
        await db.SaveChangesAsync();
        var withReceipt = await service.GetDayAsync(property.Id, ArrivalDay);

        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);
        var afterUndo = await service.GetDayAsync(property.Id, ArrivalDay);

        Assert.Single(pendingBefore);
        Assert.Empty(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay));
        Assert.Equal((1, true, null), (withReceipt.ReceiptCount, withReceipt.AlreadyDeclared, withReceipt.DeclaredAtUtc));
        Assert.Equal((1, true, null), (afterUndo.ReceiptCount, afterUndo.AlreadyDeclared, afterUndo.DeclaredAtUtc));
    }

    [Fact]
    public async Task AnOtrReceiptOrADocumentOfAnotherDay_DoesNotCloseTheDay()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddStay(db, property, BookingStatus.CheckedIn);
        AddReceipt(db, property, "OTR");
        AddReceipt(db, property, "GroupSheet");
        AddReceipt(db, property, "Police", Day.AddDays(-1));
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        Assert.Equal(ArrivalDay, Assert.Single(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay)).Day);
        Assert.False((await service.GetDayAsync(property.Id, ArrivalDay)).AlreadyDeclared);
    }

    [Fact]
    public async Task Declare_WithAReceipt_AttachesItOnlyWhenItIsAPoliceReceiptOfThePropertyAndTheDay()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var other = await SeedPropertyAsync(db);
        var good = AddReceipt(db, property, "Police");
        var otherDay = AddReceipt(db, property, "Police", Day.AddDays(-1));
        var otr = AddReceipt(db, property, "OTR");
        var otherProperty = AddReceipt(db, other, "Police");
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: new RecordingPush());

        foreach (var bad in new[] { otherDay, otr, otherProperty, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareDayAsync(property.Id, ArrivalDay, Anna, bad));
            Assert.Equal("ticino_declaration_receipt_invalid", refused.Code);
        }

        Assert.Empty(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());

        var declared = await service.DeclareDayAsync(property.Id, ArrivalDay, Anna, good);

        Assert.True(declared.Created);
        Assert.Equal(good, declared.ReceiptId);
        Assert.Equal(good, (await db.SwissNotificationDeclarations.AsNoTracking().SingleAsync()).ReceiptId);
    }

    [Fact]
    public async Task Declare_ARepeatedCallWithAReceipt_AttachesItToTheDeclarationThatExists_WithoutChangingWhoAndWhen_AndNeverRemovesIt()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddUser(db, Anna, "Anna", "Rossi");
        var receipt = AddReceipt(db, property, "Police");
        await db.SaveChangesAsync();
        var push = new RecordingPush();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        var attached = await service.DeclareDayAsync(property.Id, ArrivalDay, Bruno, receipt);
        var withoutReceipt = await service.DeclareDayAsync(property.Id, ArrivalDay, Bruno);

        Assert.False(attached.Created);
        Assert.Equal(receipt, attached.ReceiptId);
        Assert.Equal("Anna Rossi", attached.DeclaredBy);
        Assert.Equal(receipt, withoutReceipt.ReceiptId);
        var row = Assert.Single(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal((Anna, receipt), (row.DeclaredByUserId, row.ReceiptId));
        Assert.Single(push.Sent);
    }

    [Fact]
    public async Task Declare_ARepeatedCallWithAnotherReceipt_NeverReplacesTheReceiptAlreadyAttached()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddUser(db, Anna, "Anna", "Rossi");
        var first = AddReceipt(db, property, "Police");
        var second = AddReceipt(db, property, "Police");
        await db.SaveChangesAsync();
        var push = new RecordingPush();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna, first);
        var repeated = await service.DeclareDayAsync(property.Id, ArrivalDay, Bruno, second);
        var sameAgain = await service.DeclareDayAsync(property.Id, ArrivalDay, Bruno, first);

        Assert.False(repeated.Created);
        Assert.Equal(first, repeated.ReceiptId);
        Assert.Equal(first, sameAgain.ReceiptId);
        var row = Assert.Single(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal((Anna, first), (row.DeclaredByUserId, row.ReceiptId));
        Assert.Single(push.Sent);
    }

    [Fact]
    public async Task Declare_AnUndoThatPassesBetweenTheReadAndTheSaveOfTheReceipt_LeavesTheDayDeclaredAgainByThisCall()
    {
        var store = Guid.NewGuid().ToString();
        Property property;
        Guid receipt;
        await using (var setup = NewDb(store))
        {
            property = await SeedPropertyAsync(setup);
            AddUser(setup, Anna, "Anna", "Rossi");
            receipt = AddReceipt(setup, property, "Police");
            setup.SwissNotificationDeclarations.Add(
                new SwissNotificationDeclaration
                {
                    OrgId = property.OrgId,
                    PropertyId = property.Id,
                    ArrivalDay = ArrivalDay,
                    DeclaredByUserId = Bruno,
                    DeclaredAt = Now.AddSeconds(-1),
                });
            await setup.SaveChangesAsync();
        }

        await using var db = new VanishingRowDbContext(store, () => RemoveEveryDeclaration(store));
        var push = new RecordingPush();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

        var answer = await service.DeclareDayAsync(property.Id, ArrivalDay, Anna, receipt);

        Assert.True(answer.Created);
        Assert.Equal((receipt, "Anna Rossi"), (answer.ReceiptId, answer.DeclaredBy));
        Assert.Single(push.Sent);
        await using var check = NewDb(store);
        var row = Assert.Single(await check.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal((Anna, receipt), (row.DeclaredByUserId, row.ReceiptId));
    }

    [Fact]
    public async Task Declaration_IsIsolatedPerOrganization_AndAnotherOrgsPropertyIsNotFound_Test()
    {
        var store = Guid.NewGuid().ToString();
        Property mine;
        Property theirs;
        await using (var system = NewDb(store))
        {
            mine = await SeedPropertyAsync(system);
            theirs = await SeedPropertyAsync(system);
            AddStay(system, mine, BookingStatus.CheckedIn);
            AddStay(system, theirs, BookingStatus.CheckedIn);
            AddUser(system, Bruno, "Bruno", "Verdi");
            await system.SaveChangesAsync();

            await new TicinoNotificationService(system, Clock).DeclareDayAsync(theirs.Id, ArrivalDay, Bruno);
            system.SwissNotificationDeclarations.Add(
                new SwissNotificationDeclaration { OrgId = theirs.OrgId, PropertyId = mine.Id, ArrivalDay = ArrivalDay, DeclaredByUserId = Bruno });
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(store, new FixedTenantContext(mine.OrgId));
        var push = new RecordingPush();
        var service = new TicinoNotificationService(asOrgA, Clock, pushNotifications: push);

        var declareTheirs = await Assert.ThrowsAsync<NotFoundException>(() => service.DeclareDayAsync(theirs.Id, ArrivalDay, Anna));
        var undoTheirs = await Assert.ThrowsAsync<NotFoundException>(() => service.UndeclareDayAsync(theirs.Id, ArrivalDay, Anna));
        var dayTheirs = await Assert.ThrowsAsync<NotFoundException>(() => service.GetDayAsync(theirs.Id, ArrivalDay));
        var generateTheirs = await Assert.ThrowsAsync<NotFoundException>(() => service.GenerateFileAsync(theirs.Id, ArrivalDay, TicinoPoliceFileFormat.Csv));

        Assert.All([declareTheirs.Code, undoTheirs.Code, dayTheirs.Code, generateTheirs.Code], code => Assert.Equal("property_not_found", code));
        Assert.Empty(push.Sent);

        var pending = await service.ListPendingDaysAsync(mine.OrgId, null, ArrivalDay);
        Assert.Equal((mine.Id, ArrivalDay), (Assert.Single(pending).PropertyId, pending[0].Day));
        var day = await service.GetDayAsync(mine.Id, ArrivalDay);
        Assert.False(day.AlreadyDeclared);
        Assert.Null(day.DeclaredAtUtc);

        await using var asSystem = NewDb(store);
        var job = new TicinoNotificationService(asSystem, Clock);
        Assert.Equal([mine.Id], (await job.ListPendingDaysAsync(null, null, ArrivalDay)).Select(p => p.PropertyId));
        Assert.Null((await job.GetDayAsync(mine.Id, ArrivalDay)).DeclaredAtUtc);
        Assert.NotNull((await job.GetDayAsync(theirs.Id, ArrivalDay)).DeclaredAtUtc);

        await service.DeclareDayAsync(mine.Id, ArrivalDay, Anna);
        Assert.Empty(await service.ListPendingDaysAsync(mine.OrgId, null, ArrivalDay));
        await service.UndeclareDayAsync(mine.Id, ArrivalDay, Anna);
        await using var system2 = NewDb(store);
        var theirRows = await system2.SwissNotificationDeclarations.AsNoTracking().Where(d => d.PropertyId == theirs.Id).ToListAsync();
        Assert.Equal(Bruno, Assert.Single(theirRows).DeclaredByUserId);
    }

    [Fact]
    public async Task ADeclarationOfAnotherRegime_DoesNotCloseTheDayOfTheTicinoPolice()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddStay(db, property, BookingStatus.CheckedIn);
        db.SwissNotificationDeclarations.Add(new SwissNotificationDeclaration
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            ArrivalDay = ArrivalDay,
            DeclaredByUserId = Anna,
            RegimeCode = "CH-ZH",
            Channel = "portal",
        });
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: new RecordingPush());

        Assert.Single(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay));
        Assert.False((await service.GetDayAsync(property.Id, ArrivalDay)).AlreadyDeclared);
        Assert.False(await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna));
        var declared = await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);

        Assert.True(declared.Created);
        Assert.Equal(2, await db.SwissNotificationDeclarations.AsNoTracking().CountAsync());
        Assert.Empty(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay));
    }

    [Fact]
    public async Task Declare_ACoHostWhoWinsTheRace_IsTheDeclarationThatIsAnswered_AndNothingIsToldTwice()
    {
        var store = Guid.NewGuid().ToString();
        Property property;
        await using (var setup = NewDb(store))
        {
            property = await SeedPropertyAsync(setup);
            AddUser(setup, Bruno, "Bruno", "Verdi");
            await setup.SaveChangesAsync();
        }

        await using var db = new RacingDbContext(
            store,
            () =>
            {
                using var winner = NewDb(store);
                winner.SwissNotificationDeclarations.Add(new SwissNotificationDeclaration
                {
                    OrgId = property.OrgId,
                    PropertyId = property.Id,
                    ArrivalDay = ArrivalDay,
                    DeclaredByUserId = Bruno,
                    DeclaredAt = Now.AddSeconds(-1),
                });
                winner.SaveChanges();
            });
        var push = new RecordingPush();
        var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

        var answer = await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);

        Assert.False(answer.Created);
        Assert.Equal(("Bruno Verdi", Now.AddSeconds(-1)), (answer.DeclaredBy, answer.DeclaredAtUtc));
        Assert.Empty(push.Sent);
        await using var check = NewDb(store);
        Assert.Equal(Bruno, Assert.Single(await check.SwissNotificationDeclarations.AsNoTracking().ToListAsync()).DeclaredByUserId);
    }

    [Fact]
    public async Task Declare_AndUndo_TellTheHostsOfThePropertyLive_WithThePropertyAndTheDayOnly()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, name: "Villa Rosa");
        AddUser(db, Anna, "Anna", "Rossi");
        await db.SaveChangesAsync();
        var push = new RecordingPush();
        var time = new MovableTimeProvider(Clock.GetUtcNow());
        var service = new TicinoNotificationService(db, time, pushNotifications: push);

        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);

        Assert.Equal(2, push.Sent.Count);
        var (declaredKey, declaredAudience, declared) = push.Sent[0];
        var (undoneKey, undoneAudience, undone) = push.Sent[1];
        Assert.Equal(PushAudience.PropertyHosts(property.Id), declaredAudience);
        Assert.Equal(PushAudience.PropertyHosts(property.Id), undoneAudience);
        Assert.All([declared, undone], payload =>
        {
            Assert.Equal(PushTypes.TicinoNotificationDeclared, payload.Type);
            Assert.Equal(PushRoutes.Properties, payload.Route);
            Assert.Null(payload.BookingId);
            Assert.Contains("Villa Rosa", payload.Body, StringComparison.Ordinal);
            Assert.Contains("18/11/2026", payload.Body, StringComparison.Ordinal);

            Assert.DoesNotContain("Anna", payload.Title + payload.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Rossi", payload.Title + payload.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("{", payload.Title + payload.Body, StringComparison.Ordinal);
        });
        Assert.NotEqual(declared.Title, undone.Title);

        Assert.Equal($"ticino-declaration:{property.Id:N}:20261118:declared:20261118150000000", declaredKey);
        Assert.Equal($"ticino-declaration:{property.Id:N}:20261118:undone:20261118150100000", undoneKey);
    }

    [Fact]
    public async Task Declare_APushThatCannotBeQueued_IsLogged_AndNeverFailsThePersonWhoDeclared()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddStay(db, property, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();

        foreach (var push in new[] { new RecordingPush(fail: true), new RecordingPush(refuse: true) })
        {
            var service = new TicinoNotificationService(db, Clock, pushNotifications: push);

            var declared = await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
            var undone = await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);

            Assert.True(declared.Created);
            Assert.True(undone);
        }

        Assert.Empty(await db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void TheBellEntryOfADeclaration_IsAboutTheProperty_LikeTheTwoAlerts()
    {
        var propertyId = Guid.NewGuid();
        var payload = new PushNotificationPayload("title", "body", PushTypes.TicinoNotificationDeclared, null, PushRoutes.Properties);

        var queued = QueuedInAppNotification.From(PushAudience.PropertyHosts(propertyId), payload, Now);

        Assert.Equal("ticino-notification-declared", PushTypes.TicinoNotificationDeclared);
        Assert.True(PushTypes.IsTicinoNotification(PushTypes.TicinoNotificationDeclared));
        Assert.Equal(propertyId, queued.EntityId);
        Assert.Equal(PushTypes.TicinoNotificationDeclared, queued.Type);
        Assert.InRange(PushTypes.TicinoNotificationDeclared.Length, 1, InAppNotification.TypeMaxLength);
        Assert.Equal(
            ["ticino-notification-declared", "ticino-notification-due", "ticino-notification-overdue"],
            typeof(PushTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
                .Select(f => (string)f.GetRawConstantValue()!)
                .Where(PushTypes.IsTicinoNotification)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void ThePushTextOfADeclaration_ExistsInBothLanguages_WithNoPlaceholderLeft(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var declared = EmailTemplates.TicinoNotificationDeclaredPush(culture, "Villa Rosa", ArrivalDay, declared: true);
        var undone = EmailTemplates.TicinoNotificationDeclaredPush(culture, "Villa Rosa", ArrivalDay, declared: false);

        foreach (var text in new[] { declared.Title, declared.Body, undone.Title, undone.Body })
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        }

        Assert.Contains("Villa Rosa", declared.Body, StringComparison.Ordinal);
        Assert.Contains("Villa Rosa", undone.Body, StringComparison.Ordinal);
        Assert.NotEqual(declared.Body, undone.Body);
    }

    [Fact]
    public void TheJsonOfTheClients_KeepsTheNamesOfTheBrief()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var declaredAt = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);
        var generatedAt = declaredAt.AddHours(-1);
        var closed = new TicinoNotificationDay(
            Guid.NewGuid(),
            ArrivalDay,
            "NL-12345678",
            1,
            new TicinoDayPlan([], []),
            0,
            declaredAt.AddHours(10),
            [],
            DeclaredAtUtc: declaredAt,
            DeclaredBy: "Anna Rossi",
            FileGeneratedAtUtc: generatedAt,
            FileGeneratedCount: 2);
        var open = closed with { DeclaredAtUtc = null, DeclaredBy = null, FileGeneratedAtUtc = null, FileGeneratedCount = 0 };

        var closedJson = JsonSerializer.SerializeToElement(TicinoNotificationDayResponse.From(closed), web);
        var openJson = JsonSerializer.SerializeToElement(TicinoNotificationDayResponse.From(open), web);

        Assert.Equal(declaredAt, closedJson.GetProperty("declaredAt").GetDateTime());
        Assert.Equal("Anna Rossi", closedJson.GetProperty("declaredBy").GetString());
        Assert.Equal(generatedAt, closedJson.GetProperty("fileGeneratedAt").GetDateTime());
        Assert.Equal(2, closedJson.GetProperty("fileGeneratedCount").GetInt32());
        Assert.True(closedJson.GetProperty("alreadyDeclared").GetBoolean());
        Assert.All(["declaredAt", "declaredBy", "fileGeneratedAt"], name => Assert.Equal(JsonValueKind.Null, openJson.GetProperty(name).ValueKind));
        Assert.Equal(0, openJson.GetProperty("fileGeneratedCount").GetInt32());
        Assert.False(openJson.GetProperty("alreadyDeclared").GetBoolean());

        var request = JsonSerializer.Deserialize<TicinoNotificationRequest>("""{"reportDate":"2026-11-18","format":"csv","confirmRegenerate":true}""", web)!;
        Assert.Equal((ArrivalDay, "csv", true), (request.ReportDate, request.Format, request.ConfirmRegenerate));
        Assert.False(JsonSerializer.Deserialize<TicinoNotificationRequest>("""{"reportDate":"2026-11-18"}""", web)!.ConfirmRegenerate);

        var receiptId = Guid.NewGuid();
        var declare = JsonSerializer.Deserialize<DeclareTicinoDayRequest>($$"""{"arrivalDay":"2026-11-18","receiptId":"{{receiptId}}"}""", web)!;
        Assert.Equal((ArrivalDay, receiptId), (declare.ArrivalDay, declare.ReceiptId));
        Assert.Null(JsonSerializer.Deserialize<DeclareTicinoDayRequest>("""{"arrivalDay":"2026-11-18"}""", web)!.ReceiptId);

        var answer = JsonSerializer.SerializeToElement(
            TicinoDeclarationResponse.From(new TicinoDeclaration(Guid.NewGuid(), ArrivalDay, declaredAt, "Anna Rossi", receiptId, Created: true)), web);
        Assert.Equal(
            ["arrivalDay", "created", "declaredAt", "declaredBy", "propertyId", "receiptId"],
            answer.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Regenerate_DeclaredDay_RequiresConfirmation_Test()
    {
        await using var db = NewDb();
        var withDeclaration = await SeedPropertyAsync(db, structureId: "NL-12345678");
        var withReceipt = await SeedPropertyAsync(db, structureId: "NL-12345678");
        var open = await SeedPropertyAsync(db, structureId: "NL-12345678");
        foreach (var property in new[] { withDeclaration, withReceipt, open })
            AddCompleteStay(db, property);
        AddReceipt(db, withReceipt, "Police");
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);
        await service.DeclareDayAsync(withDeclaration.Id, ArrivalDay, Anna);

        var declared = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.GenerateFileAsync(withDeclaration.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna)));
        var received = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.GenerateFileAsync(withReceipt.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna, ConfirmRegenerate: false)));
        var noRequest = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.GenerateFileAsync(withDeclaration.Id, ArrivalDay, TicinoPoliceFileFormat.Csv));

        Assert.Equal("ticino_day_already_declared", declared.Code);
        Assert.Equal("ticino_day_already_declared", received.Code);
        Assert.Equal("ticino_day_already_declared", noRequest.Code);
        Assert.Equal("TicinoDayAlreadyDeclared", declared.MessageKey);
        Assert.Empty(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());

        var first = await service.GenerateFileAsync(open.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna));
        var confirmedDeclared = await service.GenerateFileAsync(
            withDeclaration.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna, ConfirmRegenerate: true));
        var confirmedReceipt = await service.GenerateFileAsync(
            withReceipt.Id, ArrivalDay, TicinoPoliceFileFormat.Xml, new TicinoFileRequest(Bruno, ConfirmRegenerate: true));

        Assert.All([first, confirmedDeclared, confirmedReceipt], file => Assert.Equal(1, file.Rows));
        var events = await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Equal(
            new[] { (open.Id, "csv"), (withDeclaration.Id, "csv"), (withReceipt.Id, "xml") }.Order(),
            events.Select(e => (e.PropertyId, e.Format)).Order());
        Assert.Equal(
            new[] { (open.Id, Anna), (withDeclaration.Id, Anna), (withReceipt.Id, Bruno) }.Order(),
            events.Select(e => (e.PropertyId, e.CreatedByUserId!)).Order());
    }

    [Fact]
    public async Task Regenerate_AfterTheDeclarationIsUndone_NeedsNoConfirmationAgain()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, structureId: "NL-12345678");
        AddCompleteStay(db, property);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);
        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Csv));

        await service.UndeclareDayAsync(property.Id, ArrivalDay, Anna);
        var file = await service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna));

        Assert.Equal(1, file.Rows);
    }

    [Fact]
    public async Task Regenerate_ADayWithNothingToWrite_AnswersTheDataError_NotAConfirmationForAFileThatWouldNotExist()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, structureId: "NL-12345678");
        AddStay(db, property, BookingStatus.Confirmed);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);
        await service.DeclareDayAsync(property.Id, ArrivalDay, Anna);

        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Csv));

        Assert.Equal("ticino_notification_empty", empty.Code);
        Assert.Empty(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task EveryGeneration_WritesOneAuditRow_AndTheDayCountsTheFilesOfThePortalOnly()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, structureId: "NL-12345678");
        AddCompleteStay(db, property);
        var group = AddGroupStay(db, property);
        await db.SaveChangesAsync();
        var time = new MovableTimeProvider(Clock.GetUtcNow());
        var service = new TicinoNotificationService(db, time);

        var before = await service.GetDayAsync(property.Id, ArrivalDay);
        var csv = await service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna));
        time.Advance(TimeSpan.FromMinutes(10));
        var xml = await service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Xml, new TicinoFileRequest(Bruno, ConfirmRegenerate: false));
        time.Advance(TimeSpan.FromMinutes(10));
        var sheet = await service.GetGroupSheetAsync(property.Id, group.Id, new TicinoFileRequest(Anna));
        var after = await service.GetDayAsync(property.Id, ArrivalDay);

        Assert.Equal((0, (DateTime?)null), (before.FileGeneratedCount, before.FileGeneratedAtUtc));

        var events = (await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync()).OrderBy(e => e.CreatedAt).ToList();
        Assert.Equal(["csv", "xml", "groupSheet"], events.Select(e => e.Format));
        Assert.Equal([csv.FileHash, xml.FileHash, Sha256Of(sheet.Content)], events.Select(e => e.Sha256));
        Assert.Equal([csv.Rows, xml.Rows, sheet.Members], events.Select(e => e.RowCount));
        Assert.Equal([Anna, Bruno, Anna], events.Select(e => e.CreatedByUserId));
        Assert.All(events, e => Assert.Equal((property.OrgId, property.Id, ArrivalDay), (e.OrgId, e.PropertyId, e.ArrivalDay)));
        Assert.Equal(2, after.FileGeneratedCount);
        Assert.Equal(Now.AddMinutes(10), after.FileGeneratedAtUtc);
    }

    [Fact]
    public async Task AFileThatCannotBeGenerated_LeavesNoAuditRow()
    {
        await using var db = NewDb();
        var withoutId = await SeedPropertyAsync(db);
        var withId = await SeedPropertyAsync(db, structureId: "XNA9999");
        AddStay(db, withId, BookingStatus.Confirmed);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateFileAsync(withoutId.Id, ArrivalDay, TicinoPoliceFileFormat.Csv));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateFileAsync(withId.Id, ArrivalDay, TicinoPoliceFileFormat.Xml));

        Assert.Empty(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FileEvent_NoPersonalData_Test()
    {
        // PROOF TEST (privacy): the audit row of a file names the day, the format, the number of rows, the hash and the person who asked (the Auth0 subject): never a name, a document, an
        // address, a plate, a booking code or the name of the person who generated it. The file itself holds all of them, so the check is not empty.
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, structureId: "NL-12345678");
        AddUser(db, Anna, "Anna", "Rossi");
        var stay = AddStay(db, property, BookingStatus.CheckedIn, guests: 1);
        stay.BookingCode = "7Q3KZ8M2XB";
        stay.VehiclePlate = "TI4455667";
        db.StayGuests.Add(new StayGuest
        {
            BookingId = stay.Id,
            OrgId = property.OrgId,
            Position = 0,
            FirstName = "Zxqvgfhj",
            LastName = "Plmnbvcx",
            DateOfBirth = new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            CitizenshipCode = "100000241",
            CitizenshipName = "SVIZZERA",
            DocumentTypeCode = "PASOR",
            DocumentNumber = "QWERTY123456",
            ResidenceStreet = "Via Segretissima 99",
            ResidencePostalCode = "6900",
            ResidenceCityName = "Lugano",
        });
        var group = AddGroupStay(db, property);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        var file = await service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(Anna));
        var sheet = await service.GetGroupSheetAsync(property.Id, group.Id, new TicinoFileRequest(Anna));

        string[] personal =
        [
            "Zxqvgfhj", "Plmnbvcx", "QWERTY123456", "7Q3KZ8M2XB", BookingCodes.Format("7Q3KZ8M2XB"), "TI4455667", "Via Segretissima", "Lugano",
            "Qazwsxed", "Edcrfvtg", "Anna Rossi", "anna@example.com", "04.01.1984",
        ];
        var fileText = Encoding.Latin1.GetString(file.Content);
        Assert.Contains("Zxqvgfhj", fileText, StringComparison.Ordinal);
        Assert.Contains("QWERTY123456", fileText, StringComparison.Ordinal);
        Assert.Contains("Qazwsxed", Encoding.UTF8.GetString(sheet.Content), StringComparison.Ordinal);

        var events = await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count);
        foreach (var row in events)
        {
            var dump = string.Join(
                '|',
                typeof(SwissNotificationFileEvent).GetProperties().Select(p => Convert.ToString(p.GetValue(row), CultureInfo.InvariantCulture)));
            foreach (var value in personal)
                Assert.DoesNotContain(value, dump, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(Anna, row.CreatedByUserId);
            Assert.Matches("^[0-9a-f]{64}$", row.Sha256);
        }

        var csvRow = Assert.Single(events, e => e.Format == "csv");
        var sheetRow = Assert.Single(events, e => e.Format == "groupSheet");
        Assert.Equal((file.FileHash, file.Rows), (csvRow.Sha256, csvRow.RowCount));
        Assert.Equal((Sha256Of(sheet.Content), 6, ArrivalDay), (sheetRow.Sha256, sheetRow.RowCount, sheetRow.ArrivalDay));
    }

    [Fact]
    public void TheTrackingTables_HoldNothingButIdsCodesCountsHashesAndDates_AndTheListsOfValuesAreFixed()
    {
        // A tripwire like the one of the empty-org check: a new column on either table must make somebody decide again whether it is personal data.
        string[] Names(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["ArrivalDay", "Channel", "DeclaredAt", "DeclaredByUserId", "Id", "OrgId", "PropertyId", "ReceiptId", "RegimeCode"],
            Names(typeof(SwissNotificationDeclaration)));
        Assert.Equal(
            ["ArrivalDay", "CreatedAt", "CreatedByUserId", "Format", "Id", "OrgId", "PropertyId", "RowCount", "Sha256"],
            Names(typeof(SwissNotificationFileEvent)));

        Assert.Equal(["csv", "xml", "register", "groupSheet"], SwissNotificationFileFormats.All);
        Assert.Equal(["csv", "xml"], SwissNotificationFileFormats.PoliceFile);
        Assert.Equal("portal", SwissNotificationChannels.Portal);
        Assert.All(SwissNotificationFileFormats.All, format => Assert.InRange(format.Length, 1, SwissNotificationFileEvent.FormatMaxLength));
        Assert.InRange(SwissNotificationChannels.Portal.Length, 1, SwissNotificationDeclaration.ChannelMaxLength);
        Assert.Equal(2, new[] { typeof(SwissNotificationDeclaration), typeof(SwissNotificationFileEvent) }.Count(t => typeof(ITenantOwned).IsAssignableFrom(t)));
    }

    [Fact]
    public void TheModel_KeysTheDeclarationWithTwoPartialIndexes_SetsTheReceiptToNullWhenItIsRemoved_AndBoundsTheAudit()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres")
                .Options,
            NullTenantContext.Instance);
        var model = db.GetService<IDesignTimeModel>().Model;

        var declaration = model.FindEntityType(typeof(SwissNotificationDeclaration))!;
        var unique = declaration.GetIndexes().Where(i => i.IsUnique).ToDictionary(i => i.GetDatabaseName()!);
        Assert.Equal(2, unique.Count);
        Assert.Equal(
            ("\"RegimeCode\" IS NULL", "PropertyId,ArrivalDay"),
            (unique[SwissNotificationDeclaration.DefaultRegimeIndexName].GetFilter(), string.Join(',', unique[SwissNotificationDeclaration.DefaultRegimeIndexName].Properties.Select(p => p.Name))));
        Assert.Equal(
            ("\"RegimeCode\" IS NOT NULL", "PropertyId,ArrivalDay,RegimeCode"),
            (unique[SwissNotificationDeclaration.NamedRegimeIndexName].GetFilter(), string.Join(',', unique[SwissNotificationDeclaration.NamedRegimeIndexName].Properties.Select(p => p.Name))));

        var receiptKey = Assert.Single(declaration.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(SwissComplianceReceipt));
        Assert.Equal((DeleteBehavior.SetNull, false), (receiptKey.DeleteBehavior, receiptKey.IsRequired));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(declaration.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Property)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Assert.Single(declaration.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(OrgEntity)).DeleteBehavior);
        var audit = model.FindEntityType(typeof(SwissNotificationFileEvent))!;
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(audit.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Property)).DeleteBehavior);
        Assert.Equal(
            [SwissNotificationFileEventConfiguration.FormatCheckName, SwissNotificationFileEventConfiguration.RowCountCheckName],
            audit.GetCheckConstraints().Select(c => c.Name!).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("'groupSheet'", audit.GetCheckConstraints().Single(c => c.Name == SwissNotificationFileEventConfiguration.FormatCheckName).Sql, StringComparison.Ordinal);
    }

    private static AppDbContext NewDb(string? store = null, ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store ?? Guid.NewGuid().ToString()).Options, tenant);

    private static string Sha256Of(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void RemoveEveryDeclaration(string store)
    {
        using var winner = NewDb(store);
        winner.SwissNotificationDeclarations.RemoveRange(winner.SwissNotificationDeclarations.ToList());
        winner.SaveChanges();
    }

    private static async Task<Property> SeedPropertyAsync(
        AppDbContext db, string country = "CH", string canton = "TI", string? structureId = null, string name = "TEST")
    {
        var municipalityCode = canton == "TI" ? "5192" : "261";
        if (!await db.SwissMunicipalities.AnyAsync(m => m.MunicipalityCode == municipalityCode))
            db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = municipalityCode, CantonCode = canton, OfficialName = "TEST" });
        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = "test@example.com" };
        db.Orgs.Add(org);
        var property = new Property
        {
            OrgId = org.Id,
            Name = name,
            Country = country,
            SwissMunicipalityCode = municipalityCode,
            TicinoStructureId = structureId,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private static void AddUser(AppDbContext db, string id, string firstName, string lastName) =>
        db.Users.Add(new User { Id = id, Email = $"{firstName.ToLowerInvariant()}@example.com", FirstName = firstName, LastName = lastName });

    private static Booking AddStay(AppDbContext db, Property property, BookingStatus status, int guests = 2)
    {
        var guest = new Guest { OrgId = property.OrgId, FirstName = "TEST", LastName = "Guest" };
        db.Guests.Add(guest);
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            CheckInDate = Day,
            CheckOutDate = Day.AddDays(2),
            NumberOfGuests = guests,
        };
        db.Bookings.Add(booking);
        return booking;
    }

    private static void AddCompleteStay(AppDbContext db, Property property)
    {
        var stay = AddStay(db, property, BookingStatus.CheckedIn, guests: 1);
        db.StayGuests.Add(new StayGuest
        {
            BookingId = stay.Id,
            OrgId = property.OrgId,
            Position = 0,
            FirstName = "Anna",
            LastName = "Test",
            DateOfBirth = new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            CitizenshipCode = "100000241",
            CitizenshipName = "SVIZZERA",
            DocumentTypeCode = "PASOR",
            DocumentNumber = "X1234567",
            ResidenceInItaly = true,
            ResidencePostalCode = "6900",
        });
    }

    private static Booking AddGroupStay(AppDbContext db, Property property)
    {
        var group = AddStay(db, property, BookingStatus.CheckedIn, guests: 6);
        for (var position = 0; position < 6; position++)
        {
            db.StayGuests.Add(new StayGuest
            {
                BookingId = group.Id,
                OrgId = property.OrgId,
                Position = position,
                Type = position == 0 ? StayGuestType.HeadOfGroup : StayGuestType.GroupMember,
                FirstName = position == 0 ? "Qazwsxed" : "Edcrfvtg",
                LastName = $"Gruppo{position}",
                DateOfBirth = new DateTime(1990, 1, position + 1, 0, 0, 0, DateTimeKind.Utc),
                CitizenshipCode = "100000241",
                CitizenshipName = "SVIZZERA",
                DocumentTypeCode = position == 0 ? "PASOR" : null,
                DocumentNumber = position == 0 ? "X7654321" : string.Empty,
                ResidenceInItaly = position == 0 ? true : null,
                ResidencePostalCode = position == 0 ? "6900" : null,
            });
        }

        return group;
    }

    private static Guid AddReceipt(AppDbContext db, Property property, string type, DateTime? reportDate = null)
    {
        var receipt = new SwissComplianceReceipt
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Type = type,
            ReportDate = reportDate ?? Day,
            Reference = "TEST-only",
            UploadedFileHash = new string('a', 64),
        };
        db.SwissComplianceReceipts.Add(receipt);
        return receipt.Id;
    }

    private sealed class RecordingPush(bool fail = false, bool refuse = false) : IPushNotificationService
    {
        public List<(string Key, PushAudience Audience, PushNotificationPayload Payload)> Sent { get; } = [];

        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            if (fail)
                throw new InvalidOperationException("The queue is down.");

            Sent.Add((deliveryKey, audience, payload));
            return !refuse;
        }
    }

    private sealed class MovableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>
    /// A context whose first save of a change to a declaration (an update or a delete) finds the row already gone: another request removed it a moment before, and the database answers that no row was
    /// affected, which EF reports as a concurrency exception. The next saves are as any other.
    /// </summary>
    private sealed class VanishingRowDbContext(string store, Action anotherRequestWins)
        : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store).Options)
    {
        private bool _raced;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_raced && ChangeTracker.Entries<SwissNotificationDeclaration>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            {
                _raced = true;
                anotherRequestWins();
                throw new DbUpdateConcurrencyException("The database operation was expected to affect 1 row(s), but actually affected 0 row(s).");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A context whose first save of a declaration loses the race on the unique index: another request saved the declaration of the same day a moment before, and PostgreSQL answers 23505 that
    /// names the index. The next saves are as any other.
    /// </summary>
    private sealed class RacingDbContext(string store, Action winnerSaves)
        : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store).Options)
    {
        private bool _raced;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_raced && ChangeTracker.Entries<SwissNotificationDeclaration>().Any(e => e.State == EntityState.Added))
            {
                _raced = true;
                winnerSaves();
                throw new DbUpdateException(
                    "save failed",
                    new PostgresException(
                        "duplicate key value violates unique constraint",
                        "ERROR",
                        "ERROR",
                        PostgresErrorCodes.UniqueViolation,
                        constraintName: SwissNotificationDeclaration.DefaultRegimeIndexName));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
