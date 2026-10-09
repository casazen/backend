using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: the reminder to the customer of a request from a supplier's showcase, hourly: at 18:00 in Rome of the day before the
/// work, once, only when the supplier took the request before that time (the e-mail of the take promised it only then). The
/// session lock and the check of the row version need PostgreSQL (<c>ServiceRequestReminderPostgresTests</c>).
/// </summary>
public class ServiceRequestReminderServiceTests
{
    /// <summary>Monday 12 October 2026, 10:00 in Rome: its reminder is due on Sunday 11 at 18:00 in Rome, 16:00 UTC.</summary>
    private static readonly DateTime MondayAt10 = ServiceRequestScenario.FridayAt10.AddDays(3);

    private static readonly DateTimeOffset SundayAt16 = new(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SendDue_BeforeEighteenTheDayBefore_SendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await TakenForMondayAsync(s);
        s.Clock.SetUtcNow(SundayAt16 - TimeSpan.FromSeconds(1));
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(new ServiceRequestReminderRun(false, 0, 0), run);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Null((await s.ReadAsync(request.Id)).ReminderSentAt);
    }

    [Fact]
    public async Task SendDue_FromEighteenTheDayBefore_QueuesTheReminderToTheCustomer_Once()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await TakenForMondayAsync(s);
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        var first = await Reminders(s).SendDueAsync();
        var second = await Reminders(s).SendDueAsync();
        s.Clock.Advance(TimeSpan.FromHours(3));
        var third = await Reminders(s).SendDueAsync();

