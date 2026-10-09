using System.Globalization;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OfficialData;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.OfficialData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Scheduled refresh of official public datasets (RS-6, RS-7, CO-12). Downloads the ISTAT comuni CSV, the four
/// Alloggiati tables and the tourist-tax pages of the configured pilot comuni. Imports when the official file
/// changed. Tourist-tax amounts are written only when a locazioni-brevi tariff can be parsed from the HTML table
/// or textual PDF; a failed re-read never overwrites the last good amount with null.
/// </summary>
public class OfficialReferenceDataRefreshService(
    OfficialSourceDownloader downloader,
    IComuneImportService comuni,
    IAlloggiatiCodeTableService alloggiati,
    AppDbContext db,
    IOptions<OfficialReferenceDataOptions> options,
    ILogger<OfficialReferenceDataRefreshService> logger,
    TimeProvider? timeProvider = null) : IOfficialReferenceDataRefreshService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OfficialReferenceDataRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Official reference data refresh is off (OfficialReferenceData__Enabled)");
            return new OfficialReferenceDataRefreshResult(
                new OfficialDatasetRefreshResult(OfficialSourceDatasets.IstatComuni, settings.IstatComuniCsvUrl, "disabled", null),
                [],
                []);
        }

        var istat = await RefreshIstatAsync(settings, cancellationToken);
        var alloggiatiResults = await RefreshAlloggiatiAsync(settings, cancellationToken);
        var tax = await RefreshTouristTaxAsync(settings, cancellationToken);
        return new OfficialReferenceDataRefreshResult(istat, alloggiatiResults, tax);
    }

    private async Task<OfficialDatasetRefreshResult> RefreshIstatAsync(
        OfficialReferenceDataOptions settings,
        CancellationToken cancellationToken)
    {
        if (!OfficialHostAllowlist.TryCreateAllowed(settings.IstatCatalogUrl, out var catalogUrl)
            || !OfficialHostAllowlist.TryCreateAllowed(settings.IstatComuniCsvUrl, out var csvUrl))
        {
            return await RecordAsync(
                OfficialSourceDatasets.IstatComuni,
                settings.IstatComuniCsvUrl,
                IstatCatalogPage.IstatAuthority,
                OfficialSourceFetchStatus.HostNotAllowed,
                "catalog or CSV URL is not an allowed institutional host",
                cancellationToken: cancellationToken);
        }

        var catalog = await downloader.GetAsync(catalogUrl, cancellationToken);
        if (!catalog.Succeeded || catalog.Bytes is null)
        {
            return await RecordAsync(
                OfficialSourceDatasets.IstatComuni,
                catalogUrl.ToString(),
                IstatCatalogPage.IstatAuthority,
                OfficialSourceFetchStatus.FetchFailed,
                catalog.Error ?? "catalog_download_failed",
                catalog.HttpStatus,
                catalog.Sha256,
                cancellationToken: cancellationToken);
        }

        var html = Encoding.UTF8.GetString(catalog.Bytes);
        if (!IstatCatalogPage.TryParse(html, out var snapshot))
        {
            return await RecordAsync(
                OfficialSourceDatasets.IstatComuni,
                catalogUrl.ToString(),
                IstatCatalogPage.IstatAuthority,
                OfficialSourceFetchStatus.ExtractFailed,
                "catalog_date_not_found",
                catalog.HttpStatus,
                catalog.Sha256,
                cancellationToken: cancellationToken);
        }

        if (snapshot.PermalinkUrl is { } permalink
            && OfficialHostAllowlist.TryCreateAllowed(permalink, out var permalinkUri)
            && permalinkUri.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            csvUrl = permalinkUri;

        var file = await downloader.GetAsync(csvUrl, cancellationToken);
        if (!file.Succeeded || file.Bytes is null)
        {
            return await RecordAsync(
                OfficialSourceDatasets.IstatComuni,
                csvUrl.ToString(),
                IstatCatalogPage.IstatAuthority,
                OfficialSourceFetchStatus.FetchFailed,
                file.Error ?? "csv_download_failed",
                file.HttpStatus,
                file.Sha256,
                cancellationToken: cancellationToken);
        }

        var lastSha = await db.ComuneImports.AsNoTracking()
            .OrderByDescending(i => i.ImportedAt)
            .Select(i => i.Sha256)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastSha is not null && lastSha == file.Sha256)
        {
            return await RecordAsync(
                OfficialSourceDatasets.IstatComuni,
                csvUrl.ToString(),
                IstatCatalogPage.IstatAuthority,
                OfficialSourceFetchStatus.Unchanged,
                $"sha256 {file.Sha256}",
                file.HttpStatus,
                file.Sha256,
                cancellationToken: cancellationToken);
        }

        var retrievedAt = _clock.GetUtcNow().UtcDateTime;
        await using var stream = new MemoryStream(file.Bytes, writable: false);
        var imported = await comuni.ImportAsync(
            new ComuneImportRequest(
                "Elenco-comuni-italiani.csv",
                $"ISTAT, Elenco dei comuni italiani, aggiornato al {snapshot.ReferenceDate:dd/MM/yyyy}",
                snapshot.ReferenceDate,
                "system",
                ComuneImportOrigin.ScheduledDownload,
                SourceUrl: csvUrl.ToString(),
                Authority: IstatCatalogPage.IstatAuthority,
                RetrievedAt: retrievedAt),
            stream,
            cancellationToken);

        if (!imported.Success)
        {
            return await RecordAsync(
                OfficialSourceDatasets.IstatComuni,
                csvUrl.ToString(),
                IstatCatalogPage.IstatAuthority,
                OfficialSourceFetchStatus.Rejected,
                imported.Errors.Count == 0 ? "import_rejected" : imported.Errors[0].Error,
                file.HttpStatus,
                file.Sha256,
                cancellationToken: cancellationToken);
        }

        return await RecordAsync(
            OfficialSourceDatasets.IstatComuni,
            csvUrl.ToString(),
            IstatCatalogPage.IstatAuthority,
            OfficialSourceFetchStatus.Imported,
            $"{imported.Rows} rows, {imported.Inserted} new, {imported.Updated} changed",
            file.HttpStatus,
            file.Sha256,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<OfficialDatasetRefreshResult>> RefreshAlloggiatiAsync(
        OfficialReferenceDataOptions settings,
        CancellationToken cancellationToken)
    {
        var results = new List<OfficialDatasetRefreshResult>();
        var retrieved = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");
        foreach (var table in Enum.GetValues<AlloggiatiCodeTable>())
        {
            var dataset = table switch
            {
                AlloggiatiCodeTable.Comuni => OfficialSourceDatasets.AlloggiatiComuni,
                AlloggiatiCodeTable.Stati => OfficialSourceDatasets.AlloggiatiStati,
                AlloggiatiCodeTable.Documenti => OfficialSourceDatasets.AlloggiatiDocumenti,
                AlloggiatiCodeTable.TipiAlloggiato => OfficialSourceDatasets.AlloggiatiTipiAlloggiato,
                _ => "alloggiati",
            };
            var urlText = settings.AlloggiatiDownloadUrl(table);
            if (!OfficialHostAllowlist.TryCreateAllowed(urlText, out var url))
            {
                results.Add(await RecordAsync(
                    dataset, urlText, AlloggiatiCodeTableService.PoliziaDiStatoAuthority,
                    OfficialSourceFetchStatus.HostNotAllowed, "download URL is not an allowed host",
                    cancellationToken: cancellationToken));
                continue;
            }

            var file = await downloader.GetAsync(url, cancellationToken);
            if (!file.Succeeded || file.Bytes is null)
            {
                results.Add(await RecordAsync(
                    dataset, url.ToString(), AlloggiatiCodeTableService.PoliziaDiStatoAuthority,
                    OfficialSourceFetchStatus.FetchFailed, file.Error ?? "download_failed", file.HttpStatus, file.Sha256,
                    cancellationToken: cancellationToken));
                continue;
            }

            await using var stream = new MemoryStream(file.Bytes, writable: false);
            var imported = await alloggiati.ImportAsync(
                table,
                stream,
                fileName: url.Query.Contains("N=", StringComparison.OrdinalIgnoreCase)
                    ? url.Query.Split("N=").Last() + ".csv"
                    : $"{table}.csv",
                sourceVersion: $"Polizia di Stato, Portale Alloggiati, scaricato il {retrieved}",
                importedBy: "system",
                cancellationToken,
                url.ToString(),
                AlloggiatiCodeTableService.PoliziaDiStatoAuthority,
                skipIfUnchanged: true);

            if (!imported.Success)
            {
                results.Add(await RecordAsync(
                    dataset, url.ToString(), AlloggiatiCodeTableService.PoliziaDiStatoAuthority,
                    OfficialSourceFetchStatus.Rejected,
                    imported.Errors.Count == 0 ? "import_rejected" : imported.Errors[0].Error,
                    file.HttpStatus, file.Sha256, cancellationToken: cancellationToken));
                continue;
            }

            results.Add(await RecordAsync(
                dataset, url.ToString(), AlloggiatiCodeTableService.PoliziaDiStatoAuthority,
                imported.Unchanged ? OfficialSourceFetchStatus.Unchanged : OfficialSourceFetchStatus.Imported,
                imported.Unchanged ? $"sha256 {file.Sha256}" : $"{imported.RowCount} codes",
                file.HttpStatus, file.Sha256, cancellationToken: cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<OfficialDatasetRefreshResult>> RefreshTouristTaxAsync(
        OfficialReferenceDataOptions settings,
        CancellationToken cancellationToken)
    {
        var results = new List<OfficialDatasetRefreshResult>();
        foreach (var source in settings.TouristTaxSources)
        {
            if (string.IsNullOrWhiteSpace(source.IstatCode) || string.IsNullOrWhiteSpace(source.SourceUrl))
                continue;

            if (!OfficialHostAllowlist.TryCreateAllowed(source.SourceUrl, out var url))
            {
                results.Add(await RecordAsync(
                    OfficialSourceDatasets.TouristTax, source.SourceUrl, source.Authority,
                    OfficialSourceFetchStatus.HostNotAllowed, "URL is not an allowed institutional host",
                    istatCode: source.IstatCode, cancellationToken: cancellationToken));
                continue;
            }

            var file = await downloader.GetAsync(url, cancellationToken);
            if (!file.Succeeded || file.Bytes is null)
            {
                results.Add(await RecordAsync(
                    OfficialSourceDatasets.TouristTax, url.ToString(), source.Authority,
                    OfficialSourceFetchStatus.FetchFailed, file.Error ?? "download_failed", file.HttpStatus, file.Sha256,
                    source.IstatCode, cancellationToken));
                continue;
            }

            var extraction = TouristTaxOfficialExtractor.TryExtract(source.IstatCode, file.Bytes);
            if (!extraction.Succeeded || extraction.Rates.Count == 0)
            {
                // Keep the last good amount. Do not stamp SourceRetrievedAt as if the tariff was re-read.
                results.Add(await RecordAsync(
                    OfficialSourceDatasets.TouristTax,
                    url.ToString(),
                    source.Authority,
                    OfficialSourceFetchStatus.ExtractFailed,
                    extraction.Detail,
                    file.HttpStatus,
                    file.Sha256,
                    source.IstatCode,
                    cancellationToken));
                continue;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var rates = await db.TouristTaxRates
                .Where(r => r.IstatCode == source.IstatCode)
                .ToListAsync(cancellationToken);
            ApplyExtractedRates(rates, extraction.Rates, url.ToString(), source, now);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            results.Add(await RecordAsync(
                OfficialSourceDatasets.TouristTax,
                url.ToString(),
                source.Authority,
                OfficialSourceFetchStatus.Imported,
                Truncate(extraction.Detail, 500),
                file.HttpStatus,
                file.Sha256,
                source.IstatCode,
                cancellationToken));
        }

        return results;
    }

    private async Task<OfficialDatasetRefreshResult> RecordAsync(
        string dataset,
        string sourceUrl,
        string authority,
        string status,
        string? detail,
        int? httpStatus = null,
        string? sha256 = null,
        string? istatCode = null,
        CancellationToken cancellationToken = default)
    {
        db.OfficialSourceFetches.Add(new OfficialSourceFetch
        {
            Dataset = Truncate(dataset, 40),
            SourceUrl = Truncate(sourceUrl, 500),
            Authority = Truncate(string.IsNullOrWhiteSpace(authority) ? "unknown" : authority, 200),
            RetrievedAt = _clock.GetUtcNow().UtcDateTime,
            Sha256 = sha256,
            HttpStatus = httpStatus,
            Status = Truncate(status, 40),
            Detail = TruncateOrNull(detail, 500),
            IstatCode = istatCode,
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        logger.LogInformation(
            "Official source {Dataset} {Status}: {Url} ({Detail})",
            dataset, status, sourceUrl, detail);
        return new OfficialDatasetRefreshResult(dataset, sourceUrl, status, detail, httpStatus, sha256);
    }

    private void ApplyExtractedRates(
        List<TouristTaxRate> existing,
        IReadOnlyList<ExtractedTouristTaxRate> extracted,
        string sourceUrl,
        OfficialTouristTaxSourceOptions source,
        DateTime now)
    {
        foreach (var item in extracted)
        {
            var row = existing.FirstOrDefault(r => SameTariffRow(r, item));
            if (row is null)
            {
                row = new TouristTaxRate
                {
                    Id = StableId(item),
                    City = item.City,
                    IstatCode = item.IstatCode,
                    RegionCode = item.RegionCode,
                    AccommodationCategory = item.AccommodationCategory,
                    SeasonStart = item.SeasonStart,
                    SeasonEnd = item.SeasonEnd,
                    MinimumAge = 14,
                    IsActive = true,
                    EffectiveFrom = DateTime.SpecifyKind(item.EffectiveFrom, DateTimeKind.Utc),
                    CreatedAt = now,
                };
                db.TouristTaxRates.Add(row);
                existing.Add(row);
            }

            row.CalculationMethod = item.CalculationMethod;
            row.RatePerPersonPerNight = item.RatePerPersonPerNight;
            row.PercentOfNightlyPrice = item.PercentOfNightlyPrice;
            row.CapPerPersonPerNight = item.CapPerPersonPerNight;
            if (item.MaxNights is int maxNights)
                row.MaxNights = maxNights;
            if (item.ReducedRateMaxAge is int reducedAge)
                row.ReducedRateMaxAge = reducedAge;
            if (item.ReducedRatePerPersonPerNight is decimal reduced)
                row.ReducedRatePerPersonPerNight = reduced;
            row.SourceUrl = sourceUrl;
            row.SourceAuthority = string.IsNullOrWhiteSpace(source.Authority) ? row.SourceAuthority : source.Authority;
            row.SourceRetrievedAt = now;
            row.VerificationLevel = TouristTaxRateVerification.Official;
            row.UpdatedAt = now;
            if (string.IsNullOrWhiteSpace(row.Notes))
            {
                row.Notes = Truncate(
                    $"{item.City}: extracted {item.RatePerPersonPerNight.ToString("0.00", CultureInfo.InvariantCulture)} from {sourceUrl}",
                    500);
            }
        }
    }

    private static bool SameTariffRow(TouristTaxRate row, ExtractedTouristTaxRate extracted) =>
        string.Equals(row.IstatCode, extracted.IstatCode, StringComparison.Ordinal)
        && string.Equals(row.SeasonStart, extracted.SeasonStart, StringComparison.Ordinal)
        && SameCategory(row.AccommodationCategory, extracted.AccommodationCategory);

    private static bool SameCategory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return NormalizeCategory(left) == NormalizeCategory(right);
    }

    private static string NormalizeCategory(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Guid StableId(ExtractedTouristTaxRate item) =>
        item.AccommodationCategory is null && item.SeasonStart is null
            ? TouristTaxRateSeed.IdFor(item.IstatCode, item.EffectiveFrom)
            : TouristTaxRateSeed.IdFor(item.IstatCode, item.EffectiveFrom, item.AccommodationCategory, item.SeasonStart);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? TruncateOrNull(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), max);
}
