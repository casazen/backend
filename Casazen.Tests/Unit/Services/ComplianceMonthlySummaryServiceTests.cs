using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T02 (NF-52, CHK-12 point 6, AC-NF52-2, AC-NF52-10): the monthly compliance summary. Each active short-rent organization gets one email for the
/// previous month, once however many runs, retries or concurrent runs there are; «all compliant» is said only when the coverage report says every applicable obligation
/// has its proof; the email holds counts, names of actions and a link and never a guest, a document or a booking code (the privacy proof). EF InMemory; the coverage
/// report is a mock, the email queue a recorder.
/// </summary>
public class ComplianceMonthlySummaryServiceTests : IDisposable
{
    private const string OrgAddress = "contatto@example.com";
    private const string GuestName = "Anna Bianchi";
    private const string GuestEmail = "anna.bianchi@example.com";
    private const string GuestPhone = "+39 333 1234567";
    private const string DocumentNumber = "AX1234567";
    private const string BookingCode = "ABCDE-FGHJK";
    private const string PropertyName = "Villa Rosa";

    private static readonly DateOnly FirstOfNovember = new(2026, 11, 1);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero)); // 07:00 in Rome
    private readonly Mock<IComplianceArchiveService> _archive = new();
    private readonly Mock<IFeatureFlags> _flags = new();
    private readonly RecordingEmailQueue _emails = new();

    public ComplianceMonthlySummaryServiceTests()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(true);
    }

    public void Dispose() => _db.Dispose();

    private ComplianceMonthlySummaryService Service(IComplianceAlertRecipientResolver? recipients = null) =>
        new(
            _db,
            _archive.Object,
            _emails,
            EmailTestHelpers.Links(),
            _flags.Object,
            NullLogger<ComplianceMonthlySummaryService>.Instance,
            recipients,
            _clock);

    private async Task<Guid> SeedOrgAsync(
        string? contactEmail = OrgAddress,
        string language = "it",
        bool active = true,
        OrgType type = OrgType.Host,
        RentalMode propertyMode = RentalMode.Short,
        bool withProperty = true,
        DateTime? createdAt = null)
    {
        var org = OrgTeamTestData.AddOrg(_db, type);
        org.ContactEmail = contactEmail ?? string.Empty;
        org.DefaultLanguage = language;
        org.IsActive = active;
        org.CreatedAt = createdAt ?? new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        if (withProperty)
        {
            _db.Properties.Add(new Property
            {
                OrgId = org.Id,
                OwnerId = "auth0|owner",
                Name = PropertyName,
                Address = "Via Test 1",
                City = "Roma",
                RentalMode = propertyMode,
            });
        }

        await _db.SaveChangesAsync();
        return org.Id;
    }

    private static ComplianceReportRow Row(
        ComplianceItemKind kind,
        ComplianceEvidenceClass evidence,
        ComplianceCockpitAction? action,
        string reason,
        string? bookingCode = null) =>
        new(
            kind,
            Guid.NewGuid(),
            PropertyName,
            kind == ComplianceItemKind.Alloggiati ? Guid.NewGuid() : null,
            bookingCode,
            kind == ComplianceItemKind.Alloggiati ? new DateOnly(2026, 10, 12) : null,
            kind == ComplianceItemKind.Alloggiati ? null : "2026-10",
            evidence,
            evidence == ComplianceEvidenceClass.OfficialProof ? ComplianceEvidenceSources.ManualUpload : null,
            null,
            null,
            action,
            reason,
            null);

    private static ComplianceCoverageReport Report(params ComplianceReportRow[] rows)
    {
        var counts = new ComplianceCoverageCounts(
            rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.OfficialProof),
            rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.ExternalVerificationRequired),
            rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.Missing),
            rows.Count(r => r.EvidenceClass == ComplianceEvidenceClass.NotApplicable));
        return new ComplianceCoverageReport(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            null,
            new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc),
            counts.ExternalVerificationRequired == 0 && counts.Missing == 0,
            counts,
            rows);
    }

    private void ArchiveSays(Guid orgId, ComplianceCoverageReport report) =>
        _archive
            .Setup(a => a.GetCoverageReportAsync(It.Is<HostScope>(s => s.OrgId == orgId), It.IsAny<ComplianceReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

    private static ComplianceCoverageReport AllCompliantMonth(int stays) =>
        Report(Enumerable.Range(0, stays)
            .Select(_ => Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.OfficialProof, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.ReceiptArchived))
            .ToArray());

    // ─── Once per organization and month ───

    [Fact]
    public async Task RunAsync_OnTheFirstOfTheMonth_SendsTheSummaryOfTheMonthBefore_OnceHoweverManyRuns()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(42));

        var first = await Service().RunAsync(FirstOfNovember, CancellationToken.None);
        var second = await Service().RunAsync(FirstOfNovember, CancellationToken.None);
        var retry = await Service().RunAsync(new DateOnly(2026, 11, 3), CancellationToken.None);

        Assert.Equal(new ComplianceMonthlySummaryRunResult(false, 1, 1), first);
        Assert.Equal(0, second.Sent);
        Assert.Equal(0, retry.Sent);
        var email = Assert.Single(_emails.Queued);
        Assert.Equal(OrgAddress, email.To);
        Assert.Equal("compliance-monthly-summary", email.Template);
        Assert.Equal("Adempimenti di ottobre 2026: soggiorni 42, tutto in regola", email.Content.Subject);
        Assert.Contains("soggiorni del mese <strong>42</strong>", email.Content.HtmlBody, StringComparison.Ordinal);
        var row = await _db.ComplianceMonthlySummaryLogs.SingleAsync();
        Assert.Equal((orgId, "2026-10", ComplianceMonthlySummaryLog.AllCompliant, 1), (row.OrgId, row.YearMonth, row.Outcome, row.RecipientCount));
        // The report was asked for once: a month already sent costs nothing more.
        _archive.Verify(a => a.GetCoverageReportAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceReportQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ARowAlreadyThereForTheMonth_SendsNothing_AsWhenAConcurrentRunWonTheRace()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(3));
        _db.ComplianceMonthlySummaryLogs.Add(new ComplianceMonthlySummaryLog
        {
            OrgId = orgId,
            YearMonth = "2026-10",
            Outcome = ComplianceMonthlySummaryLog.ItemsMissing,
            RecipientCount = 1,
        });
        await _db.SaveChangesAsync();

        var result = await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Queued);
        Assert.Equal(1, await _db.ComplianceMonthlySummaryLogs.CountAsync());
    }

    [Fact]
    public async Task RunAsync_TheNextMonth_IsAnotherSummary()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(1));

        await Service().RunAsync(FirstOfNovember, CancellationToken.None);
        await Service().RunAsync(new DateOnly(2026, 12, 1), CancellationToken.None);

        Assert.Equal(["2026-10", "2026-11"], (await _db.ComplianceMonthlySummaryLogs.Select(l => l.YearMonth).ToListAsync()).Order().ToArray());
        Assert.Equal(2, _emails.Queued.Count);
    }

    [Theory]
    [InlineData(2026, 11, 1, "2026-10-01", "2026-10-31")]
    [InlineData(2027, 1, 1, "2026-12-01", "2026-12-31")]
    [InlineData(2027, 3, 1, "2027-02-01", "2027-02-28")]
    [InlineData(2028, 3, 1, "2028-02-01", "2028-02-29")]
    public async Task RunAsync_TheMonthIsTheCalendarMonthBeforeTheRunDate(int year, int month, int day, string from, string to)
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(1));

        await Service().RunAsync(new DateOnly(year, month, day), CancellationToken.None);

        _archive.Verify(
            a => a.GetCoverageReportAsync(
                It.Is<HostScope>(s => s.OrgId == orgId && s.IsOrgWide),
                It.Is<ComplianceReportQuery>(q => q.From == DateOnly.Parse(from) && q.To == DateOnly.Parse(to) && q.PropertyId == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── What it says ───

    [Fact]
    public async Task RunAsync_WithItemsMissing_ListsThemByActionAndNeverSaysAllCompliant()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, Report(
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.OfficialProof, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.ReceiptArchived),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.ReceiptMissing),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.ReceiptMissing),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.SendAlloggiati, ComplianceReasonCodes.DeadlineOverdue),
            Row(ComplianceItemKind.Istat, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.UploadIstatProof, ComplianceReasonCodes.PeriodOpen),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.NotApplicable, null, ComplianceReportReasons.NoShow)));

        await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        var email = Assert.Single(_emails.Queued);
        // Four stays owe a communication (the no-show owes none); four obligations are missing.
        Assert.Equal("Adempimenti di ottobre 2026: soggiorni 4, da sistemare 4", email.Content.Subject);
        var body = email.Content.HtmlBody;
        Assert.Contains("Non tutto è in regola", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tutto in regola", body.Replace("Non tutto è in regola", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ricevute Alloggiati Web da caricare: 2", body, StringComparison.Ordinal);
        Assert.Contains("Comunicazioni Alloggiati Web da inviare: 1", body, StringComparison.Ordinal);
        Assert.Contains("Comunicazioni ISTAT da provare: 1", body, StringComparison.Ordinal);
        // The count of the obligations by state: proof, held by the channel manager, no proof, not owed.
        Assert.Contains("Con prova archiviata: 1", body, StringComparison.Ordinal);
        Assert.Contains("Da verificare nel pannello del channel manager: 0", body, StringComparison.Ordinal);
        Assert.Contains("Senza prova: 4", body, StringComparison.Ordinal);
        Assert.Contains("Non dovuti (gli ospiti non sono mai arrivati): 1", body, StringComparison.Ordinal);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/short-rent/compliance\"", body, StringComparison.Ordinal);
        Assert.Equal(ComplianceMonthlySummaryLog.ItemsMissing, (await _db.ComplianceMonthlySummaryLogs.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task RunAsync_AnItemHeldOnlyByTheChannelManager_IsNeverCollapsedIntoCompliant()
    {
        var orgId = await SeedOrgAsync();
        // Every stay was entrusted to the channel manager: CasaZen holds no proof of any of them.
        ArchiveSays(orgId, Report(
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.ExternalVerificationRequired, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.EntrustedToChannelManager),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.ExternalVerificationRequired, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.EntrustedToChannelManager)));

        await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        var email = Assert.Single(_emails.Queued);
        Assert.Equal("Adempimenti di ottobre 2026: soggiorni 2, da sistemare 2", email.Content.Subject);
        Assert.Contains("Non tutto è in regola", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Da verificare nel pannello del channel manager: 2", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("CasaZen non ne vede l'esito e non lo dà per fatto", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("tutto in regola, ", email.Content.Subject, StringComparison.Ordinal);
        Assert.Equal(ComplianceMonthlySummaryLog.ItemsMissing, (await _db.ComplianceMonthlySummaryLogs.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task RunAsync_AMonthWithNoStayAndNothingOwed_SaysSoInsteadOfInventingACount()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, Report());

        await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        var email = Assert.Single(_emails.Queued);
        Assert.Equal("Adempimenti di ottobre 2026: nessun soggiorno", email.Content.Subject);
        Assert.Contains("nessun soggiorno nel mese", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(ComplianceMonthlySummaryLog.AllCompliant, (await _db.ComplianceMonthlySummaryLogs.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task RunAsync_AnOrgThatWritesInEnglish_GetsTheEnglishEmail()
    {
        var orgId = await SeedOrgAsync(language: "en");
        ArchiveSays(orgId, AllCompliantMonth(2));

        await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        var email = Assert.Single(_emails.Queued);
        Assert.Equal("Compliance for October 2026: stays 2, all compliant", email.Content.Subject);
        Assert.Contains("<html lang=\"en\">", email.Content.HtmlBody, StringComparison.Ordinal);
    }

    // ─── Privacy ───

    [Fact]
    public async Task ComplianceMonthlySummary_NoGuestPersonalData_Test()
    {
        var orgId = await SeedOrgAsync();
        // A month with stays of a guest with a name, an email, a phone and a document, whose booking codes the report knows and whose rows are open.
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = GuestEmail,
            PhoneNumber = GuestPhone,
            DocumentNumber = DocumentNumber,
        };
        _db.Guests.Add(guest);
        var property = await _db.Properties.SingleAsync(p => p.OrgId == orgId);
        _db.Bookings.Add(new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc),
            Status = BookingStatus.Confirmed,
        });
        await _db.SaveChangesAsync();
        ArchiveSays(orgId, Report(
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.ReceiptMissing, BookingCode),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.ExternalVerificationRequired, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.EntrustedToChannelManager, BookingCode),
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.OfficialProof, ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceReasonCodes.ReceiptArchived, BookingCode)));

        await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        var email = Assert.Single(_emails.Queued);
        foreach (var text in new[] { email.Content.Subject, email.Content.HtmlBody })
        {
            Assert.DoesNotContain("Anna", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Bianchi", text, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestEmail, text, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestPhone, text, StringComparison.Ordinal);
            Assert.DoesNotContain(DocumentNumber, text, StringComparison.Ordinal);
            Assert.DoesNotContain(BookingCode, text, StringComparison.Ordinal);
            // Not even the property: the email names actions and counts.
            Assert.DoesNotContain(PropertyName, text, StringComparison.Ordinal);
        }

        // What is kept is the month, the outcome and how many addresses it went to.
        var row = await _db.ComplianceMonthlySummaryLogs.SingleAsync();
        Assert.Equal("2026-10", row.YearMonth);
        Assert.Equal(ComplianceMonthlySummaryLog.ItemsMissing, row.Outcome);
    }

    [Fact]
    public void TheSummary_HoldsOnlyActionIdAndReasonOfAnOpenItem_NoNameNoCode()
    {
        var report = Report(
            Row(ComplianceItemKind.Alloggiati, ComplianceEvidenceClass.Missing, ComplianceCockpitAction.SendAlloggiati, ComplianceReasonCodes.DeadlineOverdue, BookingCode));

        var summary = ComplianceMonthSummary.From("2026-10", report);

        var item = Assert.Single(summary.OpenItems);
        Assert.Equal(ComplianceCockpitAction.SendAlloggiati, item.Action);
        Assert.Equal(report.Rows[0].BookingId, item.TargetId);
        Assert.Equal(ComplianceReasonCodes.DeadlineOverdue, item.Reason);
        // The whole of an item: no name of a guest or a property, no code of a booking.
        Assert.Equal(
            ["Action", "EvidenceClass", "Reason", "TargetId"],
            typeof(ComplianceMonthItem).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    // ─── Who ───

    [Fact]
    public async Task RunAsync_WithTheRecipientPort_GoesToTheOrganizationAndItsAdministrators()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(1));
        var resolver = new Mock<IComplianceAlertRecipientResolver>();
        resolver
            .Setup(r => r.ForOrganizationAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceAlertRecipients([OrgAddress, "admin@example.com", "ADMIN@example.com", " ", "owner@example.com"], ["auth0|admin"]));

        var result = await Service(resolver.Object).RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal([OrgAddress, "admin@example.com", "owner@example.com"], _emails.Queued.Select(e => e.To!).ToArray());
        Assert.Equal(3, (await _db.ComplianceMonthlySummaryLogs.SingleAsync()).RecipientCount);
    }

    [Fact]
    public async Task RunAsync_NobodyToSendItTo_SendsNothingAndLeavesNoRow()
    {
        var orgId = await SeedOrgAsync(contactEmail: null);
        ArchiveSays(orgId, AllCompliantMonth(1));

        var result = await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Queued);
        Assert.Empty(await _db.ComplianceMonthlySummaryLogs.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_WhenTheQueueRefusesEveryEmail_TheRowIsTakenBack_SoALaterRunTriesAgain()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(1));
        _emails.Refuse = true;

        var refused = await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(0, refused.Sent);
        Assert.Empty(await _db.ComplianceMonthlySummaryLogs.ToListAsync());

        _emails.Refuse = false;
        var later = await Service().RunAsync(new DateOnly(2026, 11, 2), CancellationToken.None);

        Assert.Equal(1, later.Sent);
        Assert.Single(_emails.Queued);
        Assert.Single(await _db.ComplianceMonthlySummaryLogs.ToListAsync());
    }

    // ─── Which organizations ───

    [Fact]
    public async Task RunAsync_OnlyActiveShortRentHostOrganizationsThatExistedInTheMonth_GetTheSummary()
    {
        var active = await SeedOrgAsync();
        var inactive = await SeedOrgAsync(active: false);
        var supplier = await SeedOrgAsync(type: OrgType.Supplier);
        var longRentOnly = await SeedOrgAsync(propertyMode: RentalMode.Long);
        var noProperty = await SeedOrgAsync(withProperty: false);
        var newcomer = await SeedOrgAsync(createdAt: new DateTime(2026, 11, 1, 0, 30, 0, DateTimeKind.Utc));
        foreach (var orgId in new[] { active, inactive, supplier, longRentOnly, noProperty, newcomer })
            ArchiveSays(orgId, AllCompliantMonth(1));

        var result = await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(new ComplianceMonthlySummaryRunResult(false, 1, 1), result);
        _archive.Verify(a => a.GetCoverageReportAsync(It.Is<HostScope>(s => s.OrgId == active), It.IsAny<ComplianceReportQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        _archive.Verify(a => a.GetCoverageReportAsync(It.Is<HostScope>(s => s.OrgId != active), It.IsAny<ComplianceReportQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AnOrgThatCannotBeProcessed_DoesNotStopTheOthers_AndTheRunFailsSoHangfireRunsItAgain()
    {
        var broken = await SeedOrgAsync();
        var fine = await SeedOrgAsync();
        _archive
            .Setup(a => a.GetCoverageReportAsync(It.Is<HostScope>(s => s.OrgId == broken), It.IsAny<ComplianceReportQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        ArchiveSays(fine, AllCompliantMonth(1));

        // The other organization was sent; the failure is not swallowed.
        var failure = await Assert.ThrowsAsync<AggregateException>(() => Service().RunAsync(FirstOfNovember, CancellationToken.None));

        Assert.Single(failure.InnerExceptions);
        Assert.Equal(fine, (await _db.ComplianceMonthlySummaryLogs.SingleAsync()).OrgId);
        Assert.Single(_emails.Queued);

        // The run that Hangfire repeats sends only what is left: the organization that was sent has its row, the one that failed is retried.
        ArchiveSays(broken, AllCompliantMonth(2));
        var second = await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(new ComplianceMonthlySummaryRunResult(false, 2, 1), second);
        Assert.Equal(2, _emails.Queued.Count);
        Assert.Equal(2, await _db.ComplianceMonthlySummaryLogs.CountAsync());
    }

    [Fact]
    public async Task RunAsync_TheModuleIsOff_DoesNothingAndWritesNothing()
    {
        var orgId = await SeedOrgAsync();
        ArchiveSays(orgId, AllCompliantMonth(1));
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(false);

        var result = await Service().RunAsync(FirstOfNovember, CancellationToken.None);

        Assert.Equal(new ComplianceMonthlySummaryRunResult(true, 0, 0), result);
        _archive.VerifyNoOtherCalls();
        Assert.Empty(_emails.Queued);
        Assert.Empty(await _db.ComplianceMonthlySummaryLogs.ToListAsync());
    }
}