        Assert.Equal(new ServiceRequestReminderRun(false, 1, 0), first);
        Assert.Equal(new ServiceRequestReminderRun(false, 0, 0), second);
        Assert.Equal(new ServiceRequestReminderRun(false, 0, 0), third);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierBookingReminder, email.Template);
        Assert.Contains("Domani:", email.Content.Subject);
        Assert.Contains("Supplier Srl", email.Content.Subject);
        Assert.Contains("12/10/2026 10:00", email.Content.HtmlBody);
        Assert.Contains("/fornitori/vetrina-test/richiesta", email.Content.HtmlBody);
        Assert.Equal(SundayAt16.UtcDateTime, (await s.ReadAsync(request.Id)).ReminderSentAt);
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task SendDue_EveryHourlyRunBetweenTheReminderTimeAndTheWork_FindsItAndSendsItOnce()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await TakenForMondayAsync(s);
        s.Clock.SetUtcNow(SundayAt16 + TimeSpan.FromMinutes(7));
        s.ForgetNotifications();
        var sent = 0;

        // Hourly runs from just after 18:00 on Sunday until the work starts on Monday at 10:00.
        for (var hour = 0; hour < 16; hour++)
        {
            sent += (await Reminders(s).SendDueAsync()).Sent;
            s.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(1, sent);
        Assert.Single(s.Emails.Snapshot());
    }

    [Fact]
    public async Task SendDue_ARequestTakenAfterTheReminderTime_GetsNone_BecauseTheAcceptanceMailDidNotPromiseIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.RespondWithinMinutes = 900;
        await s.Db.SaveChangesAsync();
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync(await s.InputAsync(MondayAt10));
        s.Clock.SetUtcNow(SundayAt16 + TimeSpan.FromMinutes(30));
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        s.Clock.Advance(TimeSpan.FromHours(2));
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(new ServiceRequestReminderRun(false, 0, 0), run);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task SendDue_OnceTheWorkHasStarted_ThereIsNothingToRemind()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await TakenForMondayAsync(s);
        s.Clock.SetUtcNow(new DateTimeOffset(MondayAt10, TimeSpan.Zero));
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(0, run.Sent);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    public async Task SendDue_OnlyARequestTheSupplierTookAndDidNotStartIsRemindedOf(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync(await s.InputAsync(MondayAt10));
        if (status != ServiceRequestStatus.Richiesto)
        {
            var tracked = await s.Db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == request.Id);
            tracked.Status = status;
            tracked.TakenAt = status is ServiceRequestStatus.Rifiutato or ServiceRequestStatus.Annullato ? null : tracked.CreatedAt;
            await s.Db.SaveChangesAsync();
            s.Db.ChangeTracker.Clear();
        }

        s.Clock.SetUtcNow(SundayAt16 + TimeSpan.FromMinutes(5));
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(0, run.Sent);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task SendDue_TheRequestsOfTheHosts_AreNeverRemindedThisWay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var host = await s.RequestAsync(MondayAt10);
        await s.Service.TakeAsync(host.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        s.Clock.SetUtcNow(SundayAt16 + TimeSpan.FromMinutes(5));
        s.ForgetNotifications();

        var run = await Reminders(s).SendDueAsync();

        Assert.Equal(0, run.Sent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Null((await s.ReadAsync(host.Id)).ReminderSentAt);
    }

    [Fact]
    public async Task SendDue_TheReminderIsInTheLanguageOfTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await TakenForMondayAsync(s, await s.InputAsync(MondayAt10, change: i => i with { Locale = "en" }));
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        await Reminders(s).SendDueAsync();

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Contains("Tomorrow:", email.Content.Subject);
    }

    [Fact]
    public async Task SendDue_ACustomerWhoHasBeenAnonymized_IsNotWrittenTo_AndTheRequestIsNotTriedAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await TakenForMondayAsync(s);
        var customer = await s.Db.ServiceCustomers.SingleAsync();
        ServiceCustomerPrivacyService.Anonymize(customer, s.Clock.GetUtcNow().UtcDateTime);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        s.Clock.SetUtcNow(SundayAt16);
        s.ForgetNotifications();

        var first = await Reminders(s).SendDueAsync();
        var second = await Reminders(s).SendDueAsync();

        // At most once: the request is marked before the mail is queued, so a mail that cannot be queued is not retried forever.
        Assert.Equal(new ServiceRequestReminderRun(false, 0, 1), first);
        Assert.Equal(new ServiceRequestReminderRun(false, 0, 0), second);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task SendDue_AFailingQueue_IsCountedAndLogged_NotThrown()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await TakenForMondayAsync(s);
        s.Clock.SetUtcNow(SundayAt16);
        using var kit = new ServiceRequestTestKit(s.Db, new ThrowingEmailQueue(), clock: s.Clock);
        var service = new ServiceRequestReminderService(s.Db, kit.ShowcaseNotifier, NullLogger<ServiceRequestReminderService>.Instance, s.Clock);

        var run = await service.SendDueAsync();

        Assert.Equal(new ServiceRequestReminderRun(false, 0, 1), run);
    }

    [Fact]
    public async Task SendDue_TheCandidatesAreThoseOfTheNextTwoDays_EarliestFirst()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        s.Clock.SetUtcNow(SundayAt16 + TimeSpan.FromMinutes(1));
        foreach (var day in new[] { 3, 4 })
        {
            var (request, _) = await s.BookedAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(day), email: $"c{day}@example.com"));
            await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        }

        var candidates = await ServiceRequestReminderService
            .ReminderCandidatesOf(s.Db, s.Clock.GetUtcNow().UtcDateTime, s.Clock.GetUtcNow().UtcDateTime.AddHours(48))
            .ToListAsync();

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].StartUtc < candidates[1].StartUtc);
    }

    // ─── helpers ───

    /// <summary>A showcase request for Monday 12 October at 10:00 in Rome that the supplier took on Thursday.</summary>
    private static async Task<ServiceRequest> TakenForMondayAsync(ServiceRequestScenario s, ShowcaseBookingInput? input = null)
    {
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync(input ?? await s.InputAsync(MondayAt10));
        return await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
    }

    private static ServiceRequestReminderService Reminders(ServiceRequestScenario s) =>
        new(s.Db, s.Kit.ShowcaseNotifier, NullLogger<ServiceRequestReminderService>.Instance, s.Clock);

    private sealed class ThrowingEmailQueue : Casazen.Infrastructure.Email.IEmailQueue
    {
        public bool Enqueue(string? to, Casazen.Infrastructure.Email.EmailContent content, string template) =>
            throw new InvalidOperationException("The email queue is down.");
    }
}
