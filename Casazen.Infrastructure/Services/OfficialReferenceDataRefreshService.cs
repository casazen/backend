using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.OfficialData;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.OfficialData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Scheduled refresh of official public datasets (RS-6, RS-7, CO-12). Downloads the ISTAT comuni CSV, the four
/// Alloggiati tables and the tourist-tax pages of the configured pilot comuni. Imports when the official file
/// changed. Tourist-tax amounts are never invented: a page that cannot be parsed deterministically is logged only.
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

            var now = _clock.GetUtcNow().UtcDateTime;
            var rates = await db.TouristTaxRates
                .Where(r => r.IstatCode == source.IstatCode)
                .ToListAsync(cancellationToken);
            foreach (var rate in rates)
            {
                rate.SourceUrl ??= url.ToString();
                rate.SourceAuthority = string.IsNullOrWhiteSpace(source.Authority) ? rate.SourceAuthority : source.Authority;
                rate.SourceRetrievedAt = now;
                rate.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // Reporting only: HTML/PDF of a comune is not a structured tariff file. Do not invent amounts.
            results.Add(await RecordAsync(
                OfficialSourceDatasets.TouristTax,
                url.ToString(),
                source.Authority,
                OfficialSourceFetchStatus.ExtractFailed,
                "page retrieved; no deterministic structured tariff in the document (reporting only, amounts unchanged)",
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

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? TruncateOrNull(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), max);
}
