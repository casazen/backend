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
/// LR-OPS-1c: the ISTAT update of the rent and the communication of the cedolare secca on the real service and the real drafts. The cedolare
/// secca suspends every update (nothing is calculated, nothing is written); the calculation and the letter give the same numbers; an update
/// starts from today at the earliest; a variation that is zero or negative leaves nothing to ask for. "Today" in Rome is 10 October 2026.
/// </summary>
public class LeaseIstatServiceTests : IDisposable
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

    /// <summary>A registered lease of a property that began on 1 November 2025 and ends on 31 October 2029.</summary>
    private async Task<LeaseContract> SeedAsync(LeaseTaxRegime? regime, decimal rent = 650m)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var lease = await _h.SeedLeaseAsync(property, Day(2025, 11, 1), Day(2029, 10, 31));
        await using var db = _h.SecondContext();
        var tracked = await db.LeaseContracts.SingleAsync(l => l.Id == lease.Id);
        tracked.TaxRegime = regime;
        tracked.MonthlyRent = rent;
        await db.SaveChangesAsync();
        return lease;
    }

    [Fact]
    public async Task TheCedolareSecca_SuspendsTheUpdate_NothingIsCalculatedOrWritten_ButTheOptionCanBeCommunicated()
    {
        var lease = await SeedAsync(LeaseTaxRegime.CedolareSecca);
        await using var db = _h.SecondContext();
        var service = NewService(db);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CalculateIstatAsync(lease.Id, 3.2m, 75m));
        var letterRefused = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.BuildAggiornamentoIstatAsync(lease.Id, 3.2m, 75m, new DateOnly(2026, 11, 1)));

        Assert.Equal("lease_istat_waived", refused.Code);
        Assert.Equal("lease_istat_waived", letterRefused.Code);
        Assert.Empty(await db.LeaseEvents.ToListAsync());

        // The row of the update is off and the one of the communication of the option is on, never the other way round.
        var operations = await service.GetAsync(lease.Id);
        Assert.False(operations.Istat.Available);
        Assert.True(operations.CedolareNotice.Available);

        var ordinary = await SeedAsync(LeaseTaxRegime.Ordinario);
        var ordinaryOperations = await service.GetAsync(ordinary.Id);
        Assert.True(ordinaryOperations.Istat.Available);
        Assert.False(ordinaryOperations.CedolareNotice.Available);
        Assert.Equal("lease_letter_not_applicable", (await Assert.ThrowsAsync<DomainRuleException>(() => service.BuildOpzioneCedolareAsync(ordinary.Id, null))).Code);
    }

    [Fact]
    public async Task TheCalculationAndTheLetter_GiveTheSameNumbers_ForTheSameVariationAndShare()
    {
        var lease = await SeedAsync(LeaseTaxRegime.Ordinario);
        await using var db = _h.SecondContext();
        var service = NewService(db);

        var calculation = await service.CalculateIstatAsync(lease.Id, 3.2m, 75m);
        var letter = await service.BuildAggiornamentoIstatAsync(lease.Id, 3.2m, 75m, new DateOnly(2026, 11, 1));
        var text = BodyText(letter.Pdf);

        Assert.Equal(650m, calculation.CurrentRent);
        Assert.Equal(2.4m, calculation.AppliedPercent);
        Assert.Equal(15.60m, calculation.Increase);
        Assert.Equal(665.60m, calculation.NewRent);
        Assert.Contains("euro 650,00", text, StringComparison.Ordinal);
        Assert.Contains("3,2%", text, StringComparison.Ordinal);
        Assert.Contains("75%", text, StringComparison.Ordinal);
        Assert.Contains("2,4%", text, StringComparison.Ordinal);
        Assert.Contains("euro 15,60", text, StringComparison.Ordinal);
        Assert.Contains("euro 665,60", text, StringComparison.Ordinal);
        Assert.Contains("1 novembre 2026", text, StringComparison.Ordinal);
        Assert.Contains("BOZZA", text, StringComparison.Ordinal); // a draft until the product owner approves the text
        Assert.Empty(await db.LeaseEvents.ToListAsync()); // preparing the letter writes nothing
    }

    [Fact]
    public async Task AVariationThatIsZeroOrNegative_LeavesNothingToAskFor_AndABadPercentageIsRefused()
    {
        var lease = await SeedAsync(LeaseTaxRegime.Ordinario);
        await using var db = _h.SecondContext();
        var service = NewService(db);

        foreach (var variation in new[] { 0m, -1.5m })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CalculateIstatAsync(lease.Id, variation, 75m));
            Assert.Equal("lease_istat_no_increase", refused.Code);
        }

        // An increase that rounds to nothing is no update either: 0.0001 % of 10.00 is 0.00001, which is 0.00 to the cent.
        var tiny = await SeedAsync(LeaseTaxRegime.Ordinario, rent: 10m);
        Assert.Equal("lease_istat_no_increase", (await Assert.ThrowsAsync<DomainRuleException>(() => service.CalculateIstatAsync(tiny.Id, 0.01m, 1m))).Code);

        foreach (var (variation, share) in new[] { (3.2m, 0m), (3.2m, 100.5m), (3.215m, 75m), (100.5m, 75m) })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CalculateIstatAsync(lease.Id, variation, share));
            Assert.Equal("lease_istat_percent_invalid", refused.Code);
        }
    }

    [Fact]
    public async Task AnUpdateStartsFromTodayAtTheEarliest_AndNotAfterThePeriod()
    {
        var lease = await SeedAsync(LeaseTaxRegime.Ordinario);
        await using var db = _h.SecondContext();
        var service = NewService(db);

        foreach (var day in new[] { new DateOnly(2026, 10, 9), new DateOnly(2029, 11, 1) })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.BuildAggiornamentoIstatAsync(lease.Id, 3.2m, 75m, day));
            Assert.Equal("lease_letter_effect_date_invalid", refused.Code);
        }

        // Today and the last day of the period are inside.
        Assert.NotEmpty((await service.BuildAggiornamentoIstatAsync(lease.Id, 3.2m, 75m, new DateOnly(2026, 10, 10))).Pdf);
        Assert.NotEmpty((await service.BuildAggiornamentoIstatAsync(lease.Id, 3.2m, 75m, new DateOnly(2029, 10, 31))).Pdf);
    }

    [Fact]
    public async Task TheCedolareCommunication_PrintsTheDayTheOptionAppliesFrom_OrLeavesItToComplete()
    {
        var lease = await SeedAsync(LeaseTaxRegime.CedolareSecca);
        await using var db = _h.SecondContext();
        var service = NewService(db);

        var dated = BodyText((await service.BuildOpzioneCedolareAsync(lease.Id, new DateOnly(2026, 11, 1))).Pdf);
        var undated = BodyText((await service.BuildOpzioneCedolareAsync(lease.Id, null)).Pdf);

        Assert.Contains("con effetto dal 1 novembre 2026", dated, StringComparison.Ordinal);
        Assert.Contains("il locatore rinuncia alla", dated, StringComparison.Ordinal);
        Assert.Contains("con effetto dal [da completare]", undated, StringComparison.Ordinal);
        var outside = await Assert.ThrowsAsync<DomainRuleException>(() => service.BuildOpzioneCedolareAsync(lease.Id, new DateOnly(2025, 10, 31)));
        Assert.Equal("lease_letter_effect_date_invalid", outside.Code);
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
