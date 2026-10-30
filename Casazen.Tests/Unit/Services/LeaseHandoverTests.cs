using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
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

/// <summary>
/// LR-OPS-1c: the minutes of the handover of the property. They are for a lease that is ending (a notice is recorded, it was ended early, or
/// it is of a type that ends by itself), never for one that simply runs on; they carry the day when it is given and leave it to complete
/// otherwise; and the state, the meters and the keys are lines to fill in by hand. "Today" in Rome is 10 October 2026.
/// </summary>
public class LeaseHandoverTests : IDisposable
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

    /// <summary>A registered lease that began on 1 November 2025 and ends on 31 October 2029, changed by <paramref name="change"/>.</summary>
    private async Task<LeaseContract> SeedAsync(Action<LeaseContract>? change = null)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var lease = await _h.SeedLeaseAsync(property, Day(2025, 11, 1), Day(2029, 10, 31));
        if (change is not null)
        {
            await using var db = _h.SecondContext();
            var tracked = await db.LeaseContracts.SingleAsync(l => l.Id == lease.Id);
            change(tracked);
            await db.SaveChangesAsync();
        }

        return lease;
    }

    [Fact]
    public async Task TheMinutes_AreForALeaseThatIsEnding_NotForOneThatRunsOn()
    {
        var running = await SeedAsync();
        var withNotice = await SeedAsync(l =>
        {
            l.NoticeOn = Day(2026, 5, 1);
            l.NoticeFrom = LeaseNoticeParty.Tenant;
        });
        var endedEarly = await SeedAsync(l => l.TerminatedOn = Day(2026, 8, 31));
        var transitory = await SeedAsync(l => l.ContractType = LeaseContractType.Transitorio);
        await using var db = _h.SecondContext();
        var service = NewService(db);

        Assert.False((await service.GetAsync(running.Id)).Handover.Available);
        Assert.Equal("lease_letter_not_applicable", (await Assert.ThrowsAsync<DomainRuleException>(() => service.BuildRiconsegnaAsync(running.Id, null))).Code);
        foreach (var lease in new[] { withNotice, endedEarly, transitory })
        {
            var handover = (await service.GetAsync(lease.Id)).Handover;
            Assert.True(handover.Available);
            Assert.Equal(new DateOnly(2029, 10, 31), handover.SuggestedOn); // the last day of the lease
            Assert.Equal(new DateOnly(2025, 11, 1), handover.MinOn);
            Assert.NotEmpty((await service.BuildRiconsegnaAsync(lease.Id, null)).Pdf);
        }
    }

    [Fact]
    public async Task TheMinutes_CarryTheDayWhenGiven_LeaveItToCompleteOtherwise_AndHaveTheLinesToFillInByHand()
    {
        var lease = await SeedAsync(l => l.NoticeOn = Day(2026, 5, 1));
        await using var db = _h.SecondContext();
        var service = NewService(db);

        var dated = BodyText((await service.BuildRiconsegnaAsync(lease.Id, new DateOnly(2029, 10, 31))).Pdf);
        var undated = BodyText((await service.BuildRiconsegnaAsync(lease.Id, null)).Pdf);

        Assert.Contains("Il giorno 31 ottobre 2029 il conduttore riconsegna", dated, StringComparison.Ordinal);
        Assert.Contains("Il giorno [da completare] il conduttore riconsegna", undated, StringComparison.Ordinal);
        foreach (var text in new[] { dated, undated })
        {
            Assert.Contains("Energia elettrica:", text, StringComparison.Ordinal);
            Assert.Contains("Chiavi", text, StringComparison.Ordinal);
            Assert.Contains("BOZZA", text, StringComparison.Ordinal); // a draft until the product owner approves the text
        }

        // Before the lease began, or more than a year after it ended, is not a handover of this lease.
        foreach (var day in new[] { new DateOnly(2025, 10, 31), new DateOnly(2030, 11, 1) })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.BuildRiconsegnaAsync(lease.Id, day));
            Assert.Equal("lease_letter_effect_date_invalid", refused.Code);
        }

        Assert.Empty(await db.LeaseEvents.ToListAsync()); // preparing the minutes writes nothing
    }

    /// <summary>The words of the body of the PDF in reading order: the rotated watermark is not interleaved with the sentences it crosses.</summary>
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
