using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E31/F02/US01/T01 (NF-131, decisions D1, D5, D10 and D12, casazen/backend#979): the assisted notification of the guests to the authority of a canton other than Ticino, on EF InMemory with the
/// real resolver of the regime. The rules that cost if they fail are the three proofs: a duty that is only <c>Ask</c> or <c>NotRequired</c> never raises a day
/// (<see cref="Assisted_AskOrNotRequired_NeverRaisesItem_Test"/>: no false legal alarm), Ticino is never in this section
/// (<see cref="Assisted_Ticino_NotInThisSection_Test"/>: one obligation, one section), and another organization never reads or declares a day
/// (<see cref="Assisted_OtherOrganization_NeverReadsNorDeclares_Test"/> for the data; <c>SwissNotificationControllerTests.Assisted_OtherOrganizationForbidden_Test</c> for the web layer).
/// </summary>
public class SwissNotificationServiceTests
{
    private const string Anna = "auth0|anna";
    private const string Bruno = "auth0|bruno";
    private static readonly DateOnly Day = SwissNotificationTestWorld.Day;

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ─── Which days are raised ───

    [Fact]
    public async Task Assisted_AskOrNotRequired_NeverRaisesItem_Test()
    {
        // PROOF TEST (data): a legal alarm is raised only where the duty is Required: the host said so, or the regime is verified as covering private rentals. A canton whose sources are partial
        // (Zurich, Nidwalden) is Ask, the host that said NotRequired is not raised, and a canton that only asks for a register (Bern) is never raised, whatever the host says.
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var verifiedByDefault = world.AddProperty("SH", org, name: "Schaffhausen verified");
        var askZurich = world.AddProperty("ZH", org, name: "Zurich ask");
        var askNidwalden = world.AddProperty("NW", org, name: "Nidwalden ask");
        var hostSaidNotRequired = world.AddProperty("SH", org, GuestNotificationOverride.NotRequired, name: "Schaffhausen not required");
        var hostSaidRequired = world.AddProperty("ZH", org, GuestNotificationOverride.Required, name: "Zurich required");
        var registerOnly = world.AddProperty("BE", org, GuestNotificationOverride.Required, name: "Bern register");
        foreach (var property in new[] { verifiedByDefault, askZurich, askNidwalden, hostSaidNotRequired, hostSaidRequired, registerOnly })
            world.AddStay(property);

        var days = await world.Service().ListPendingDaysAsync(org.Id, null, Day);

        Assert.Equal(
            new[] { verifiedByDefault.Id, hostSaidRequired.Id }.Order(),
            days.Select(d => d.PropertyId).Order());
        Assert.Equal(["CH-SH", "CH-ZH"], days.OrderBy(d => d.RegimeCode, StringComparer.Ordinal).Select(d => d.RegimeCode));
        Assert.All(days, d => Assert.Equal((Day, 1, org.Id), (d.Day, d.StayCount, d.OrgId)));

        // The day of a property whose duty is Ask is still readable (it says so and raises nothing), the one of a register-only canton is not covered at all.
        var service = world.Service();
        var ask = await service.GetDayAsync(askZurich.Id, Day);
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, ask.EffectiveDuty);
        Assert.Empty(ask.PendingDays);
        var notRequired = await service.GetDayAsync(hostSaidNotRequired.Id, Day);
        Assert.Equal(GuestRegistrationEffectiveDuty.NotRequired, notRequired.EffectiveDuty);
        Assert.Empty(notRequired.PendingDays);
        var register = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetDayAsync(registerOnly.Id, Day));
        Assert.Equal("swiss_notification_not_applicable", register.Code);
        Assert.Equal("SwissNotificationNotApplicable", register.MessageKey);
    }

    [Fact]
    public async Task Assisted_Ticino_NotInThisSection_Test()
    {
        // PROOF TEST (data): one obligation, one section. A Ticino property has its own flow: it is never listed here, its day, its declaration and its undo are refused as not covered, and
        // a declaration of the Ticino police (no regime code) closes nothing of this section. An Italian property is not Swiss at all.
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var ticino = world.AddProperty("TI", org, name: "Lugano");
        var italian = world.AddProperty("TI", org, name: "Como", country: "IT");
        var schaffhausen = world.AddProperty("SH", org, name: "Schaffhausen");
        foreach (var property in new[] { ticino, italian, schaffhausen })
            world.AddStay(property);
        var service = world.Service();
        var ticinoService = new TicinoNotificationService(world.Db, world.Clock);

        var swiss = await service.ListPendingDaysAsync(org.Id, null, Day);
        var ticinoSection = await ticinoService.ListPendingDaysAsync(org.Id, null, Day);

        Assert.Equal(schaffhausen.Id, Assert.Single(swiss).PropertyId);
        Assert.Equal(ticino.Id, Assert.Single(ticinoSection).PropertyId);
        foreach (var notCovered in new[] { ticino.Id, italian.Id })
        {
            Assert.Equal("swiss_notification_not_applicable", (await Assert.ThrowsAsync<DomainRuleException>(() => service.GetDayAsync(notCovered, Day))).Code);
            Assert.Equal("swiss_notification_not_applicable", (await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareDayAsync(notCovered, Day, Anna))).Code);
            Assert.Equal("swiss_notification_not_applicable", (await Assert.ThrowsAsync<DomainRuleException>(() => service.UndeclareDayAsync(notCovered, Day, Anna))).Code);
        }

        // The declaration of the Ticino day closes the Ticino day and nothing here; a declaration here closes nothing of Ticino.
        await ticinoService.DeclareDayAsync(ticino.Id, Day, Anna);
        Assert.Equal(schaffhausen.Id, Assert.Single(await service.ListPendingDaysAsync(org.Id, null, Day)).PropertyId);
        await service.DeclareDayAsync(schaffhausen.Id, Day, Anna);
        Assert.Empty(await ticinoService.ListPendingDaysAsync(org.Id, null, Day));
        Assert.Empty(await service.ListPendingDaysAsync(org.Id, null, Day));
        Assert.Equal(
            [(ticino.Id, (string?)null), (schaffhausen.Id, "CH-SH")],
            (await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync())
                .Select(d => (d.PropertyId, d.RegimeCode))
                .OrderBy(d => d.RegimeCode, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Assisted_OtherOrganization_NeverReadsNorDeclares_Test()
    {
        // PROOF TEST (data, tenant): the day holds personal data and a declaration closes a legal duty, so another org never sees them. The caller of org B (the tenant filter of the
        // context) finds the property of org A neither to read, nor to declare, nor to undo; its list holds nothing of A, not even when it names A's property; a declaration of A is not B's.
        using var world = new SwissNotificationTestWorld();
        var orgA = world.AddOrg("a@example.com");
        var orgB = world.AddOrg("b@example.com");
        var propertyA = world.AddProperty("SH", orgA, name: "Casa A");
        var propertyB = world.AddProperty("SH", orgB, name: "Casa B");
        var stayA = world.AddStay(propertyA);
        world.AddGuest(stayA, firstName: "Zxqvgfhj", lastName: "Plmnbvcx");
        world.AddStay(propertyB);
        await world.Service().DeclareDayAsync(propertyA.Id, Day, Anna);
        using var asB = world.NewDb(new SwissTestTenant(orgB.Id));
        var serviceOfB = world.Service(asB);

        await Assert.ThrowsAsync<NotFoundException>(() => serviceOfB.GetDayAsync(propertyA.Id, Day));
        await Assert.ThrowsAsync<NotFoundException>(() => serviceOfB.DeclareDayAsync(propertyA.Id, Day.AddDays(-1), Bruno));
        await Assert.ThrowsAsync<NotFoundException>(() => serviceOfB.UndeclareDayAsync(propertyA.Id, Day, Bruno));

        var listedForB = await serviceOfB.ListPendingDaysAsync(orgB.Id, null, Day);
        var namingTheOtherOrgsProperty = await serviceOfB.ListPendingDaysAsync(orgB.Id, [propertyA.Id], Day);
        Assert.Equal(propertyB.Id, Assert.Single(listedForB).PropertyId);
        Assert.Empty(namingTheOtherOrgsProperty);

        // Nothing of B touched A: its declaration is still there, and the day of B shows no declaration.
        Assert.Equal(propertyA.Id, Assert.Single(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync()).PropertyId);
        Assert.Null((await serviceOfB.GetDayAsync(propertyB.Id, Day)).DeclaredAtUtc);
    }

    [Fact]
    public async Task ListPendingDays_AnEmptyListOfProperties_IsNobody_NeverEveryone()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var first = world.AddProperty("SH", org, name: "Casa 1");
        var second = world.AddProperty("SH", org, name: "Casa 2");
        world.AddStay(first);
        world.AddStay(second);
        var service = world.Service();

        Assert.Empty(await service.ListPendingDaysAsync(org.Id, [], Day));
        Assert.Equal(first.Id, Assert.Single(await service.ListPendingDaysAsync(org.Id, [first.Id], Day)).PropertyId);
        Assert.Equal(2, (await service.ListPendingDaysAsync(org.Id, null, Day)).Count);

        // The job reads every org: (null, null), and an org that has nothing is not an error.
        Assert.Equal(2, (await service.ListPendingDaysAsync(null, null, Day)).Count);
        Assert.Empty(await service.ListPendingDaysAsync(Guid.NewGuid(), null, Day));
    }

    [Fact]
    public async Task ListPendingDays_WithTheFlagOff_IsEmpty_AndTheSettingOfTheHostIsNotRead()
    {
        using var world = new SwissNotificationTestWorld(regimesOn: false);
        var org = world.AddOrg();
        var verified = world.AddProperty("SH", org, name: "Verified");
        var chosen = world.AddProperty("ZH", org, GuestNotificationOverride.Required, name: "Chosen");
        world.AddStay(verified);
        world.AddStay(chosen);
        var service = world.Service();

        Assert.Empty(await service.ListPendingDaysAsync(org.Id, null, Day));
        Assert.Empty(await service.ListPendingDaysAsync(null, null, Day));

        // The setting is not read while the flag is off: the duty of the property that chose Required is the one of its regime.
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, (await service.GetDayAsync(chosen.Id, Day)).EffectiveDuty);

        world.RegimesOn = true;
        Assert.Equal(2, (await service.ListPendingDaysAsync(org.Id, null, Day)).Count);
    }

    [Fact]
    public async Task ListPendingDays_StaysThatOweNothing_AreNeverListed()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        world.AddStay(property, status: BookingStatus.Cancelled);
        world.AddStay(property, status: BookingStatus.Pending);
        world.AddStay(property, status: BookingStatus.CheckedIn, noShowAt: new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc));
        var service = world.Service();

        Assert.Empty(await service.ListPendingDaysAsync(org.Id, null, Day));

        world.AddStay(property, status: BookingStatus.Confirmed);
        world.AddStay(property, status: BookingStatus.CheckedOut);
        var listed = Assert.Single(await service.ListPendingDaysAsync(org.Id, null, Day));
        Assert.Equal(2, listed.StayCount);
    }

    [Fact]
    public async Task ListPendingDays_TheLastFourteenDays_OldestFirst_AndNotTomorrow()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        foreach (var offset in new[] { -15, -14, -3, 0, 1 })
            world.AddStay(property, Day.AddDays(offset));

        var days = await world.Service().ListPendingDaysAsync(org.Id, null, Day);

        Assert.Equal([Day.AddDays(-14), Day.AddDays(-3), Day], days.Select(d => d.Day));
        Assert.Equal(14, ISwissNotificationService.PendingLookbackDays);
    }

    // ─── The deadline of each kind, in the list the cockpit and the alerts read ───

    [Fact]
    public async Task ListPendingDays_EachDayCarriesTheDeadlineOfItsRegime_OnTheClockOfTheProperty()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var daily = world.AddProperty("SH", org, name: "Schaffhausen");
        var atArrival = world.AddProperty("ZG", org, name: "Zug");
        var weekly = world.AddProperty("NW", org, GuestNotificationOverride.Required, name: "Nidwalden");
        var noTime = world.AddProperty("BS", org, GuestNotificationOverride.Required, name: "Basel");
        var noDeadline = world.AddProperty("FR", org, GuestNotificationOverride.Required, name: "Fribourg");
        foreach (var property in new[] { daily, weekly, noTime, noDeadline })
            world.AddStay(property);
        world.AddStay(atArrival, arrivedAtUtc: "2026-11-19T14:00:00Z");

        var days = (await world.Service().ListPendingDaysAsync(org.Id, null, Day)).ToDictionary(d => d.PropertyId);

        Assert.Equal((DateTime?)Utc("2026-11-20T01:00:00Z"), days[daily.Id].DueAtUtc);
        Assert.Equal((DateTime?)Utc("2026-11-19T14:00:00Z"), days[atArrival.Id].DueAtUtc);
        Assert.Equal((DateTime?)Utc("2026-11-22T22:59:00Z"), days[weekly.Id].DueAtUtc);
        Assert.Equal((DateTime?)Utc("2026-11-19T23:00:00Z"), days[noTime.Id].DueAtUtc);
        Assert.Null(days[noDeadline.Id].DueAtUtc);
    }

    [Fact]
    public async Task ListPendingDays_ThePropertyClockDecidesTheDeadline_AndTheFirstArrivalOfTheDayIsTheOneThatCounts()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var zug = world.AddProperty("ZG", org, timezone: "Europe/Zurich");
        var utcClock = world.AddProperty("BS", org, GuestNotificationOverride.Required, timezone: "UTC", name: "On UTC");
        world.AddStay(zug, arrivedAtUtc: "2026-11-19T16:00:00Z");
        world.AddStay(zug, arrivedAtUtc: "2026-11-19T13:30:00Z");
        world.AddStay(zug);
        world.AddStay(utcClock);

        var days = (await world.Service().ListPendingDaysAsync(org.Id, null, Day)).ToDictionary(d => d.PropertyId);

        // Zug: the earliest arrival recorded among the three stays. A property on another clock ends the day when its own day ends: midnight on UTC is 00:00Z, not the 23:00Z of Zurich in winter.
        Assert.Equal((DateTime?)Utc("2026-11-19T13:30:00Z"), days[zug.Id].DueAtUtc);
        Assert.Equal((DateTime?)Utc("2026-11-20T00:00:00Z"), days[utcClock.Id].DueAtUtc);
    }

    // ─── The one rule of a closed day ───

    [Fact]
    public async Task Declare_ClosesTheDay_ThePendingListNoLongerHoldsIt_AndTheDayShowsWhoAndWhen()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        world.AddUser(Anna, "Anna", "Rossi");
        var property = world.AddProperty("SH", org);
        world.AddStay(property);
        world.AddStay(property, Day.AddDays(-1));
        var service = world.Service();

        var before = await service.GetDayAsync(property.Id, Day);
        var declaration = await service.DeclareDayAsync(property.Id, Day, Anna);
        var after = await service.GetDayAsync(property.Id, Day);
        var list = await service.ListPendingDaysAsync(org.Id, null, Day);
        var everyOrg = await service.ListPendingDaysAsync(null, null, Day);

        Assert.False(before.AlreadyDeclared);
        Assert.Equal([Day.AddDays(-1), Day], before.PendingDays.Select(d => d.Day));
        Assert.Equal((property.Id, Day, "CH-SH", true, "Anna Rossi"), (declaration.PropertyId, declaration.Day, declaration.RegimeCode, declaration.Created, declaration.DeclaredBy));

        // The rule the cockpit and the alerts read: the declared day is gone from the list, for one org and for the job that reads every org; the other day is still to notify.
        Assert.Equal(Day.AddDays(-1), Assert.Single(list).Day);
        Assert.Equal(Day.AddDays(-1), Assert.Single(everyOrg).Day);
        Assert.True(after.AlreadyDeclared);
        Assert.Equal(((DateTime?)declaration.DeclaredAtUtc, "Anna Rossi"), (after.DeclaredAtUtc, after.DeclaredBy));
        Assert.Equal(Day.AddDays(-1), Assert.Single(after.PendingDays).Day);

        var row = Assert.Single(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal((org.Id, property.Id, Day, Anna, "CH-SH", "assisted", (Guid?)null), (row.OrgId, row.PropertyId, row.ArrivalDay, row.DeclaredByUserId, row.RegimeCode, row.Channel, row.ReceiptId));
    }

    [Fact]
    public async Task Declare_ADeclarationOfAnotherRegime_ClosesNothing_AndTheNewRegimeIsDeclaredAfresh()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        world.AddStay(property);

        // The property was declared while it was in Zug (the host then corrected the municipality): the declaration was for another authority.
        world.Db.SwissNotificationDeclarations.Add(new SwissNotificationDeclaration
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            ArrivalDay = Day,
            DeclaredByUserId = Anna,
            RegimeCode = "CH-ZG",
            Channel = SwissNotificationChannels.Assisted,
        });
        await world.Db.SaveChangesAsync();
        var service = world.Service();

        Assert.Equal(Day, Assert.Single(await service.ListPendingDaysAsync(org.Id, null, Day)).Day);
        var declared = await service.DeclareDayAsync(property.Id, Day, Bruno);

        Assert.True(declared.Created);
        Assert.Empty(await service.ListPendingDaysAsync(org.Id, null, Day));
        Assert.Equal(2, await world.Db.SwissNotificationDeclarations.CountAsync());
    }

    [Fact]
    public async Task Declare_IsIdempotent_TheSecondCallAnswersTheFirstDeclaration_AndTellsNobodyAgain()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        world.AddUser(Anna, "Anna", "Rossi");
        world.AddUser(Bruno, "Bruno", "Verdi");
        var property = world.AddProperty("SH", org, name: "Casa Rosa");
        world.AddStay(property);
        var service = world.Service(withPush: true);

        var created = await service.DeclareDayAsync(property.Id, Day, Anna);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var repeated = await service.DeclareDayAsync(property.Id, Day, Bruno);

        Assert.True(created.Created);
        Assert.False(repeated.Created);
        Assert.Equal((created.DeclaredAtUtc, "Anna Rossi"), (repeated.DeclaredAtUtc, repeated.DeclaredBy));
        Assert.Single(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());

        // Told once, to the hosts of the property, with the type of the other cantons and a key of its own (never the Ticino one).
        var (key, audience, payload) = Assert.Single(world.Pushes);
        Assert.Equal(PushAudience.PropertyHosts(property.Id), audience);
        Assert.Equal((PushTypes.SwissNotificationDeclared, PushRoutes.Properties), (payload.Type, payload.Route));
        Assert.Equal("swiss-declaration:" + property.Id.ToString("N") + ":20261119:declared:20261119120000000", key);
        Assert.Equal(PushDeliveryKeys.SwissNotificationDeclared(property.Id, Day, declared: true, created.DeclaredAtUtc), key);
    }

    [Fact]
    public async Task Declare_TheLiveMessage_NamesThePropertyAndTheDay_NeverAGuestAValueOrThePersonWhoActed()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        world.AddUser(Anna, "Anna", "Rossi");
        var property = world.AddProperty("SH", org, name: "Casa Rosa");
        var stay = world.AddStay(property, bookingCode: "7Q3KZ8M2XB", plate: "ZH4455667");
        world.AddGuest(stay, firstName: "Zxqvgfhj", lastName: "Plmnbvcx", documentNumber: "QWERTY123456");
        var service = world.Service(withPush: true);

        await service.DeclareDayAsync(property.Id, Day, Anna);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await service.UndeclareDayAsync(property.Id, Day, Bruno));
        Assert.False(await service.UndeclareDayAsync(property.Id, Day, Bruno));

        // One push for the declaration and one for the undo, none for the undo that had nothing to undo.
        Assert.Equal(2, world.Pushes.Count);
        Assert.All(world.Pushes, p => Assert.Equal(PushTypes.SwissNotificationDeclared, p.Payload.Type));
        Assert.Equal(2, world.Pushes.Select(p => p.Key).Distinct().Count());
        Assert.NotEqual(world.Pushes[0].Payload.Body, world.Pushes[1].Payload.Body);
        foreach (var (_, _, payload) in world.Pushes)
        {
            var text = payload.Title + " " + payload.Body;
            Assert.Contains("Casa Rosa", text, StringComparison.Ordinal);
            Assert.Contains("19", text, StringComparison.Ordinal);
            foreach (var secret in new[] { "Zxqvgfhj", "Plmnbvcx", "QWERTY123456", "7Q3KZ8M2XB", "ZH4455667", "Anna", "Rossi", "Bruno", Anna, Bruno })
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Declare_AQueueThatIsDown_NeverFailsTheDeclaration()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        world.AddStay(property);
        var broken = new SwissNotificationService(world.Db, world.Resolver(), world.Flags.Object, world.Clock, new ThrowingPush(), NullLogger<SwissNotificationService>.Instance);

        var declared = await broken.DeclareDayAsync(property.Id, Day, Anna);

        Assert.True(declared.Created);
        Assert.Single(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Declare_AFutureDay_IsRefused_BecauseItWouldSilenceTheAlertsOfAnArrivalThatHasNotHappened()
    {
        using var world = new SwissNotificationTestWorld();
        var property = world.AddProperty("SH");
        var service = world.Service(withPush: true);

        var tomorrow = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareDayAsync(property.Id, Day.AddDays(1), Anna));
        var today = await service.DeclareDayAsync(property.Id, Day, Anna);

        Assert.Equal(("swiss_declaration_day_in_future", "SwissDeclarationDayInFuture"), (tomorrow.Code, tomorrow.MessageKey));
        Assert.True(today.Created);
        Assert.Equal(Day, Assert.Single(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync()).ArrivalDay);
        Assert.Single(world.Pushes);
    }

    [Fact]
    public async Task Declare_ACoHostWhoWinsTheKeyAtTheSameMoment_IsAnsweredAsIfItHadComeSecond_AndNothingIsToldTwice()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        world.AddUser(Anna, "Anna", "Rossi");
        var property = world.AddProperty("SH", org);
        world.AddStay(property);
        var racing = new RacingDbContext(
            world.Store,
            () =>
            {
                // The winner saves the same day a moment before this call.
                using var other = world.NewDb();
                other.SwissNotificationDeclarations.Add(new SwissNotificationDeclaration
                {
                    OrgId = org.Id,
                    PropertyId = property.Id,
                    ArrivalDay = Day,
                    DeclaredByUserId = Anna,
                    RegimeCode = "CH-SH",
                    Channel = SwissNotificationChannels.Assisted,
                });
                other.SaveChanges();
            });
        var service = new SwissNotificationService(racing, world.Resolver(racing), world.Flags.Object, world.Clock, new RecordingPush(world), NullLogger<SwissNotificationService>.Instance);

        var answered = await service.DeclareDayAsync(property.Id, Day, Bruno);

        Assert.False(answered.Created);
        Assert.Equal("Anna Rossi", answered.DeclaredBy);
        Assert.Single(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Empty(world.Pushes);
    }

    [Fact]
    public async Task Undo_RemovesTheDeclaration_TheDayIsToNotifyAgain_AndRepeatingItIsHarmless()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        world.AddStay(property);
        var service = world.Service();

        var nothingToUndo = await service.UndeclareDayAsync(property.Id, Day, Anna);
        await service.DeclareDayAsync(property.Id, Day, Anna);
        var undone = await service.UndeclareDayAsync(property.Id, Day, Bruno);
        var undoneAgain = await service.UndeclareDayAsync(property.Id, Day, Bruno);

        Assert.False(nothingToUndo);
        Assert.True(undone);
        Assert.False(undoneAgain);
        Assert.Empty(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
        Assert.Equal(Day, Assert.Single(await service.ListPendingDaysAsync(org.Id, null, Day)).Day);
        Assert.False((await service.GetDayAsync(property.Id, Day)).AlreadyDeclared);
    }

    [Fact]
    public async Task Undo_OnlyTheDeclarationsOfTheRegimeOfTheProperty_AreRemoved()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        world.AddStay(property);
        world.Db.SwissNotificationDeclarations.AddRange(
            new SwissNotificationDeclaration { OrgId = org.Id, PropertyId = property.Id, ArrivalDay = Day, DeclaredByUserId = Anna, RegimeCode = "CH-ZG" },
            new SwissNotificationDeclaration { OrgId = org.Id, PropertyId = property.Id, ArrivalDay = Day, DeclaredByUserId = Anna, RegimeCode = null });
        await world.Db.SaveChangesAsync();

        var removed = await world.Service().UndeclareDayAsync(property.Id, Day, Bruno);

        Assert.False(removed);
        Assert.Equal(2, await world.Db.SwissNotificationDeclarations.CountAsync());
    }

    [Fact]
    public async Task Undo_ACoHostWhoUndoesTheSameDeclarationAtTheSameMoment_GetsTheIdempotentAnswer_AndTellsNobody()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        world.AddStay(property);
        await world.Service().DeclareDayAsync(property.Id, Day, Anna);
        var vanishing = new VanishingRowDbContext(
            world.Store,
            () =>
            {
                using var winner = world.NewDb();
                winner.SwissNotificationDeclarations.RemoveRange(winner.SwissNotificationDeclarations.ToList());
                winner.SaveChanges();
            });
        var service = new SwissNotificationService(vanishing, world.Resolver(vanishing), world.Flags.Object, world.Clock, new RecordingPush(world), NullLogger<SwissNotificationService>.Instance);

        var removed = await service.UndeclareDayAsync(property.Id, Day, Bruno);

        Assert.False(removed);
        Assert.Empty(world.Pushes);
        Assert.Empty(await world.Db.SwissNotificationDeclarations.AsNoTracking().ToListAsync());
    }

    // ─── The day: data ready to copy ───

    [Fact]
    public async Task GetDay_TheGuestsAreCopyReady_WithWhatTheyLack_ThePortalAndTheDeadline()
    {
        using var world = new SwissNotificationTestWorld();
        var org = world.AddOrg();
        var property = world.AddProperty("SH", org);
        var stay = world.AddStay(property, bookingCode: "7Q3KZ8M2XB", plate: "ZH 123456", guests: 2, arrivedAtUtc: "2026-11-19T14:00:00Z");
        world.AddGuest(stay, position: 0);
        world.AddGuest(stay, position: 1, firstName: "Marta", lastName: "", birth: new DateTime(2012, 5, 5, 0, 0, 0, DateTimeKind.Utc), documentTypeCode: null, documentNumber: "");

        var day = await world.Service().GetDayAsync(property.Id, Day);

        Assert.Equal(("CH-SH", GuestRegistrationEffectiveDuty.Required, 1), (day.Regime.Code, day.EffectiveDuty, day.StayCount));
        Assert.Equal("https://gast.sh.ch", day.Regime.PortalUrl);
        Assert.Equal(GuestRegistrationCertainty.Verified, day.Regime.Certainty);
        Assert.NotNull(day.Regime.DeadlineNoteKey);
        Assert.Equal((DateTime?)Utc("2026-11-20T01:00:00Z"), day.DueAtUtc);
        Assert.Equal(2, day.Guests.Count);
        var adult = day.Guests[0];
        Assert.Equal(("Anna", "Rossi", (DateOnly?)new DateOnly(1984, 1, 4), "SVIZZERA", "CHE"), (adult.FirstName, adult.LastName, adult.BirthDate, adult.Nationality, adult.NationalityCode));
        Assert.Equal((SwissDocumentTypes.Passport, "X1234567", "Via Berna 11", "8001", "Zürich"), (adult.DocumentType, adult.DocumentNumber, adult.Street, adult.PostalCode, adult.City));
        Assert.Equal(("7Q3KZ-8M2XB", "ZH 123456", Day, Day.AddDays(2)), (adult.BookingCode, adult.Plate, adult.Arrival, adult.Departure));

        // The child has no last name: a gap the host completes; a minor needs no document.
        var issue = Assert.Single(day.Issues);
        Assert.Equal((1, "Marta"), (issue.Position, issue.GuestName));
        Assert.Equal(["lastName"], issue.Missing);
    }

    [Fact]
    public async Task GetDay_TheBookerStandsForTheGuestsUntilTheCheckInIsDone_AndTheStayLacksTheOthers()
    {
        using var world = new SwissNotificationTestWorld();
        var property = world.AddProperty("SH");
        world.AddStay(property, guests: 3);

        var day = await world.Service().GetDayAsync(property.Id, Day);

        // No guest recorded: the booker alone, with nothing but the name, and the stay lacks the other two.
        var booker = Assert.Single(day.Guests);
        Assert.Equal(("Booker", "Person", (DateOnly?)null), (booker.FirstName, booker.LastName, booker.BirthDate));
        Assert.Contains(day.Issues, i => i is { Position: null } && i.Missing.SequenceEqual(["guests"]));
        Assert.Contains(day.Issues, i => i.Position == 0 && i.Missing.Contains("birthDate") && i.Missing.Contains("documentType"));
    }

    [Fact]
    public async Task GetDay_NeverHoldsTheExemptionOfAGuest_NorTheContactOfTheBooker()
    {
        // PROOF (privacy): the reason of a tourist tax exemption can reveal a health condition and the e-mail of the booker is a contact: neither is among the data the notification needs,
        // so neither reaches the answer, whatever the check-in holds.
        using var world = new SwissNotificationTestWorld();
        var property = world.AddProperty("SH");
        var stay = world.AddStay(property);
        world.AddGuest(stay, exemption: "medical-condition-xyz");

        var day = await world.Service().GetDayAsync(property.Id, Day);

        var json = JsonSerializer.Serialize(day.Guests) + JsonSerializer.Serialize(day.Issues);
        Assert.DoesNotContain("medical-condition-xyz", json, StringComparison.Ordinal);
        Assert.DoesNotContain("booker@example.com", json, StringComparison.Ordinal);
        Assert.Contains("Anna", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDay_ADayWithNoStay_IsEmpty_AndTheDeadlineIsStillThere()
    {
        using var world = new SwissNotificationTestWorld();
        var property = world.AddProperty("NW", choice: GuestNotificationOverride.Required);

        var day = await world.Service().GetDayAsync(property.Id, Day);

        Assert.Empty(day.Guests);
        Assert.Empty(day.Issues);
        Assert.Equal(0, day.StayCount);
        Assert.Equal((DateTime?)Utc("2026-11-22T22:59:00Z"), day.DueAtUtc);
        Assert.Equal(GuestRegistrationDeadlineKind.Weekly, day.Regime.DeadlineKind);
    }

    [Fact]
    public async Task GetDay_APropertyThatDoesNotExist_IsNotFound()
    {
        using var world = new SwissNotificationTestWorld();

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => world.Service().GetDayAsync(Guid.NewGuid(), Day));

        Assert.Equal("property_not_found", missing.Code);
    }

    // ─── Test doubles ───

    private sealed class RecordingPush(SwissNotificationTestWorld world) : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            world.Pushes.Add((deliveryKey, audience, payload));
            return true;
        }
    }

    private sealed class ThrowingPush : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload) => throw new InvalidOperationException("The queue is down.");
    }

    /// <summary>A context whose first save of a new declaration loses the race on the unique index: another request saved the same day a moment before, and PostgreSQL answers 23505 that names the index.</summary>
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
                        constraintName: SwissNotificationDeclaration.NamedRegimeIndexName));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>A context whose first save of a change to a declaration finds the row already gone: another request removed it a moment before, which EF reports as a concurrency exception.</summary>
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
}
