using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Repositories;
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
/// PM-01 on real PostgreSQL, where the jobs run for real (session advisory locks, conditional updates): the daily CIN alert
/// and the nightly compliance check ignore the properties in long-term mode, and the generic save of a property never
/// writes the mode. The clock is a <see cref="FakeTimeProvider"/> at 08:00 UTC, the time of the recurring job.
/// </summary>
/// <remarks>
/// The tests of this class share one database and every run looks at every org: each test checks only the emails sent to its
/// own host.
/// </remarks>
public class PropertyRentalModePostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateOnly Deadline = new(2027, 3, 1);

    private readonly CasazenWebApplicationFactory _factory;

    public PropertyRentalModePostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task CinAlert_LongPropertyWithoutCin_IsNeverAlertedNorClaimed()
    {
        var host = await SeedHostAsync("alert-long");
        var longTerm = await SeedPropertyAsync(host, "Bilocale Lungo", RentalMode.Long, cinCode: null);
        var control = await SeedHostAsync("alert-control");
        await SeedPropertyAsync(control, "Casa Controllo", RentalMode.Short, cinCode: null);
        var alerts = NewHarness(Deadline.AddDays(-10));

        await alerts.RunAsync();
        await alerts.RunAsync();

        // The host with only a long-term property gets nothing; the control host (short stays, no CIN) does.
        Assert.Empty(alerts.EmailsTo(host.Email));
        Assert.Single(alerts.EmailsTo(control.Email));
        Assert.False(await HasAlertStateAsync(longTerm));
    }

    [PostgresFact]
    public async Task CinAlert_ShortAndLongPropertiesWithoutCin_TheEmailNamesOnlyTheShortOne()
    {
        var host = await SeedHostAsync("alert-mixed");
        await SeedPropertyAsync(host, "Casa Breve Senza Codice", RentalMode.Short, cinCode: null);
        await SeedPropertyAsync(host, "Bilocale Lungo Senza Codice", RentalMode.Long, cinCode: null);
        var alerts = NewHarness(Deadline.AddDays(-10));

        await alerts.RunAsync();

        var email = Assert.Single(alerts.EmailsTo(host.Email));
        Assert.Equal(EmailTemplates.Names.CinDeadlineAlert, email.Template);
        Assert.Contains("<li>Casa Breve Senza Codice</li>", email.Content.HtmlBody);
        Assert.DoesNotContain("Bilocale Lungo", email.Content.HtmlBody);
    }

    [PostgresFact]
    public async Task ComplianceCheck_LongActivePropertyWithoutCin_StaysActiveAndTheHostGetsNoEmail()
    {
        var host = await SeedHostAsync("check-long");
        var longTerm = await SeedPropertyAsync(
            host, "Bilocale Lungo", RentalMode.Long, cinCode: null, PropertyComplianceStatus.Active);
        var shortStay = await SeedPropertyAsync(
            host, "Casa Breve", RentalMode.Short, cinCode: null, PropertyComplianceStatus.Active);
        var alerts = NewHarness(Deadline.AddDays(-10));

        // The nightly check, as the job runs it, over the whole database.
        await alerts.RecalculateAllAsync();

        Assert.Equal(PropertyComplianceStatus.Active, (await LoadAsync(longTerm)).ComplianceStatus);
        Assert.Equal(PropertyComplianceStatus.Suspended, (await LoadAsync(shortStay)).ComplianceStatus);
        // One suspension email, for the short-rent property only.
        var email = Assert.Single(alerts.EmailsTo(host.Email));
        Assert.Equal(EmailTemplates.Names.PropertyComplianceSuspended, email.Template);
        Assert.Contains("Casa Breve", email.Content.Subject);
    }

    [PostgresFact]
    public async Task GenericSave_ChangedRentalModeOnTheEntity_IsNotWrittenToTheDatabase()
    {
        var host = await SeedHostAsync("save");
        var propertyId = await SeedPropertyAsync(host, "Casa", RentalMode.Short, cinCode: "IT058091C27G5FFZDZ");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
            property.Name = "Casa rinnovata";
            property.RentalMode = RentalMode.Long;

            await new PropertyRepository(db).UpdateAsync(property);
        }

        var stored = await LoadAsync(propertyId);
        Assert.Equal("Casa rinnovata", stored.Name);
        Assert.Equal(RentalMode.Short, stored.RentalMode);
    }

    [PostgresFact]
    public async Task ListIndex_OrgAndMode_IsInTheDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var indexes = await db.Database
            .SqlQuery<string>($"""SELECT indexname::text AS "Value" FROM pg_indexes WHERE tablename = 'Properties'""")
            .ToListAsync();

        Assert.Contains("IX_Properties_OrgId_RentalMode", indexes);
    }

    // ─── Seed ───────────────────────────────────────────────────────────────────────────────────────

    private sealed record SeededHost(string HostId, string Email, Guid OrgId);

    private async Task<SeededHost> SeedHostAsync(string label)
    {
        var hostId = $"auth0|pm01-{label}-{Guid.NewGuid():N}";
        var hostEmail = $"host-{Guid.NewGuid():N}@example.com";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        stored.ContactEmail = hostEmail;
        await db.SaveChangesAsync();
        return new SeededHost(hostId, hostEmail, org.Id);
    }

    /// <summary>
    /// A property with complete base data, a CIN certificate and a confirmed checklist (so that only the CIN can be
    /// missing) in the given mode and compliance status.
    /// </summary>
    private async Task<Guid> SeedPropertyAsync(
        SeededHost host,
        string name,
        RentalMode mode,
        string? cinCode,
        PropertyComplianceStatus status = PropertyComplianceStatus.Pending)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = host.HostId,
            OrgId = host.OrgId,
            Name = name,
            Description = "PM-01",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = cinCode,
            IsActive = true,
            RentalMode = mode,
            ComplianceStatus = status,
            ComplianceCheckedAt = status == PropertyComplianceStatus.Active ? new DateTime(2027, 1, 10, 4, 0, 0, DateTimeKind.Utc) : null,
        };
        db.Properties.Add(property);
        db.PropertyDocuments.Add(new PropertyDocument
        {
            PropertyId = property.Id,
            OrgId = host.OrgId,
            FileName = "cin.pdf",
            StorageUrl = "documents/cin.pdf",
            DocumentType = Core.Enums.DocumentType.CinCertificate,
            UploadedBy = host.HostId,
        });
        db.PropertySafetyChecklists.Add(SafetyChecklistTestData.CompleteAllElectric(property.Id, host.OrgId));
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task<Property> LoadAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private async Task<bool> HasAlertStateAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CinAlertStates.IgnoreQueryFilters().AnyAsync(s => s.PropertyId == propertyId);
    }

    private AlertHarness NewHarness(DateOnly romeDay) =>
        new(_factory, new FakeTimeProvider(new DateTimeOffset(romeDay.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero)));

    /// <summary>
    /// The CIN alert service and the compliance status service as the daily jobs build them, on the factory's database,
    /// writing to one recording email queue, with a clock the test moves.
    /// </summary>
    private sealed class AlertHarness(CasazenWebApplicationFactory factory, FakeTimeProvider clock)
    {
        private readonly RecordingEmailQueue _emails = new();

        public IReadOnlyList<(string? To, EmailContent Content, string Template)> EmailsTo(string recipient) =>
            _emails.Snapshot().Where(e => e.To == recipient).ToList();

        public async Task<CinDeadlineAlertRunResult> RunAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var links = EmailTestHelpers.Links();
            var service = new CinDeadlineAlertService(
                db,
                StatusService(scope, db, links),
                new NotificationService(
                    db, _emails, Mock.Of<IPushNotificationService>(), links, NullLogger<NotificationService>.Instance),
                new CinDeadlineCalendar(Options.Create(new CinOptions { ExposureDeadline = "2027-03-01" }), clock),
                NullLogger<CinDeadlineAlertService>.Instance,
                clock);
            return await service.RunAsync();
        }

        public async Task RecalculateAllAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await StatusService(scope, db, EmailTestHelpers.Links()).RecalculateAllAsync(dryRun: false);
        }

        private PropertyComplianceStatusService StatusService(
            IServiceScope scope, AppDbContext db, PublicSiteLinks links) =>
            new(
                db,
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                _emails,
                links,
                Options.Create(new ComplianceOptions { StatusCheck = { NotifyOnFirstCheck = true } }),
                NullLogger<PropertyComplianceStatusService>.Instance,
                clock);
    }
}
