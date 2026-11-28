using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.LeaseLetters;
using Casazen.Tests.Unit.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class LeaseDepositServiceTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private LeaseOperationsService NewService(AppDbContext db)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        var catalog = new LeaseLetterCatalog(
            Options.Create(new LeaseLetterOptions { LettersDirectory = LettersDirectory() }),
            environment.Object,
            NullLogger<LeaseLetterCatalog>.Instance);
        return new LeaseOperationsService(db, catalog, new MigraDocPdfDocumentRenderer(), NullLogger<LeaseOperationsService>.Instance, _h.Clock);
    }

    private async Task<LeaseContract> SeedAsync(decimal? deposit = 1950m, bool ending = true)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var lease = await _h.SeedLeaseAsync(property, Day(2025, 11, 1), Day(2029, 10, 31));
        await using var db = _h.SecondContext();
        var tracked = await db.LeaseContracts.SingleAsync(l => l.Id == lease.Id);
        tracked.SecurityDeposit = deposit;
        if (ending)
        {
            tracked.NoticeOn = Day(2026, 4, 1);
            tracked.NoticeFrom = LeaseNoticeParty.Tenant;
        }

        await db.SaveChangesAsync();
        return lease;
    }

    private static LeaseDepositReturnInput Input(DateOnly? returnedOn = null, params LeaseDepositDeduction[] deductions) => new(
        returnedOn ?? new DateOnly(2026, 10, 10),
        null,
        [new LeaseDepositRate(2025, 2m), new LeaseDepositRate(2026, 1.6m)],
        0m,
        deductions);

    [Fact]
    public async Task TheStatementAndTheRecord_GiveTheSameSums_TheRecordWritesOneEventWithThemAndTouchesNothingElse()
    {
        var lease = await SeedAsync();
        await using var db = _h.SecondContext();
        var service = NewService(db);
        var input = Input(null, new LeaseDepositDeduction("Pulizie straordinarie", 120m));

        var statement = await service.CalculateDepositReturnAsync(lease.Id, input);
        var letter = BodyText((await service.BuildRestituzioneCauzioneAsync(lease.Id, input)).Pdf);

        Assert.Equal(30.63m, statement.Interest);
        Assert.Equal(1860.63m, statement.Returned);
        Assert.Contains("2025: tasso 2% per 61 giorni su 365: euro 6,52", letter, StringComparison.Ordinal);
        Assert.Contains("2026: tasso 1,6% per 282 giorni su 365: euro 24,11", letter, StringComparison.Ordinal);
        Assert.Contains("Pulizie straordinarie: euro 120,00", letter, StringComparison.Ordinal);
        Assert.Contains("euro 1.860,63", letter, StringComparison.Ordinal);
        Assert.Contains("BOZZA", letter, StringComparison.Ordinal);
        Assert.Empty(await db.LeaseEvents.ToListAsync());

        await service.RecordDepositReturnAsync(lease.Id, input);

        var recorded = Assert.Single(await db.LeaseEvents.AsNoTracking().ToListAsync());
        Assert.Equal(LeaseEventType.DepositReturnRecorded, recorded.EventType);
        Assert.Equal("2026-10-10:1950.00:30.63:120.00:1860.63", recorded.Payload);
        Assert.DoesNotContain("Pulizie", recorded.Payload, StringComparison.Ordinal);
        Assert.Equal(1950m, (await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == lease.Id)).SecurityDeposit);

        var operations = await service.GetAsync(lease.Id);
        Assert.True(operations.Deposit.Available);
        Assert.Equal(new LeaseDepositRecordDto(new DateOnly(2026, 10, 10), 1950m, 30.63m, 120m, 1860.63m), operations.Deposit.Recorded);
    }

    [Fact]
    public async Task ADayThatHasNotCome_AYearWithoutARate_SumsKeptAboveWhatIsOwed_NoDepositOrALeaseNotEnding_AreRefused_AndNothingIsWritten()
    {
        var lease = await SeedAsync();
        await using var db = _h.SecondContext();
        var service = NewService(db);

        var tomorrow = new DateOnly(2026, 10, 11);
        Assert.Equal(1950m, (await service.CalculateDepositReturnAsync(lease.Id, Input(tomorrow))).Deposit);
        Assert.Equal("lease_deposit_return_date_invalid", (await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordDepositReturnAsync(lease.Id, Input(tomorrow)))).Code);

        var withoutRate = new LeaseDepositReturnInput(new DateOnly(2026, 10, 10), null, [new LeaseDepositRate(2025, 2m)], 0m, []);
        var missing = await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordDepositReturnAsync(lease.Id, withoutRate));
        Assert.Equal("lease_deposit_rate_missing", missing.Code);

        var exceed = Input(null, new LeaseDepositDeduction("Danni", 5000m));
        Assert.Equal("lease_deposit_deductions_exceed", (await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordDepositReturnAsync(lease.Id, exceed))).Code);

        var noDeposit = await SeedAsync(deposit: null);
        Assert.Equal("lease_deposit_none", (await Assert.ThrowsAsync<DomainRuleException>(() => service.CalculateDepositReturnAsync(noDeposit.Id, Input()))).Code);
        var running = await SeedAsync(ending: false);
        Assert.Equal("lease_letter_not_applicable", (await Assert.ThrowsAsync<DomainRuleException>(() => service.CalculateDepositReturnAsync(running.Id, Input()))).Code);
        Assert.False((await service.GetAsync(noDeposit.Id)).Deposit.Available);
        Assert.False((await service.GetAsync(running.Id)).Deposit.Available);

        Assert.Empty(await db.LeaseEvents.ToListAsync());
    }

    private static string BodyText(byte[] pdf) => string.Join(' ', PdfTestReader.BodyWords(pdf));

    private static string LettersDirectory()
    {
        for (var directory = new DirectoryInfo(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Casazen.Web");
            if (File.Exists(Path.Combine(candidate, "Casazen.Web.csproj")))
                return Path.Combine(candidate, "LeaseLetters");
        }

        throw new DirectoryNotFoundException("Casazen.Web project not found above the test output folder.");
    }
}
