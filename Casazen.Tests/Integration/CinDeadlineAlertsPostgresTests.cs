using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-20 (A5-31) on real PostgreSQL: the daily CIN alert emails the host of properties without a valid CIN once per stage
/// of the configured deadline (days left, the deadline day, deadline passed: never "today" again), never twice whatever
/// the number of runs, nothing on top of the CO-06 suspension email, and one reminder without a date when no deadline
/// is configured. The clock is a <see cref="FakeTimeProvider"/> set to 08:00 UTC, the time of the recurring job.
/// </summary>
/// <remarks>
/// The tests of this class share one database and every run looks at every org: each test checks only the emails sent
/// to its own host.
/// </remarks>
public class CinDeadlineAlertsPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateOnly Deadline = new(2027, 3, 1);

    private readonly CasazenWebApplicationFactory _factory;

    public CinDeadlineAlertsPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task RunAsync_TenDaysBeforeOnTheDayAnd200DaysAfter_SendsTheMessageOfEachPhaseOnce()
    {
        var host = await SeedHostAsync("phases");
        var missing = await SeedPropertyAsync(host, "Casa Senza Codice", cinCode: null);
        await SeedPropertyAsync(host, "Casa Vecchio Formato", cinCode: "IT-12345-0123456789");
        await SeedPropertyAsync(host, "Casa In Regola", cinCode: ValidCin());
        var alerts = NewHarness(Deadline.AddDays(-10));

        await alerts.RunAsync();
        await alerts.RunAsync();

        var upcoming = Assert.Single(alerts.EmailsTo(host.Email));
        Assert.Equal(EmailTemplates.Names.CinDeadlineAlert, upcoming.Template);
        Assert.Equal("CIN da inserire entro il 01/03/2027", upcoming.Content.Subject);
        Assert.Contains("Mancano <strong>10 giorni</strong> alla scadenza del 01/03/2027", upcoming.Content.HtmlBody);
        Assert.Contains("<li>Casa Senza Codice</li>", upcoming.Content.HtmlBody);
        Assert.Contains("<li>Casa Vecchio Formato</li>", upcoming.Content.HtmlBody);
        Assert.DoesNotContain("Casa In Regola", upcoming.Content.HtmlBody);
        Assert.Contains("art. 13-ter, comma 8, D.L. 145/2023", upcoming.Content.HtmlBody);
        Assert.Contains("href=\"https://casazen-app.test/app/short-rent/compliance/cin\"", upcoming.Content.HtmlBody);

        alerts.Clock.SetUtcNow(At(Deadline));
        await alerts.RunAsync();
        await alerts.RunAsync();

        var today = alerts.EmailsTo(host.Email)[^1];
        Assert.Equal(2, alerts.EmailsTo(host.Email).Count);
        Assert.Equal("CIN da inserire: la scadenza del 01/03/2027 è oggi", today.Content.Subject);
        Assert.Contains("La scadenza del 01/03/2027 per il codice identificativo nazionale (CIN) è <strong>oggi</strong>", today.Content.HtmlBody);

        alerts.Clock.SetUtcNow(At(Deadline.AddDays(200)));
        await alerts.RunAsync();
        await alerts.RunAsync();
        alerts.Clock.SetUtcNow(At(Deadline.AddDays(201)));
        await alerts.RunAsync();

        var emails = alerts.EmailsTo(host.Email);
        Assert.Equal(3, emails.Count);
        var passed = emails[^1];
        Assert.Equal("CIN mancante: scadenza del 01/03/2027 superata", passed.Content.Subject);
        Assert.Contains("è <strong>superata</strong> e queste proprietà non hanno ancora un CIN valido", passed.Content.HtmlBody);
        Assert.DoesNotContain("oggi", passed.Content.HtmlBody);
        Assert.Contains("da 800 a 8.000 euro", passed.Content.HtmlBody);
        Assert.Contains("da 500 a 5.000 euro", passed.Content.HtmlBody);

        var state = await StateAsync(missing);
        Assert.Equal(CinAlertStages.Final, state.Stage);
        Assert.Equal(Deadline, state.Deadline);
        Assert.Equal(3, state.AlertCount);
        Assert.Equal(host.OrgId, state.OrgId);
    }

    [PostgresFact]
    public async Task RunAsync_DailyRunsFrom40DaysBeforeTo10DaysAfter_SendsOneEmailPerStage()
    {
        var host = await SeedHostAsync("daily");
        await SeedPropertyAsync(host, "Casa Giornaliera", cinCode: null);
        var alerts = NewHarness(Deadline.AddDays(-40));

        for (var day = 0; day <= 50; day++)
        {
            await alerts.RunAsync();
            alerts.Clock.Advance(TimeSpan.FromDays(1));
        }

        var bodies = alerts.EmailsTo(host.Email).Select(e => e.Content.HtmlBody).ToList();
        Assert.Equal(5, bodies.Count);
        Assert.Contains("Mancano <strong>30 giorni</strong>", bodies[0]);
        Assert.Contains("Mancano <strong>7 giorni</strong>", bodies[1]);
        Assert.Contains("Manca <strong>1 giorno</strong>", bodies[2]);
        Assert.Contains("è <strong>oggi</strong>", bodies[3]);
        Assert.Contains("è <strong>superata</strong>", bodies[4]);
    }

    [PostgresFact]
    public async Task RunAsync_BeforeTheFirstThreshold_SendsNothing()
    {
        var host = await SeedHostAsync("early");
        var property = await SeedPropertyAsync(host, "Casa Presto", cinCode: null);
        var alerts = NewHarness(Deadline.AddDays(-31));

        var result = await alerts.RunAsync();

        Assert.Null(result.Stage);
        Assert.Empty(alerts.EmailsTo(host.Email));
        Assert.False(await HasStateAsync(property));
    }

    [PostgresFact]
    public async Task RunAsync_PropertyAlreadySuspendedForMissingCin_SendsNoCinAlert()
    {
        var host = await SeedHostAsync("suspended");
        var property = await SeedPropertyAsync(
            host, "Casa Sospesa", cinCode: null, PropertyComplianceStatus.Suspended, suspensionReasons: ["activation_cin_missing"]);
        var alerts = NewHarness(Deadline.AddDays(200));

        await alerts.RunAsync();

        Assert.Empty(alerts.EmailsTo(host.Email));
        Assert.False(await HasStateAsync(property));
    }

    [PostgresFact]
    public async Task RunAsync_PublishedPropertyWithoutCin_OnlyTheCo06SuspensionEmail()
    {
        var host = await SeedHostAsync("published");
        var property = await SeedPropertyAsync(
            host,
            "Casa Pubblicata",
            cinCode: null,
            PropertyComplianceStatus.Active,
            complianceCheckedAt: new DateTime(2027, 1, 10, 4, 0, 0, DateTimeKind.Utc));
        var alerts = NewHarness(Deadline.AddDays(200));

        await alerts.RunAsync();
        alerts.Clock.Advance(TimeSpan.FromDays(1));
        await alerts.RunAsync();

        var email = Assert.Single(alerts.EmailsTo(host.Email));
        Assert.Equal(EmailTemplates.Names.PropertyComplianceSuspended, email.Template);
        Assert.Contains("codice identificativo nazionale (CIN) mancante o non valido", email.Content.HtmlBody);
        Assert.Equal(PropertyComplianceStatus.Suspended, (await LoadPropertyAsync(property)).ComplianceStatus);
        Assert.False(await HasStateAsync(property));
    }

    [PostgresFact]
    public async Task RunAsync_NoDeadlineConfigured_SendsOneReminderWithoutDate()
    {
        var host = await SeedHostAsync("no-deadline");
        var property = await SeedPropertyAsync(host, "Casa Senza Data", cinCode: null);
        var alerts = NewHarness(new DateOnly(2027, 9, 1), exposureDeadline: null);

        await alerts.RunAsync();
        await alerts.RunAsync();
        alerts.Clock.Advance(TimeSpan.FromDays(30));
        await alerts.RunAsync();

        var reminder = Assert.Single(alerts.EmailsTo(host.Email));
        Assert.Equal("CIN obbligatorio: proprietà senza codice valido", reminder.Content.Subject);
        Assert.Contains("Queste proprietà non hanno ancora un codice identificativo nazionale (CIN) valido", reminder.Content.HtmlBody);
        Assert.Contains("<li>Casa Senza Data</li>", reminder.Content.HtmlBody);
        Assert.DoesNotContain("scadenza", reminder.Content.HtmlBody);
        var state = await StateAsync(property);
        Assert.Null(state.Deadline);
        Assert.Equal(CinAlertStages.Final, state.Stage);
    }

    [PostgresFact]
    public async Task RunAsync_DeadlineConfiguredAfterTheReminder_StartsTheSequenceForIt()
    {
        var host = await SeedHostAsync("configured-later");
        await SeedPropertyAsync(host, "Casa Configurata Dopo", cinCode: null);
        var withoutDeadline = NewHarness(Deadline.AddDays(-20), exposureDeadline: null);
        await withoutDeadline.RunAsync();

        var withDeadline = NewHarness(Deadline.AddDays(-5));
        await withDeadline.RunAsync();
        await withDeadline.RunAsync();

        Assert.Single(withoutDeadline.EmailsTo(host.Email));
        var upcoming = Assert.Single(withDeadline.EmailsTo(host.Email));
        Assert.Contains("Mancano <strong>5 giorni</strong> alla scadenza del 01/03/2027", upcoming.Content.HtmlBody);
    }

    [PostgresFact]
    public async Task RunAsync_TwoRunsAtOnce_AlertTheHostOnce()
    {
        var host = await SeedHostAsync("concurrent");
        await SeedPropertyAsync(host, "Casa Concorrente", cinCode: null);
        var alerts = NewHarness(Deadline.AddDays(-3));

        var results = await Task.WhenAll(alerts.RunAsync(), alerts.RunAsync());

        Assert.Single(alerts.EmailsTo(host.Email));
        Assert.Contains(results, r => !r.Skipped && r.PropertiesAlerted > 0);
    }

    private AlertHarness NewHarness(DateOnly romeDay, string? exposureDeadline = "2027-03-01") =>
        new(_factory, new FakeTimeProvider(At(romeDay)), new CinOptions { ExposureDeadline = exposureDeadline });

    /// <summary>08:00 UTC of <paramref name="day"/>: the time of the recurring job, the same calendar day in Rome.</summary>
    private static DateTimeOffset At(DateOnly day) => new(day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);

    private async Task<SeededHost> SeedHostAsync(string label)
    {
        var hostId = $"auth0|co20-{label}-{Guid.NewGuid():N}";
        var hostEmail = $"host-{Guid.NewGuid():N}@example.com";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        stored.ContactEmail = hostEmail;
        await db.SaveChangesAsync();
        return new SeededHost(hostId, hostEmail, org.Id);
    }

    private async Task<Guid> SeedPropertyAsync(
        SeededHost host,
        string name,
        string? cinCode,
        PropertyComplianceStatus status = PropertyComplianceStatus.Pending,
        List<string>? suspensionReasons = null,
        DateTime? complianceCheckedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = host.HostId,
            OrgId = host.OrgId,
            Name = name,
            Description = "CO-20",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = cinCode,
            IsActive = true,
            ComplianceStatus = status,
            ComplianceSuspendedAt = status == PropertyComplianceStatus.Suspended
                ? new DateTime(2027, 1, 10, 4, 0, 0, DateTimeKind.Utc)
                : null,
            ComplianceSuspensionReasons = suspensionReasons,
            ComplianceCheckedAt = complianceCheckedAt,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task<CinAlertState> StateAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CinAlertStates.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.PropertyId == propertyId);
    }

    private async Task<bool> HasStateAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CinAlertStates.IgnoreQueryFilters().AnyAsync(s => s.PropertyId == propertyId);
    }

    private async Task<Property> LoadPropertyAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    /// <summary>A valid CIN (CinFormat) never used by another property.</summary>
    private static string ValidCin() => $"IT058091C2{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private sealed record SeededHost(string HostId, string Email, Guid OrgId);

    /// <summary>
    /// The CIN alert service as the daily job builds it, on the factory's database, with the CO-06 compliance status
    /// service and the notification service writing to one recording email queue, and a clock the test moves.
    /// </summary>
    private sealed class AlertHarness(CasazenWebApplicationFactory factory, FakeTimeProvider clock, CinOptions options)
    {
        public FakeTimeProvider Clock { get; } = clock;

        public RecordingEmailQueue Emails { get; } = new();

        public IReadOnlyList<(string? To, EmailContent Content, string Template)> EmailsTo(string recipient) =>
            Emails.Snapshot().Where(e => e.To == recipient).ToList();

        public async Task<CinDeadlineAlertRunResult> RunAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var links = EmailTestHelpers.Links();
            var complianceStatus = new PropertyComplianceStatusService(
                db,
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                Emails,
                links,
                Options.Create(new ComplianceOptions()),
                NullLogger<PropertyComplianceStatusService>.Instance,
                Clock);
            var service = new CinDeadlineAlertService(
                db,
                complianceStatus,
                new NotificationService(
                    db, Emails, Mock.Of<IPushNotificationService>(), links, NullLogger<NotificationService>.Instance),
                new CinDeadlineCalendar(Options.Create(options), Clock),
                NullLogger<CinDeadlineAlertService>.Instance,
                Clock);
            return await service.RunAsync();
        }
    }
}
