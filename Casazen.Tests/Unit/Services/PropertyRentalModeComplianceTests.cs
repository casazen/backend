using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-01: a property in long-term mode is outside the compliance of the short stays. The status service neither suspends it
/// nor writes to its host (re-evaluation, nightly check) and refuses to activate it; the CIN alert, the CIN summaries (host
/// and staff) and the activation cockpit leave it out. The same property in short-rent mode keeps being handled as before.
/// </summary>
public class PropertyRentalModeComplianceTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

    private readonly RecordingEmailQueue _emails = new();

    // ─── Compliance status (CO-06) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReevaluateAsync_LongActivePropertyWithoutCin_ChangesNothingAndEmailsNobody()
    {
        await using var db = CreateDb();
        var property = await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Active, cinCode: null);

        var check = await StatusService(db).ReevaluateAsync(property.Id);

        Assert.False(check.Suspended);
        Assert.False(check.HostNotified);
        Assert.Empty(check.IncompleteSteps);
        Assert.Equal(PropertyComplianceStatus.Active, check.Status);
        Assert.Empty(_emails.Snapshot());
        var stored = await ReloadAsync(db, property.Id);
        Assert.Equal(PropertyComplianceStatus.Active, stored.ComplianceStatus);
        Assert.Null(stored.ComplianceSuspendedAt);
        Assert.Null(stored.ComplianceSuspensionReasons);
    }

    [Fact]
    public async Task ReevaluateAsync_ShortActivePropertyWithoutCin_IsStillSuspendedAndTheHostEmailed()
    {
        // The same data in short-rent mode: the behaviour of CO-06 does not change.
        await using var db = CreateDb();
        var property = await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Active, cinCode: null);

        var check = await StatusService(db).ReevaluateAsync(property.Id);

        Assert.True(check.Suspended);
        Assert.True(check.HostNotified);
        Assert.Single(_emails.Snapshot());
    }

    [Fact]
    public async Task ActivateAsync_LongProperty_Is422AndLeavesTheStatusAlone()
    {
        await using var db = CreateDb();
        var property = await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: "IT058091C27G5FFZDZ");

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => StatusService(db).ActivateAsync(property.Id));

        Assert.Equal(PropertyRentalModeErrorCodes.NotBookableInLongMode, error.Code);
        Assert.Equal("PropertyNotBookableInLongMode", error.MessageKey);
        Assert.Equal(PropertyComplianceStatus.Pending, (await ReloadAsync(db, property.Id)).ComplianceStatus);
    }

    [Fact]
    public async Task RecalculateAllAsync_LongProperties_AreNotCheckedNorSuspendedNorEmailed()
    {
        await using var db = CreateDb();
        var shortStay = await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Active, cinCode: null);
        var activeLong = await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Active, cinCode: null);
        var suspendedLong = await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Suspended, cinCode: "IT058091C27G5FFZDZ");

        var report = await StatusService(db).RecalculateAllAsync(dryRun: false);

        // Only the short-rent property is looked at: suspended, and its host told once.
        Assert.Equal((1, 1, 0, 1), (report!.Checked, report.Suspended, report.Reactivated, report.HostsNotified));
        Assert.Single(_emails.Snapshot());
        Assert.Equal(PropertyComplianceStatus.Suspended, (await ReloadAsync(db, shortStay.Id)).ComplianceStatus);
        Assert.Equal(PropertyComplianceStatus.Active, (await ReloadAsync(db, activeLong.Id)).ComplianceStatus);
        // A long-term property that was suspended is not reactivated by the check either: it is not looked at.
        Assert.Equal(PropertyComplianceStatus.Suspended, (await ReloadAsync(db, suspendedLong.Id)).ComplianceStatus);
    }

    [Fact]
    public async Task RecalculateAllAsync_DryRunWithLongProperties_CountsOnlyTheShortRentOnes()
    {
        await using var db = CreateDb();
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Active, cinCode: null);
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Active, cinCode: null);

        var report = await StatusService(db).RecalculateAllAsync(dryRun: true);

        Assert.Equal((1, 1), (report!.Checked, report.Suspended));
        Assert.Empty(_emails.Snapshot());
    }

    // ─── CIN alert (CO-20) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CandidateProperties_LongProperties_AreNeverAlerted()
    {
        await using var db = CreateDb();
        var shortPending = await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Pending, cinCode: null);
        var shortActive = await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Active, cinCode: null);
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: null);
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Active, cinCode: null);

        var candidates = await CinDeadlineAlertService.CandidateProperties(db.Properties.AsNoTracking())
            .Select(p => p.Id)
            .ToListAsync();

        Assert.Equal(new[] { shortPending.Id, shortActive.Id }.Order(), candidates.Order());
    }

    [Fact]
    public async Task CandidateProperties_ShortProperties_KeepTheRulesOfCo20()
    {
        // Active and not suspended, pending or active: unchanged by the mode.
        await using var db = CreateDb();
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Suspended, cinCode: null);
        var inactive = await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Pending, cinCode: null);
        await SetAsync(db, inactive.Id, p => p.IsActive = false);

        var candidates = await CinDeadlineAlertService.CandidateProperties(db.Properties.AsNoTracking()).ToListAsync();

        Assert.Empty(candidates);
    }

    // ─── CIN summaries (host and staff) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCinComplianceAsync_HostSummary_IgnoresLongProperties()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Active, cinCode: "IT058091C27G5FFZDZ", orgId: orgId, name: "Casa Breve 1");
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Pending, cinCode: null, orgId: orgId, name: "Casa Breve 2");
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: null, orgId: orgId, name: "Casa Lungo 1");
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: "IT-12345-0123456789", orgId: orgId, name: "Casa Lungo 2");
        var service = new PropertyService(
            new PropertyRepository(db),
            Mock.Of<IPropertyComplianceStatusService>(),
            new CinDeadlineCalendar(Options.Create(new CinOptions()), TimeProvider.System),
            NullLogger<PropertyService>.Instance);

        var result = await service.GetCinComplianceAsync(new HostScope(orgId, null), null, 1, 50);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal((1, 1, 0), (result.Summary.Valid, result.Summary.Missing, result.Summary.Invalid));
        Assert.True(result.Summary.HasNonCompliant);
        Assert.Equal(["Casa Breve 1", "Casa Breve 2"], result.Items.Select(item => item.PropertyName).Order());
    }

    [Fact]
    public async Task GetCinComplianceAsync_HostSummary_OfAnOrgOnlyWithLongProperties_HasNothingToComply()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: null, orgId: orgId);
        var service = new PropertyService(
            new PropertyRepository(db),
            Mock.Of<IPropertyComplianceStatusService>(),
            new CinDeadlineCalendar(Options.Create(new CinOptions()), TimeProvider.System),
            NullLogger<PropertyService>.Instance);

        var result = await service.GetCinComplianceAsync(new HostScope(orgId, null), null, 1, 50);

        Assert.Equal(0, result.TotalCount);
        Assert.Equal((0, 0, 0), (result.Summary.Valid, result.Summary.Missing, result.Summary.Invalid));
        Assert.False(result.Summary.HasNonCompliant);
    }

    [Fact]
    public async Task GetStatsAsync_StaffCinFigures_CountOnlyTheShortRentProperties()
    {
        await using var db = CreateDb();
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Active, cinCode: "IT058091C27G5FFZDZ");
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Pending, cinCode: null);
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: null);
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: "IT-12345-0123456789");

        var stats = await new AdminService(db, NullLogger<AdminService>.Instance).GetStatsAsync();

        // Every property is still counted as a property; the CIN figures are about the short stays.
        Assert.Equal(4, stats.TotalProperties);
        Assert.Equal((1, 1, 0, 2), (stats.CinValid, stats.CinMissing, stats.CinInvalid, stats.CinTotal));
    }

    [Fact]
    public async Task GetCinComplianceAsync_StaffList_LeavesLongPropertiesOut()
    {
        await using var db = CreateDb();
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Pending, cinCode: null, name: "Casa Breve");
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: null, name: "Casa Lungo");

        var (items, total) = await new AdminService(db, NullLogger<AdminService>.Instance)
            .GetCinComplianceAsync("missing", 1, 50);

        Assert.Equal(1, total);
        Assert.Equal(["Casa Breve"], items.Select(i => i.PropertyName));
    }

    // ─── Cockpit (activation wizard summary) ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_Cockpit_DoesNotAskToActivateALongProperty()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, RentalMode.Short, PropertyComplianceStatus.Pending, cinCode: null, orgId: orgId, name: "Casa Breve");
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: null, orgId: orgId, name: "Casa Lungo");
        await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Suspended, cinCode: null, orgId: orgId, name: "Casa Lungo 2");

        var summary = await WizardService(db, Mock.Of<IPropertyComplianceStatusService>()).GetSummaryAsync(orgId);

        Assert.Equal(1, summary.PropertiesPending.Count);
        Assert.Equal(["Casa Breve"], summary.PropertiesPending.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task CompleteActivationAsync_LongProperty_Is422BeforeTheTermsAndWithoutActivating()
    {
        await using var db = CreateDb();
        var property = await SeedAsync(db, RentalMode.Long, PropertyComplianceStatus.Pending, cinCode: "IT058091C27G5FFZDZ");
        var status = new Mock<IPropertyComplianceStatusService>();

        // Terms not accepted either: the reason of the long-term mode comes first.
        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            WizardService(db, status.Object).CompleteActivationAsync(property.Id, "auth0|owner", tosAccepted: null));

        Assert.Equal(PropertyRentalModeErrorCodes.NotBookableInLongMode, error.Code);
        status.Verify(s => s.ActivateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private PropertyComplianceStatusService StatusService(AppDbContext db) =>
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
            Options.Create(new ComplianceOptions { StatusCheck = { NotifyOnFirstCheck = true } }),
            NullLogger<PropertyComplianceStatusService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(Now)));

    private static ComplianceWizardService WizardService(AppDbContext db, IPropertyComplianceStatusService status) =>
        new(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IStayLifecycleService>(),
            Mock.Of<ITouristTaxQuoteService>(),
            status,
            Mock.Of<ILogger<ComplianceWizardService>>(),
            new FixedTimeProvider(new DateTimeOffset(Now)));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// A property whose other data are complete (base data, CIN certificate, confirmed safety checklist) in the given mode and
    /// compliance status, with <paramref name="cinCode"/> as its CIN.
    /// </summary>
    private static async Task<Property> SeedAsync(
        AppDbContext db,
        RentalMode mode,
        PropertyComplianceStatus status,
        string? cinCode,
        Guid? orgId = null,
        string? name = null)
    {
        var org = orgId is { } existing ? await db.Orgs.FindAsync(existing) : null;
        if (org is null)
        {
            org = new OrgEntity
            {
                Id = orgId ?? Guid.NewGuid(),
                Name = "Org",
                Slug = $"org-{Guid.NewGuid():N}",
                ContactEmail = "host@villa.test",
            };
            db.Orgs.Add(org);
        }

        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = name ?? $"Casa {mode}",
            Address = $"Via Roma {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = cinCode,
            IsActive = true,
            RentalMode = mode,
            ComplianceStatus = status,
            ComplianceCheckedAt = Now.AddDays(-1),
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

    private static async Task<Property> ReloadAsync(AppDbContext db, Guid propertyId)
    {
        db.ChangeTracker.Clear();
        return await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }
}
