using System.Globalization;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OfficialData;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.OfficialData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// MEF <c>nuova_at</c> profile of a comune: download the official CSV and PDF, extract deterministically then with
/// DeepSeek, version the result and project onto <c>TouristTaxRates</c> by insert + period close. Never invents amounts.
/// </summary>
public class ComuneOfficialProfileService(
    AppDbContext db,
    IComuneDirectory comuni,
    OfficialSourceDownloader downloader,
    IAiProvider ai,
    IOptions<OfficialReferenceDataOptions> options,
    ILogger<ComuneOfficialProfileService> logger,
    TimeProvider? timeProvider = null) : IComuneOfficialProfileService
{
    public const int MaxActChars = ComuneOfficialExtractPrompt.MaxActChars;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly OfficialReferenceDataOptions _settings = options.Value;
    private MefNuovaAtIndex.Snapshot? _indexCache;

    public async Task<ComuneOfficialEnsureResult> EnsureAsync(string istatCode, CancellationToken cancellationToken = default)
        => await EnsureCoreAsync(istatCode, skipRecentReuse: false, cancellationToken);

    public async Task<ComuneOfficialRefreshResult> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var codes = await db.ComuneOfficialProfiles.AsNoTracking()
            .Select(p => p.IstatCode)
            .ToListAsync(cancellationToken);
        if (codes.Count == 0)
            return new ComuneOfficialRefreshResult(OfficialSourceFetchStatus.Unchanged, 0, 0, 0, "no_profiles");

        var lastIndex = await LatestIndexFetchAsync(cancellationToken);
        var index = await LoadIndexAsync(cancellationToken);
        if (index is null)
            return new ComuneOfficialRefreshResult(OfficialSourceFetchStatus.FetchFailed, codes.Count, 0, 0, "index_unavailable");

        if (lastIndex is { Sha256: not null }
            && string.Equals(lastIndex.Sha256, index.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var marked = 0;
            foreach (var code in codes)
            {
                await RecordFetchAsync(
                    OfficialSourceDatasets.MefImpostaSoggiorno,
                    index.SourceUrl,
                    OfficialSourceFetchStatus.Unchanged,
                    "csv_unchanged",
                    istatCode: code,
                    sha256: index.Sha256,
                    cancellationToken: cancellationToken);
                await TouchProfileAsync(code, cancellationToken);
                marked++;
            }

            await RecordFetchAsync(
                OfficialSourceDatasets.MefNuovaAtIndex,
                index.SourceUrl,
                OfficialSourceFetchStatus.Unchanged,
                $"profiles={marked}",
                sha256: index.Sha256,
                cancellationToken: cancellationToken);
            return new ComuneOfficialRefreshResult(OfficialSourceFetchStatus.Unchanged, codes.Count, 0, marked, "csv_unchanged");
        }

        await RecordFetchAsync(
            OfficialSourceDatasets.MefNuovaAtIndex,
            index.SourceUrl,
            OfficialSourceFetchStatus.Imported,
            $"rows={index.Rows.Count}",
            sha256: index.Sha256,
            cancellationToken: cancellationToken);

        var added = 0;
        var unchanged = 0;
        foreach (var code in codes)
        {
            var result = await EnsureCoreAsync(code, skipRecentReuse: true, cancellationToken);
            if (result.Reused || result.Status == ComuneOfficialProfileStatus.Unchanged)
                unchanged++;
            else
                added++;
        }

        return new ComuneOfficialRefreshResult(OfficialSourceFetchStatus.Imported, codes.Count, added, unchanged, null);
    }

    private async Task<ComuneOfficialEnsureResult> EnsureCoreAsync(
        string istatCode,
        bool skipRecentReuse,
        CancellationToken cancellationToken)
    {
        var code = ComuneRules.NormalizeIstatCode(istatCode);
        if (code is null)
        {
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, _settings.MefNuovaAtCsvUrl,
                OfficialSourceFetchStatus.Rejected, "istat_invalid", cancellationToken: cancellationToken);
            return new ComuneOfficialEnsureResult(istatCode, ComuneOfficialProfileStatus.Rejected, false, false, "istat_invalid");
        }

        var comune = await comuni.FindByIstatCodeAsync(code, activeOnly: false, cancellationToken);
        if (comune is null)
        {
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, _settings.MefNuovaAtCsvUrl,
                OfficialSourceFetchStatus.Rejected, "comune_unknown", istatCode: code, cancellationToken: cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Rejected, false, false, "comune_unknown");
        }

        if (!skipRecentReuse && await TryReuseRecentAsync(code, cancellationToken) is { } reused)
            return reused;

        var index = _indexCache ?? await LoadIndexAsync(cancellationToken);
        if (index is null)
        {
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Unreadable, null, null, null, null, null, null,
                "index_unavailable", projectRates: false, cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Unreadable, false, false, "index_unavailable");
        }

        await RecordIndexImportedIfNeededAsync(index, cancellationToken);

        var match = MefNuovaAtIndex.Match(index, comune);
        if (!match.Succeeded)
        {
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Rejected, null, null, index.SourceUrl, null, null, null,
                match.Rejection, projectRates: false, cancellationToken);
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, index.SourceUrl,
                OfficialSourceFetchStatus.Rejected, match.Rejection, istatCode: code, sha256: index.Sha256,
                cancellationToken: cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Rejected, false, false, match.Rejection);
        }

        if (await TryReuseSameActAsync(code, match.ActSha256, cancellationToken) is { } sameAct)
            return sameAct;

        if (!OfficialHostAllowlist.TryCreateAllowed(match.PdfUrl, out var pdfUri))
        {
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Rejected, match, null, index.SourceUrl, null, null, null,
                "host_not_allowed", projectRates: false, cancellationToken);
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, match.PdfUrl,
                OfficialSourceFetchStatus.HostNotAllowed, "host_not_allowed", istatCode: code,
                sha256: match.ActSha256, cancellationToken: cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Rejected, false, false, "host_not_allowed");
        }

        var pdf = await downloader.GetAsync(pdfUri, cancellationToken);
        if (!pdf.Succeeded || pdf.Bytes is null)
        {
            var status = pdf.Error == "host_not_allowed"
                ? OfficialSourceFetchStatus.HostNotAllowed
                : OfficialSourceFetchStatus.FetchFailed;
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Unreadable, match, pdfUri.ToString(), index.SourceUrl,
                null, pdf.Sha256, null, pdf.Error ?? "fetch_failed", projectRates: false, cancellationToken);
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, pdfUri.ToString(), status, pdf.Error,
                pdf.HttpStatus, pdf.Sha256, code, cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Unreadable, false, false, pdf.Error);
        }

        string text;
        try
        {
            text = TouristTaxOfficialExtractor.ReadDocumentText(pdf.Bytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PDF of comune {IstatCode} is not readable", code);
            text = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Unreadable, match, pdfUri.ToString(), index.SourceUrl,
                null, pdf.Sha256, null, "ocr_unavailable", projectRates: false, cancellationToken);
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, pdfUri.ToString(), OfficialSourceFetchStatus.ExtractFailed,
                "ocr_unavailable", pdf.HttpStatus, pdf.Sha256, code, cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Unreadable, false, false, "ocr_unavailable");
        }

        var deterministic = TouristTaxOfficialExtractor.TryExtractFromText(code, text);
        if (deterministic.Succeeded && deterministic.Rates.Count > 0)
        {
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Extracted, match, pdfUri.ToString(), index.SourceUrl,
                FromDeterministic(comune, match, pdfUri.ToString(), deterministic), pdf.Sha256, null,
                deterministic.Detail, projectRates: true, cancellationToken, deterministic.Rates);
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, pdfUri.ToString(), OfficialSourceFetchStatus.Imported,
                deterministic.Detail, pdf.HttpStatus, pdf.Sha256, code, cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Extracted, false, true, deterministic.Detail);
        }

        var aiResult = await TryExtractWithAiAsync(comune, index.SourceUrl, pdfUri.ToString(), match, text, cancellationToken);
        if (aiResult is { Accepted: true, Extract: { } extract }
            && string.Equals(extract.Status, ComuneOfficialExtractStatus.Extracted, StringComparison.OrdinalIgnoreCase))
        {
            await PersistVersionAsync(comune, ComuneOfficialProfileStatus.Extracted, match, pdfUri.ToString(), index.SourceUrl,
                extract, pdf.Sha256, ComuneOfficialExtractPrompt.Version, "ai_extracted", projectRates: true, cancellationToken);
            await RecordFetchAsync(
                OfficialSourceDatasets.MefImpostaSoggiorno, pdfUri.ToString(), OfficialSourceFetchStatus.Imported,
                "ai_extracted", pdf.HttpStatus, pdf.Sha256, code, cancellationToken);
            return new ComuneOfficialEnsureResult(code, ComuneOfficialProfileStatus.Extracted, false, true, "ai_extracted");
        }

        var reason = aiResult?.Rejection ?? "extract_failed";
        var versionStatus = string.Equals(aiResult?.Extract?.Status, ComuneOfficialExtractStatus.Unreadable, StringComparison.OrdinalIgnoreCase)
            ? ComuneOfficialProfileStatus.Unreadable
            : ComuneOfficialProfileStatus.Rejected;
        await PersistVersionAsync(comune, versionStatus, match, pdfUri.ToString(), index.SourceUrl,
            aiResult?.Extract, pdf.Sha256, ComuneOfficialExtractPrompt.Version, reason, projectRates: false, cancellationToken);
        await RecordFetchAsync(
            OfficialSourceDatasets.MefImpostaSoggiorno, pdfUri.ToString(),
            versionStatus == ComuneOfficialProfileStatus.Unreadable
                ? OfficialSourceFetchStatus.ExtractFailed
                : OfficialSourceFetchStatus.Rejected,
            reason, pdf.HttpStatus, pdf.Sha256, code, cancellationToken);
        return new ComuneOfficialEnsureResult(code, versionStatus, false, false, reason);
    }

    private async Task<ComuneOfficialEnsureResult?> TryReuseRecentAsync(string istatCode, CancellationToken cancellationToken)
    {
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.ComuneOfficialProfile, istatCode));
        var current = await CurrentVersionAsync(istatCode, cancellationToken);
        if (current is null)
            return null;

        var reuseUntil = _clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, _settings.ComuneProfileReuseDays));
        var reusable = current.RetrievedAt >= reuseUntil
            && current.Status is ComuneOfficialProfileStatus.Extracted
                or ComuneOfficialProfileStatus.Unreadable
                or ComuneOfficialProfileStatus.Unchanged;
        if (!reusable)
            return null;

        await TouchProfileLockedAsync(istatCode, cancellationToken);
        if (tx is not null)
            await tx.CommitAsync(cancellationToken);
        return new ComuneOfficialEnsureResult(istatCode, current.Status, true, false, "reused_recent");
    }

    private async Task<ComuneOfficialEnsureResult?> TryReuseSameActAsync(
        string istatCode, string actSha256, CancellationToken cancellationToken)
    {
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.ComuneOfficialProfile, istatCode));
        var current = await CurrentVersionAsync(istatCode, cancellationToken);
        if (current is null
            || !string.Equals(current.ActSha256, actSha256, StringComparison.OrdinalIgnoreCase))
            return null;

        await TouchProfileLockedAsync(istatCode, cancellationToken);
        if (tx is not null)
            await tx.CommitAsync(cancellationToken);
        await RecordFetchAsync(
            OfficialSourceDatasets.MefImpostaSoggiorno, current.SourceUrl ?? _settings.MefNuovaAtCsvUrl,
            OfficialSourceFetchStatus.Unchanged, "act_unchanged", sha256: actSha256, istatCode: istatCode,
            cancellationToken: cancellationToken);
        return new ComuneOfficialEnsureResult(istatCode, ComuneOfficialProfileStatus.Unchanged, true, false, "act_unchanged");
    }

    private async Task<MefNuovaAtIndex.Snapshot?> LoadIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexCache is not null)
            return _indexCache;

        var csvUrl = await ResolveCsvUrlAsync(cancellationToken);
        if (csvUrl is null)
            return null;

        var file = await downloader.GetAsync(csvUrl, cancellationToken);
        if (!file.Succeeded || file.Bytes is null || file.Sha256 is null)
        {
            await RecordFetchAsync(
                OfficialSourceDatasets.MefNuovaAtIndex, csvUrl.ToString(),
                file.Error == "host_not_allowed" ? OfficialSourceFetchStatus.HostNotAllowed : OfficialSourceFetchStatus.FetchFailed,
                file.Error, file.HttpStatus, file.Sha256, cancellationToken: cancellationToken);
            return null;
        }

        _indexCache = MefNuovaAtIndex.Parse(file.Bytes, file.Sha256, csvUrl.ToString());
        return _indexCache;
    }

    private async Task<Uri?> ResolveCsvUrlAsync(CancellationToken cancellationToken)
    {
        if (OfficialHostAllowlist.TryCreateAllowed(_settings.MefNuovaAtCatalogUrl, out var catalog))
        {
            var page = await downloader.GetAsync(catalog, cancellationToken);
            if (page.Succeeded && page.Bytes is not null)
            {
                var html = Encoding.UTF8.GetString(page.Bytes);
                if (MefNuovaAtCatalogPage.TryParse(html, out var discovered))
                    return discovered;
            }
        }

        if (OfficialHostAllowlist.TryCreateAllowed(_settings.MefNuovaAtCsvUrl, out var configured))
            return configured;

        await RecordFetchAsync(
            OfficialSourceDatasets.MefNuovaAtIndex, _settings.MefNuovaAtCsvUrl,
            OfficialSourceFetchStatus.HostNotAllowed, "host_not_allowed", cancellationToken: cancellationToken);
        return null;
    }

    private async Task<ComuneOfficialExtractValidator.Result?> TryExtractWithAiAsync(
        Comune comune,
        string indexUrl,
        string pdfUrl,
        MefNuovaAtIndex.MatchResult match,
        string actText,
        CancellationToken cancellationToken)
    {
        var scrubbed = PiiScrubber.Scrub(actText);
        var prompt = ComuneOfficialExtractPrompt.Build(comune, indexUrl, pdfUrl, match.ActId, match.MefPublishedOn, scrubbed);
        prompt = PiiScrubber.Scrub(prompt);
        var cacheKey = ComuneOfficialExtractPrompt.CacheKey(comune.IstatCode, match.ActSha256);
        AiGenerationResult generated;
        try
        {
            generated = await ai.GenerateAsync(prompt, AiModelTier.Economy, cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI extract failed for comune {IstatCode}", comune.IstatCode);
            return new ComuneOfficialExtractValidator.Result(false, "ai_failed", null);
        }

        if (!generated.ProviderConfigured || string.IsNullOrWhiteSpace(generated.Content))
            return new ComuneOfficialExtractValidator.Result(false, "ai_not_configured", null);

        return ComuneOfficialExtractValidator.Validate(generated.Content, pdfUrl);
    }

    private async Task PersistVersionAsync(
        Comune comune,
        string status,
        MefNuovaAtIndex.MatchResult? match,
        string? sourceUrl,
        string? indexUrl,
        ComuneOfficialExtractDto? extract,
        string? pdfSha256,
        string? promptVersion,
        string? detail,
        bool projectRates,
        CancellationToken cancellationToken,
        IReadOnlyList<ExtractedTouristTaxRate>? deterministicRates = null)
    {
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.ComuneOfficialProfile, comune.IstatCode));

        var now = _clock.GetUtcNow().UtcDateTime;
        var profile = await db.ComuneOfficialProfiles
            .FirstOrDefaultAsync(p => p.IstatCode == comune.IstatCode, cancellationToken);
        if (profile is null)
        {
            profile = new ComuneOfficialProfile
            {
                IstatCode = comune.IstatCode,
                CreatedAt = now,
            };
            db.ComuneOfficialProfiles.Add(profile);
        }

        var previous = await db.ComuneOfficialProfileVersions
            .Where(v => v.IstatCode == comune.IstatCode && v.IsCurrent)
            .ToListAsync(cancellationToken);

        if (match is { Succeeded: true }
            && previous.Exists(v => string.Equals(v.ActSha256, match.ActSha256, StringComparison.OrdinalIgnoreCase)))
        {
            profile.LastEnsuredAt = now;
            profile.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (tx is not null)
                await tx.CommitAsync(cancellationToken);
            return;
        }

        var published = match?.MefPublishedOn
            ?? ParseIso(extract?.MefPublishedOn)
            ?? DateOnly.FromDateTime(now);
        var validFrom = match?.MefPublishedOn is { } mefDate
            ? MefPublicationCalendar.ValidFrom(mefDate)
            : published;
        if (extract?.ValidFrom is { } claimed
            && DateOnly.TryParseExact(claimed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed != validFrom)
        {
            logger.LogInformation(
                "MEF validFrom override for {IstatCode}: JSON {Json} rule {Rule}",
                comune.IstatCode, parsed, validFrom);
        }

        var nextNumber = await db.ComuneOfficialProfileVersions
            .Where(v => v.IstatCode == comune.IstatCode)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken) ?? 0;
        nextNumber++;

        foreach (var old in previous)
        {
            old.IsCurrent = false;
            old.ValidTo = validFrom.AddDays(-1);
        }

        if (previous.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        var version = new ComuneOfficialProfileVersion
        {
            Id = Guid.NewGuid(),
            IstatCode = comune.IstatCode,
            VersionNumber = nextNumber,
            IsCurrent = true,
            Status = status,
            ActId = Truncate(match?.ActId ?? extract?.ActId, 200),
            ActSha256 = match?.ActSha256,
            PdfSha256 = pdfSha256,
            SourceUrl = Truncate(sourceUrl ?? extract?.SourceUrl, 500),
            IndexUrl = Truncate(indexUrl, 500),
            Authority = MefNuovaAtIndex.Authority,
            MefPublishedOn = match?.MefPublishedOn ?? ParseIso(extract?.MefPublishedOn),
            ValidFrom = validFrom,
            ExtractJson = extract is null ? null : JsonSerializer.Serialize(extract, ComuneOfficialExtractValidator.JsonOptions),
            PromptVersion = promptVersion,
            RetrievedAt = now,
            Detail = Truncate(detail, 500),
        };
        db.ComuneOfficialProfileVersions.Add(version);
        profile.CurrentVersionId = version.Id;
        profile.LastEnsuredAt = now;
        profile.UpdatedAt = now;

        if (projectRates && status == ComuneOfficialProfileStatus.Extracted)
            await ProjectRatesAsync(comune, version, extract, deterministicRates, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        if (tx is not null)
            await tx.CommitAsync(cancellationToken);
    }

    private async Task ProjectRatesAsync(
        Comune comune,
        ComuneOfficialProfileVersion version,
        ComuneOfficialExtractDto? extract,
        IReadOnlyList<ExtractedTouristTaxRate>? deterministicRates,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.SourceUrl) || string.IsNullOrWhiteSpace(version.ActId))
            return;

        var validFromUtc = version.ValidFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var closeOn = validFromUtc.AddDays(-1);

        var open = await db.TouristTaxRates
            .Where(r => r.IstatCode == comune.IstatCode
                        && (r.EffectiveTo == null || r.EffectiveTo >= validFromUtc))
            .ToListAsync(cancellationToken);
        foreach (var row in open)
        {
            if (row.EffectiveTo is null || row.EffectiveTo >= validFromUtc)
                row.EffectiveTo = closeOn;
            row.UpdatedAt = now;
        }

        if (deterministicRates is { Count: > 0 })
        {
            foreach (var item in deterministicRates)
                db.TouristTaxRates.Add(ToRate(comune, version, item, validFromUtc, now));
            return;
        }

        if (extract?.Rates is not { Count: > 0 })
            return;

        foreach (var item in extract.Rates)
            db.TouristTaxRates.Add(ToRate(comune, version, item, validFromUtc, now));
    }

    private static TouristTaxRate ToRate(
        Comune comune,
        ComuneOfficialProfileVersion version,
        ExtractedTouristTaxRate item,
        DateTime validFromUtc,
        DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            City = comune.Name,
            IstatCode = comune.IstatCode,
            RegionCode = comune.RegionCode ?? item.RegionCode,
            AccommodationCategory = item.AccommodationCategory,
            SeasonStart = item.SeasonStart,
            SeasonEnd = item.SeasonEnd,
            CalculationMethod = item.CalculationMethod,
            RatePerPersonPerNight = item.RatePerPersonPerNight,
            PercentOfNightlyPrice = item.PercentOfNightlyPrice,
            CapPerPersonPerNight = item.CapPerPersonPerNight,
            MaxNights = item.MaxNights,
            MinimumAge = 14,
            ReducedRateMaxAge = item.ReducedRateMaxAge,
            ReducedRatePerPersonPerNight = item.ReducedRatePerPersonPerNight,
            IsActive = true,
            EffectiveFrom = validFromUtc,
            SourceUrl = version.SourceUrl,
            SourceAuthority = version.Authority,
            SourceRetrievedAt = now,
            VerificationLevel = TouristTaxRateVerification.Official,
            Notes = Truncate($"Atto {version.ActId}", 500) ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static TouristTaxRate ToRate(
        Comune comune,
        ComuneOfficialProfileVersion version,
        ComuneOfficialExtractRateDto item,
        DateTime validFromUtc,
        DateTime now)
    {
        var percent = string.Equals(
            item.CalculationMethod, ComuneOfficialCalculationMethods.PercentOfNightlyPrice, StringComparison.OrdinalIgnoreCase);
        return new TouristTaxRate
        {
            Id = Guid.NewGuid(),
            City = comune.Name,
            IstatCode = comune.IstatCode,
            RegionCode = comune.RegionCode ?? string.Empty,
            AccommodationCategory = Truncate(item.AccommodationCategory, 100),
            SeasonStart = item.SeasonStart,
            SeasonEnd = item.SeasonEnd,
            CalculationMethod = percent
                ? TouristTaxCalculationMethod.PercentOfNightlyPrice
                : TouristTaxCalculationMethod.PerPersonPerNight,
            RatePerPersonPerNight = percent ? 0m : item.RatePerPersonPerNight ?? 0m,
            PercentOfNightlyPrice = item.PercentOfNightlyPrice,
            CapPerPersonPerNight = item.CapPerPersonPerNight,
            MaxNights = item.MaxNights,
            MinimumAge = item.MinimumAge ?? 14,
            ReducedRateMaxAge = item.ReducedRateMaxAge,
            ReducedRatePerPersonPerNight = item.ReducedRatePerPersonPerNight,
            IsActive = true,
            EffectiveFrom = validFromUtc,
            SourceUrl = version.SourceUrl,
            SourceAuthority = version.Authority,
            SourceRetrievedAt = now,
            VerificationLevel = TouristTaxRateVerification.Official,
            Notes = Truncate($"Atto {version.ActId}", 500) ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ComuneOfficialExtractDto FromDeterministic(
        Comune _,
        MefNuovaAtIndex.MatchResult match,
        string pdfUrl,
        TouristTaxExtraction extraction) =>
        new()
        {
            Status = ComuneOfficialExtractStatus.Extracted,
            ActId = match.ActId,
            SourceUrl = pdfUrl,
            MefPublishedOn = match.MefPublishedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ValidFrom = match.MefPublishedOn is { } published
                ? MefPublicationCalendar.ValidFrom(published).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            LegalValue = false,
            Disclaimer = "Reportistica CasaZen, non valore legale.",
            Rates = extraction.Rates.Select(r => new ComuneOfficialExtractRateDto
            {
                AccommodationCategory = r.AccommodationCategory,
                SeasonStart = r.SeasonStart,
                SeasonEnd = r.SeasonEnd,
                CalculationMethod = r.CalculationMethod.ToString(),
                RatePerPersonPerNight = r.RatePerPersonPerNight,
                PercentOfNightlyPrice = r.PercentOfNightlyPrice,
                CapPerPersonPerNight = r.CapPerPersonPerNight,
                MaxNights = r.MaxNights,
                ReducedRateMaxAge = r.ReducedRateMaxAge,
                ReducedRatePerPersonPerNight = r.ReducedRatePerPersonPerNight,
            }).ToList(),
        };

    private async Task TouchProfileAsync(string istatCode, CancellationToken cancellationToken)
    {
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.ComuneOfficialProfile, istatCode));
        await TouchProfileLockedAsync(istatCode, cancellationToken);
        if (tx is not null)
            await tx.CommitAsync(cancellationToken);
    }

    private async Task TouchProfileLockedAsync(string istatCode, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var profile = await db.ComuneOfficialProfiles.FirstOrDefaultAsync(p => p.IstatCode == istatCode, cancellationToken);
        if (profile is null)
            return;
        profile.LastEnsuredAt = now;
        profile.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<ComuneOfficialProfileVersion?> CurrentVersionAsync(string istatCode, CancellationToken cancellationToken) =>
        db.ComuneOfficialProfileVersions.FirstOrDefaultAsync(
            v => v.IstatCode == istatCode && v.IsCurrent, cancellationToken);

    private async Task RecordIndexImportedIfNeededAsync(MefNuovaAtIndex.Snapshot index, CancellationToken cancellationToken)
    {
        var last = await LatestIndexFetchAsync(cancellationToken);
        if (last is { Sha256: not null }
            && string.Equals(last.Sha256, index.Sha256, StringComparison.OrdinalIgnoreCase))
            return;

        await RecordFetchAsync(
            OfficialSourceDatasets.MefNuovaAtIndex,
            index.SourceUrl,
            OfficialSourceFetchStatus.Imported,
            $"rows={index.Rows.Count}",
            sha256: index.Sha256,
            cancellationToken: cancellationToken);
    }

    private Task<OfficialSourceFetch?> LatestIndexFetchAsync(CancellationToken cancellationToken) =>
        db.OfficialSourceFetches.AsNoTracking()
            .Where(f => f.Dataset == OfficialSourceDatasets.MefNuovaAtIndex && f.Sha256 != null)
            .OrderByDescending(f => f.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task RecordFetchAsync(
        string dataset,
        string sourceUrl,
        string status,
        string? detail,
        int? httpStatus = null,
        string? sha256 = null,
        string? istatCode = null,
        CancellationToken cancellationToken = default)
    {
        db.OfficialSourceFetches.Add(new OfficialSourceFetch
        {
            Dataset = Truncate(dataset, 40) ?? dataset,
            SourceUrl = Truncate(sourceUrl, 500) ?? sourceUrl,
            Authority = MefNuovaAtIndex.Authority,
            RetrievedAt = _clock.GetUtcNow().UtcDateTime,
            Sha256 = sha256,
            HttpStatus = httpStatus,
            Status = Truncate(status, 40) ?? status,
            Detail = Truncate(detail, 500),
            IstatCode = istatCode,
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Comune official {Dataset} {Status}: {Url} ({Detail})", dataset, status, sourceUrl, detail);
    }

    private static DateOnly? ParseIso(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
