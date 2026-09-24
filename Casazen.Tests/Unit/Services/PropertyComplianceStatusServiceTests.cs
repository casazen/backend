using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CO-06 (A5-20, A5-36): the compliance status of a property follows its activation blockers. An active property that
/// loses a requirement is suspended with the reason and one email to the host; a suspended one is reactivated as soon as
/// its requirements are complete again; a pending one is activated only by the host; the first check of a historic
/// property emails only when configured.
/// </summary>
public class PropertyComplianceStatusServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    private readonly RecordingEmailQueue _emails = new();

    [Fact]
    public async Task ReevaluateAsync_ActivePropertyWithoutCin_SuspendsWithReasonAndEmailsTheHost()
    {
        await using var db = CreateDb(nameof(ReevaluateAsync_ActivePropertyWithoutCin_SuspendsWithReasonAndEmailsTheHost));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetAsync(db, property.Id, p => p.CinCode = null);

        var check = await CreateService(db).ReevaluateAsync(property.Id);

        Assert.True(check.Suspended);
        Assert.True(check.HostNotified);
        Assert.Equal(["cin"], check.IncompleteSteps.Select(s => s.Id));
        var stored = await ReloadAsync(db, property.Id);
        Assert.Equal(PropertyComplianceStatus.Suspended, stored.ComplianceStatus);
        Assert.Equal(Now, stored.ComplianceSuspendedAt);
        Assert.Equal(["activation_cin_missing"], stored.ComplianceSuspensionReasons);
        Assert.Equal(Now, stored.ComplianceCheckedAt);

        var email = Assert.Single(_emails.Snapshot());
        Assert.Equal("host@villa.test", email.To);
        Assert.Equal(EmailTemplates.Names.PropertyComplianceSuspended, email.Template);
        Assert.Equal("Annuncio sospeso - Villa Test", email.Content.Subject);
        Assert.Contains("codice identificativo nazionale (CIN) mancante o non valido", email.Content.HtmlBody);
        Assert.DoesNotContain("checklist di sicurezza", email.Content.HtmlBody);
        Assert.Contains("nessuna prenotazione è stata cancellata", email.Content.HtmlBody);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/short-rent/properties/{property.Id:D}/activation\"", email.Content.HtmlBody);
    }

    [Fact]
    public async Task ReevaluateAsync_CalledAgainOnSuspendedProperty_NoSecondEmail()
    {
        await using var db = CreateDb(nameof(ReevaluateAsync_CalledAgainOnSuspendedProperty_NoSecondEmail));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetAsync(db, property.Id, p => p.CinCode = null);
        var service = CreateService(db);

        await service.ReevaluateAsync(property.Id);
        var second = await service.ReevaluateAsync(property.Id);

        Assert.False(second.Suspended);
        Assert.False(second.HostNotified);
        Assert.Equal(PropertyComplianceStatus.Suspended, second.Status);
        Assert.Single(_emails.Snapshot());
    }

    [Fact]
    public async Task ReevaluateAsync_ActiveCompliantProperty_StaysActiveWithoutEmail()
    {
        await using var db = CreateDb(nameof(ReevaluateAsync_ActiveCompliantProperty_StaysActiveWithoutEmail));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);

        var check = await CreateService(db).ReevaluateAsync(property.Id);

        Assert.Equal(PropertyComplianceStatus.Active, check.Status);
        Assert.Empty(check.IncompleteSteps);
        Assert.Empty(_emails.Snapshot());
        Assert.Equal(Now, (await ReloadAsync(db, property.Id)).ComplianceCheckedAt);
    }

    [Fact]
    public async Task ReevaluateAsync_PendingPropertyWithoutBlockers_IsNeverActivated()
    {
        await using var db = CreateDb(nameof(ReevaluateAsync_PendingPropertyWithoutBlockers_IsNeverActivated));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Pending);

        var check = await CreateService(db).ReevaluateAsync(property.Id);

        Assert.Equal(PropertyComplianceStatus.Pending, check.Status);
        Assert.Equal(PropertyComplianceStatus.Pending, (await ReloadAsync(db, property.Id)).ComplianceStatus);
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task ReevaluateAsync_SuspendedPropertyWithCinBack_ReactivatedWithoutSecondEmail()
    {
        await using var db = CreateDb(nameof(ReevaluateAsync_SuspendedPropertyWithCinBack_ReactivatedWithoutSecondEmail));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetAsync(db, property.Id, p => p.CinCode = null);
        var service = CreateService(db);
        await service.ReevaluateAsync(property.Id);

        await SetAsync(db, property.Id, p => p.CinCode = "IT058091C27G5FFZDZ");
        var afterFix = await service.ReevaluateAsync(property.Id);

        Assert.True(afterFix.Reactivated);
        Assert.False(afterFix.Suspended);
        Assert.Empty(afterFix.IncompleteSteps);
        var stored = await ReloadAsync(db, property.Id);
        Assert.Equal(PropertyComplianceStatus.Active, stored.ComplianceStatus);
        Assert.Null(stored.ComplianceSuspendedAt);
        Assert.Null(stored.ComplianceSuspensionReasons);
        Assert.Equal(Now, stored.ComplianceCompletedAt);
        Assert.Equal(Now, stored.ComplianceCheckedAt);
        Assert.Single(_emails.Snapshot()); // only the suspension
    }

    [Fact]
    public async Task ReevaluateAsync_SuspendedPropertyWithOneOfTwoBlockersSolved_StaysSuspendedWithItsReasons()
    {
        await using var db = CreateDb(nameof(ReevaluateAsync_SuspendedPropertyWithOneOfTwoBlockersSolved_StaysSuspendedWithItsReasons));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetAsync(db, property.Id, p => p.CinCode = null);
        await SetChecklistAsync(db, property.Id, c => c.ConfirmedAt = null);
        var service = CreateService(db);
        await service.ReevaluateAsync(property.Id);

        await SetAsync(db, property.Id, p => p.CinCode = "IT058091C27G5FFZDZ");
        var check = await service.ReevaluateAsync(property.Id);

        Assert.Equal(PropertyComplianceStatus.Suspended, check.Status);
        Assert.False(check.Reactivated);
        Assert.False(check.HostNotified);
        Assert.Equal(["safety"], check.IncompleteSteps.Select(s => s.Id));
        Assert.Equal(
            ["activation_cin_missing", "safety_confirmation_missing"],
            (await ReloadAsync(db, property.Id)).ComplianceSuspensionReasons);
        Assert.Single(_emails.Snapshot());
    }

    [Fact]
    public async Task ActivateAsync_SuspendedPropertyWithoutBlockers_Active()
    {
        await using var db = CreateDb(nameof(ActivateAsync_SuspendedPropertyWithoutBlockers_Active));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Suspended);

        var check = await CreateService(db).ActivateAsync(property.Id);

        Assert.True(check.Reactivated);
        Assert.Equal(PropertyComplianceStatus.Active, (await ReloadAsync(db, property.Id)).ComplianceStatus);
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task ActivateAsync_ActivePropertyWithIncompleteChecklist_SuspendsIt()
    {
        await using var db = CreateDb(nameof(ActivateAsync_ActivePropertyWithIncompleteChecklist_SuspendsIt));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetChecklistAsync(db, property.Id, c => c.ConfirmedAt = null);

        var check = await CreateService(db).ActivateAsync(property.Id);

        Assert.True(check.Suspended);
        Assert.Equal(["safety"], check.IncompleteSteps.Select(s => s.Id));
        Assert.Equal(["safety_confirmation_missing"], (await ReloadAsync(db, property.Id)).ComplianceSuspensionReasons);
        Assert.Contains("checklist di sicurezza (D.L. 145/2023) incompleta o non confermata", Assert.Single(_emails.Snapshot()).Content.HtmlBody);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ReevaluateAsync_FirstCheckOfHistoricProperty_EmailFollowsNotifyOnFirstCheck(bool notifyOnFirstCheck, int emails)
    {
        await using var db = CreateDb($"{nameof(ReevaluateAsync_FirstCheckOfHistoricProperty_EmailFollowsNotifyOnFirstCheck)}-{notifyOnFirstCheck}");
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active, neverChecked: true);
        db.PropertyDocuments.RemoveRange(db.PropertyDocuments.Where(d => d.PropertyId == property.Id));
        await db.SaveChangesAsync();

        var check = await CreateService(db, notifyOnFirstCheck).ReevaluateAsync(property.Id);

        Assert.True(check.Suspended);
        Assert.Equal(notifyOnFirstCheck, check.HostNotified);
        Assert.Equal(emails, _emails.Snapshot().Count);
        Assert.Equal(["activation_documents_missing"], (await ReloadAsync(db, property.Id)).ComplianceSuspensionReasons);
    }

    [Fact]
    public async Task RecalculateAllAsync_DryRun_ReportsWithoutChangingAnything()
    {
        await using var db = CreateDb(nameof(RecalculateAllAsync_DryRun_ReportsWithoutChangingAnything));
        var historic = await SeedCompliantAsync(db, PropertyComplianceStatus.Active, neverChecked: true);
        await SetAsync(db, historic.Id, p => p.CinCode = null);
        var checkedBefore = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetChecklistAsync(db, checkedBefore.Id, c => c.ConfirmedAt = null);
        await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        var suspendedComplete = await SeedCompliantAsync(db, PropertyComplianceStatus.Suspended);

        var report = await CreateService(db).RecalculateAllAsync(dryRun: true);

        Assert.NotNull(report);
        Assert.True(report.DryRun);
        Assert.Equal(
            (4, 2, 1, 1, 0),
            (report.Checked, report.Suspended, report.Reactivated, report.HostsNotified, report.Failed));
        Assert.Equal(1, report.SuspendedByBlocker["activation_cin_missing"]);
        Assert.Equal(1, report.SuspendedByBlocker["safety_confirmation_missing"]);
        Assert.Empty(_emails.Snapshot());
        db.ChangeTracker.Clear();
        Assert.All(
            await db.Properties.Where(p => p.Id != suspendedComplete.Id).ToListAsync(),
            p => Assert.Equal(PropertyComplianceStatus.Active, p.ComplianceStatus));
        Assert.Equal(PropertyComplianceStatus.Suspended, (await ReloadAsync(db, suspendedComplete.Id)).ComplianceStatus);
        Assert.Null((await ReloadAsync(db, historic.Id)).ComplianceCheckedAt);
    }

    [Fact]
    public async Task RecalculateAllAsync_TwoRuns_SuspendAndEmailOnce()
    {
        await using var db = CreateDb(nameof(RecalculateAllAsync_TwoRuns_SuspendAndEmailOnce));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetAsync(db, property.Id, p => p.MaxGuests = 0);
        var pending = await SeedCompliantAsync(db, PropertyComplianceStatus.Pending);
        await SetAsync(db, pending.Id, p => p.CinCode = null);
        var service = CreateService(db);

        var first = await service.RecalculateAllAsync(dryRun: false);
        var second = await service.RecalculateAllAsync(dryRun: false);

        // The pending property is never evaluated; the suspended one is evaluated again but stays suspended silently.
        Assert.Equal((1, 1, 0, 1), (first!.Checked, first.Suspended, first.Reactivated, first.HostsNotified));
        Assert.Equal((1, 0, 0, 0), (second!.Checked, second.Suspended, second.Reactivated, second.HostsNotified));
        Assert.Contains("dati di base incompleti", Assert.Single(_emails.Snapshot()).Content.HtmlBody);
        Assert.Equal(PropertyComplianceStatus.Suspended, (await ReloadAsync(db, property.Id)).ComplianceStatus);
        Assert.Equal(PropertyComplianceStatus.Pending, (await ReloadAsync(db, pending.Id)).ComplianceStatus);
    }

    [Fact]
    public async Task RecalculateAllAsync_SuspendedPropertyCompleteAgain_ReactivatedOnceWithoutEmail()
    {
        await using var db = CreateDb(nameof(RecalculateAllAsync_SuspendedPropertyCompleteAgain_ReactivatedOnceWithoutEmail));
        var property = await SeedCompliantAsync(db, PropertyComplianceStatus.Active);
        await SetChecklistAsync(db, property.Id, c => c.ConfirmedAt = null);
        var service = CreateService(db);
        await service.RecalculateAllAsync(dryRun: false);
        // Requirement completed again without a request that re-evaluates it (e.g. a row fixed by hand).
        await SetChecklistAsync(db, property.Id, c => c.ConfirmedAt = Now);

        var first = await service.RecalculateAllAsync(dryRun: false);
        var second = await service.RecalculateAllAsync(dryRun: false);

        Assert.Equal((1, 0, 1, 0), (first!.Checked, first.Suspended, first.Reactivated, first.HostsNotified));
        Assert.Equal((1, 0, 0, 0), (second!.Checked, second.Suspended, second.Reactivated, second.HostsNotified));
        var stored = await ReloadAsync(db, property.Id);
        Assert.Equal(PropertyComplianceStatus.Active, stored.ComplianceStatus);
        Assert.Null(stored.ComplianceSuspendedAt);
        Assert.Single(_emails.Snapshot()); // only the suspension
    }

    private PropertyComplianceStatusService CreateService(AppDbContext db, bool notifyOnFirstCheck = false) =>
        new(
            db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Compliance:RequiredDocuments:default:0"] = "CinCertificate",
                })
                .Build(),
            _emails,
            EmailTestHelpers.Links(),
            Options.Create(new ComplianceOptions { StatusCheck = { NotifyOnFirstCheck = notifyOnFirstCheck } }),
            NullLogger<PropertyComplianceStatusService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(Now)));

    private static AppDbContext CreateDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>
    /// Base data, valid CIN, CIN certificate and a complete confirmed checklist. <paramref name="neverChecked"/> = a
    /// property published before CO-06 and never evaluated.
    /// </summary>
    private static async Task<Property> SeedCompliantAsync(
        AppDbContext db,
        PropertyComplianceStatus status,
        bool neverChecked = false)
    {
        var org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}", ContactEmail = "host@villa.test" };
        db.Orgs.Add(org);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Villa Test",
            Address = "Via Roma 1",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = status,
            ComplianceCheckedAt = neverChecked ? null : Now.AddDays(-1),
        };
        db.Properties.Add(property);
        db.PropertyDocuments.Add(new PropertyDocument
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            FileName = "cin.pdf",
            StorageUrl = "documents/cin.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = property.OwnerId,
        });
        db.PropertySafetyChecklists.Add(SafetyChecklistTestData.CompleteAllElectric(property.Id, org.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return property;
    }

    private static async Task SetAsync(AppDbContext db, Guid propertyId, Action<Property> change)
    {
        var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SetChecklistAsync(AppDbContext db, Guid propertyId, Action<PropertySafetyChecklist> change)
    {
        var checklist = await db.PropertySafetyChecklists.SingleAsync(c => c.PropertyId == propertyId);
        change(checklist);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Property> ReloadAsync(AppDbContext db, Guid propertyId)
    {
        db.ChangeTracker.Clear();
        return await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }
}
