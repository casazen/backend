using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-16d: the reminder of the day before to the <b>supplier</b>, the second pass of the hourly job: at 18:00 in Rome of the day before, once, for a
/// request the supplier took in any context, with the comune and the time and never the address; not when the supplier turned the reminder off or is
/// suspended. The customer's pass is covered by <c>ServiceRequestReminderServiceTests</c>; the session lock and the row version need PostgreSQL.
/// </summary>
public class SupplierDayBeforeReminderTests
{
    /// <summary>Monday 12 October 2026, 10:00 in Rome (summer time): its reminder is due on Sunday 11 at 18:00 in Rome, 16:00 UTC.</summary>
    private static readonly DateTime MondayAt10 = ServiceRequestScenario.FridayAt10.AddDays(3);

    private static readonly DateTimeOffset SundayAt16 = new(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SendDue_FromEighteenTheDayBefore_RemindsTheSupplierOnce_WithTheComuneAndTheTime_AndNeverTheAddress()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.TakenAsync(MondayAt10);
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        var first = await Reminders(s).SendDueAsync();
        var second = await Reminders(s).SendDueAsync();
        s.Clock.Advance(TimeSpan.FromHours(3));
        var third = await Reminders(s).SendDueAsync();

        Assert.Equal(1, first.SupplierSent);
        Assert.Equal(0, second.SupplierSent);
        Assert.Equal(0, third.SupplierSent);
        Assert.Equal(0, first.Sent);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.ServiceRequestDayBefore, email.Template);
        Assert.Equal("supplier@test.com", email.To);
        Assert.Contains("Domani:", email.Content.Subject);
        Assert.Contains("12/10/2026 10:00", email.Content.HtmlBody);
        Assert.Contains(ServiceRequestScenario.Comune, email.Content.HtmlBody);
        Assert.Contains($"/app/supplier/inbox/{request.Id}", email.Content.HtmlBody);
        // The address, the name of the property and the notes of the host are in the console after the take, not in a queued message (decision D9).
        Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, email.Content.HtmlBody);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyName, email.Content.HtmlBody);
        Assert.DoesNotContain(ServiceRequestScenario.HostNotes, email.Content.HtmlBody);

        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestDayBefore, push.Payload.Type);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(request.Id, push.Payload.ServiceRequestId);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, push.Payload.Body);
        Assert.Equal(SundayAt16.UtcDateTime, (await s.ReadAsync(request.Id)).SupplierReminderSentAt);
    }

    [Fact]
    public async Task SendDue_FollowsTheClockOfRome_AfterTheEndOfSummerTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // Monday 26 October 2026 at 10:00 in Rome is winter time (09:00 UTC); the reminder is due on Sunday 25 at 18:00 in Rome, 17:00 UTC.
        var request = await s.TakenAsync(MondayAt10.AddDays(14).AddHours(1));
        var dueAt = new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero);
        s.Clock.SetUtcNow(dueAt - TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var early = await Reminders(s).SendDueAsync();
        s.Clock.SetUtcNow(dueAt);
        var due = await Reminders(s).SendDueAsync();

        Assert.Equal(0, early.SupplierSent);
        Assert.Equal(1, due.SupplierSent);
        Assert.Contains("10:00", Assert.Single(s.Emails.Snapshot()).Content.HtmlBody);
        Assert.Equal(dueAt.UtcDateTime, (await s.ReadAsync(request.Id)).SupplierReminderSentAt);
    }

    [Fact]
    public async Task SendDue_ASupplierWhoTurnedTheReminderOff_GetsNone_AndTheRequestIsNotJudgedAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.TakenAsync(MondayAt10);
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.NotifyDayBeforeReminder = false;
        await s.Db.SaveChangesAsync();
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();
        // The supplier changes its mind that night: the reminder of this job is not sent late.
        settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.NotifyDayBeforeReminder = true;
        await s.Db.SaveChangesAsync();
        s.Clock.Advance(TimeSpan.FromHours(2));
        var again = await Reminders(s).SendDueAsync();

        Assert.Equal(0, run.SupplierSent);
        Assert.Equal(0, run.SupplierFailed);
        Assert.Equal(0, again.SupplierSent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        Assert.Equal(SundayAt16.UtcDateTime, (await s.ReadAsync(request.Id)).SupplierReminderSentAt);
    }

    [Fact]
    public async Task SendDue_ASupplierWhoIsSuspended_IsNotRemindedOfAJobItCannotDo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.TakenAsync(MondayAt10);
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(0, run.SupplierSent);
        Assert.Equal(0, run.SupplierFailed);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task SendDue_ARequestTakenAfterTheReminderTime_GetsNone_BecauseTheSupplierTookItAMomentAgo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Clock.SetUtcNow(SundayAt16 + TimeSpan.FromMinutes(30));
        var request = await s.RequestAsync(MondayAt10, durationMinutes: ServiceRequestScenario.ServiceMinutes);
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        s.Clock.Advance(TimeSpan.FromHours(2));
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(0, run.SupplierSent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task SendDue_ARequestOfTheLongRentContext_IsRemindedToo_AndOneNotTakenIsNot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var longRent = await s.RequestAsync(MondayAt10, rentalContext: ServiceRequestRentalContext.LongRent, durationMinutes: ServiceRequestScenario.ServiceMinutes);
        await s.Service.TakeAsync(longRent.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        var notTaken = await s.RequestAsync(MondayAt10.AddHours(4), durationMinutes: ServiceRequestScenario.ServiceMinutes);
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        // Any context of the request is reminded to its supplier; a request still waiting for the answer has nothing to remind.
        Assert.Equal(1, run.SupplierSent);
        Assert.Single(s.Emails.Snapshot());
        Assert.NotNull((await s.ReadAsync(longRent.Id)).SupplierReminderSentAt);
        Assert.Null((await s.ReadAsync(notTaken.Id)).SupplierReminderSentAt);
    }

    // ─── helpers ───

    /// <summary>The job as the product runs it: the customers' pass and, under the same lock, the supplier's.</summary>
    private static ServiceRequestReminderService Reminders(ServiceRequestScenario s) =>
        new(
            s.Db,
            s.Kit.ShowcaseNotifier,
            NullLogger<ServiceRequestReminderService>.Instance,
            s.Clock,
            new SupplierDayBeforeReminders(s.Db, s.Notifier, NullLogger<SupplierDayBeforeReminders>.Instance));
}
