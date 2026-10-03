using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit;

/// <summary>
/// The official ISTAT comuni list for tests (SU-04). <c>Fixtures/Comuni/comuni-istat-sample.csv</c> holds 29 rows copied
/// verbatim from the official file (<c>Data/Seeds/comuni-istat.csv</c>, ISTAT, 21/02/2026): the comuni the tests need, among
/// them the ones the old hard-coded registry got wrong (Torino 001272, Bellagio 013250, Menaggio 013145, Varenna 097084 in
/// Lecco), pairs of comuni with the same name in different provinces (Castro, Livo, Samone, Peglio) and a bilingual one
/// (Bolzano/Bozen). It is imported through the real import, never inserted by hand.
/// </summary>
public static class ComuneTestData
{
    /// <summary>Reference date of the official file the sample comes from.</summary>
    public static readonly DateOnly ReferenceDate = new(2026, 2, 21);

    public const string SampleSourceVersion = "Test sample: rows of the ISTAT list updated to 21/02/2026";

    // The comuni of the sample the tests refer to by name.
    public const string Roma = "058091";
    public const string Milano = "015146";
    public const string Firenze = "048017";
    public const string Torino = "001272";
    public const string Genova = "010025";
    public const string Como = "013075";
    public const string Bellagio = "013250";
    public const string Menaggio = "013145";
    public const string Varenna = "097084";
    public const string Napoli = "063049";
    public const string Venezia = "027042";
    public const string Bologna = "037006";
    public const string Palermo = "082053";
    public const string Bolzano = "021008";
    public const string CastroBergamo = "016065";
    public const string CastroLecce = "075096";

    // The SEO view of four comuni of the sample, for tests that build a service by hand (codes of the official list).
    public static readonly ComuneInfo ComoInfo = new("013075", "Como", "LOM", "lombardia", "como");
    public static readonly ComuneInfo BellagioInfo = new("013250", "Bellagio", "LOM", "lombardia", "bellagio");
    public static readonly ComuneInfo MenaggioInfo = new("013145", "Menaggio", "LOM", "lombardia", "menaggio");
    public static readonly ComuneInfo PalermoInfo = new("082053", "Palermo", "SIC", "sicilia", "palermo");

    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Comuni", "comuni-istat-sample.csv");

    public static Stream OpenSample() => File.OpenRead(SamplePath);

    public static ComuneImportService CreateImportService(AppDbContext db, TimeProvider? clock = null) =>
        new(db, NullLogger<ComuneImportService>.Instance, clock);

    /// <summary>Imports the sample as a full list, like an admin would.</summary>
    public static async Task<ComuneImportResult> ImportSampleAsync(AppDbContext db)
    {
        await using var stream = OpenSample();
        var result = await CreateImportService(db).ImportAsync(
            new ComuneImportRequest("comuni-istat-sample.csv", SampleSourceVersion, ReferenceDate, "test"),
            stream);
        if (!result.Success)
            throw new InvalidOperationException("The sample of the ISTAT list was rejected: " + result.Errors[0]);

        db.ChangeTracker.Clear();
        return result;
    }
}

/// <summary>The services built on the comuni list, on a test database (SU-04), for the tests that build a service by hand.</summary>
public static class ComuneTestServices
{
    public static IComuneDirectory Directory(AppDbContext db) => new ComuneDirectory(db);

    public static ISupplierComuneMatcher Matcher(AppDbContext db) => new SupplierComuneMatcher(new ComuneDirectory(db));

    public static ISupplierPilotComuni Pilots(AppDbContext db, SupplierRegistrationOptions? options = null) =>
        new SupplierPilotComuni(Options.Create(options ?? new SupplierRegistrationOptions()), new ComuneDirectory(db));

    public static ISeoComuneCatalog SeoCatalog(AppDbContext db) => new SeoComuneCatalog(new ComuneDirectory(db));
}

/// <summary>A fixed SEO catalog, for tests of a service that only needs the comuni it names.</summary>
public sealed class StaticSeoComuneCatalog(params ComuneInfo[] comuni) : ISeoComuneCatalog
{
    public Task<ComuneInfo?> GetByCodeAsync(string? istatCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(comuni.FirstOrDefault(c => c.Code == istatCode));

    public Task<IReadOnlyDictionary<string, ComuneInfo>> GetByCodesAsync(IEnumerable<string> istatCodes, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ComuneInfo>>(comuni.Where(c => istatCodes.Contains(c.Code)).ToDictionary(c => c.Code));

    public Task<ComuneInfo?> GetPilotBySlugAsync(string? comuneSlug, CancellationToken cancellationToken = default) =>
        Task.FromResult(comuni.FirstOrDefault(c => c.ComuneSlug == comuneSlug));

    public Task<ComuneInfo?> GetPilotByRegionAndComuneSlugAsync(string? regionSlug, string? comuneSlug, CancellationToken cancellationToken = default) =>
        Task.FromResult(comuni.FirstOrDefault(c => c.RegionSlug == regionSlug && c.ComuneSlug == comuneSlug));

    public Task<IReadOnlyList<ComuneInfo>> GetPilotsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ComuneInfo>>(comuni);

    public Task<ComuneInfo?> ResolveSlugOrCodeAsync(string? value, CancellationToken cancellationToken = default) =>
        Task.FromResult(comuni.FirstOrDefault(c => c.Code == value || c.ComuneSlug == value));
}
