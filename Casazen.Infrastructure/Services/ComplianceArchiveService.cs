using System.Globalization;
using System.IO.Compression;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceArchiveService" />
/// <remarks>
/// <para><b>One boundary for every read.</b> A proof, a stay or a property is read with the org of the scope and <c>InScope(scope)</c>, and
/// <c>IgnoreQueryFilters([SoftDelete])</c> so the history of a property deleted since stays reachable (the tenant filter stays). Anything that
/// is another org's, out of the caller's reach, or unknown answers the same <see cref="NotFoundException"/>: the answer tells nothing about
/// which check refused it. The receipts of a tourist tax declaration or payment are records of money: every read leaves them out unless the caller
/// says it may read money (<c>payment.read</c>), and then they are not listed, not counted, not exported and the download is that same 404.</para>
/// <para><b>The report is the traffic light's.</b> The obligations and the proof of each are the ones <see cref="IComplianceStatusService"/> decides
/// with <see cref="ComplianceLightRules"/>, over a window of days instead of the lookback of the cockpit; this service only reads them, names them
/// and adds the stays that owe nothing (a no-show). It decides no state of its own.</para>
/// <para><b>Files.</b> A proof is a file of the private bucket under a key made by the server (<see cref="StorageKeys.ManualComplianceProof"/>); it
/// is handed out by a short-lived signed address or, where the storage cannot sign, through the authenticated endpoint. An export is built in
/// memory, bounded, and never stored.</para>
/// </remarks>
public sealed class ComplianceArchiveService(
    AppDbContext db,
    IFileStorage storage,
    IComplianceStatusService status,
    IPdfDocumentRenderer pdfRenderer,
    IFeatureFlags featureFlags,
    IOptions<ComplianceOptions> options,
    ILogger<ComplianceArchiveService> logger,
    TimeProvider? timeProvider = null) : IComplianceArchiveService
{
    private const int ReadChunkBytes = 81920;

    /// <summary>A page number beyond this is a mistake (it would overflow the offset of the query).</summary>
    private const int MaxPage = 100_000;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private ComplianceArchiveOptions Archive => options.Value.Archive;

    // ---------------------------------------------------------------- the list

    public async Task<ComplianceArchivePage> ListAsync(HostScope scope, ComplianceArchiveQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        await EnsurePropertyInScopeAsync(scope, query.PropertyId, ct);

        var page = Math.Clamp(query.Page, 1, MaxPage);
        var size = Math.Clamp(query.PageSize, 1, ComplianceArchiveLimits.MaxPageSize);
        var proofs = ProofsInScope(scope, query.PropertyId, query.Kind, query.From, query.To, query.MoneyProofs);

        var total = await proofs.CountAsync(ct);
        var rows = await proofs
            .OrderByDescending(p => p.UploadedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(p => new ProofRow(p.Id, p.Kind, p.PropertyId, p.Property.Name, p.Day, p.ContentType, p.UploadedAt))
            .ToListAsync(ct);

        return new ComplianceArchivePage(rows.Select(ToEntry).ToList(), page, size, total);
    }

    public async Task<ComplianceBookingCompliance> GetBookingComplianceAsync(HostScope scope, Guid bookingId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;

        var booking = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .InScope(scope)
            .Select(b => new { b.Id, b.PropertyId, b.CheckInDate })
            .FirstOrDefaultAsync(ct)
            ?? throw BookingNotFound();

        // The proofs of the stay are the receipts filed under its arrival day, for its property (the key the traffic light reads them by). They are
        // the Alloggiati receipts and nothing else: a record of money (a tourist tax receipt) never belongs to a stay, whoever asks.
        var arrival = RomeCalendar.DateInRome(booking.CheckInDate);
        var proofs = await ProofsInScope(scope, booking.PropertyId, ManualProofKind.AlloggiatiReceipt, arrival, arrival, moneyProofs: false)
            .OrderByDescending(p => p.UploadedAt)
            .ThenByDescending(p => p.Id)
            .Select(p => new ProofRow(p.Id, p.Kind, p.PropertyId, p.Property.Name, p.Day, p.ContentType, p.UploadedAt))
            .ToListAsync(ct);

        var lines = await db.ComplianceSendLogEntries
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(e => e.OrgId == orgId && e.BookingId == bookingId)
            .InScope(scope)
            .OrderByDescending(e => e.SentAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new SendLogRow(e.Id, e.Kind, e.Sender, e.SentAt, e.Reference, e.ByUserId))
            .ToListAsync(ct);

        var names = await NamesOfMembersAsync(orgId, lines.Select(l => l.ByUserId), ct);
        return new ComplianceBookingCompliance(
            booking.Id,
            booking.PropertyId,
            proofs.Select(ToEntry).ToList(),
            lines
                .Select(l => new ComplianceSendLogLine(
                    l.Id, l.Kind, l.Sender, l.SentAt, l.Reference, l.ByUserId is { } user ? names.GetValueOrDefault(user) : null))
                .ToList());
    }

    // ---------------------------------------------------------------- download

    public async Task<ComplianceArchiveDownload> OpenDownloadAsync(HostScope scope, Guid proofId, bool moneyProofs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;

        // The same boundary as the list: the org, the reach, the deleted properties kept, and a record of money only for who may read money.
        var proof = await ProofsInScope(scope, null, null, null, null, moneyProofs)
            .Where(p => p.Id == proofId)
            .Select(p => new { p.Kind, p.Day, p.FileKey, p.ContentType })
            .FirstOrDefaultAsync(ct)
            ?? throw ProofNotFound();

        var fileName = ComplianceArchiveRules.DownloadName(proof.Kind, proof.Day, proof.ContentType);
        if (!StorageKeys.IsValid(proof.FileKey))
        {
            logger.LogWarning("The stored key of compliance proof {ProofId} of org {OrgId} is not a storage key", proofId, orgId);
            throw ProofNotFound();
        }

        // Never a public address: a signed one of the private bucket that lives for a few minutes, made for this download.
        var signed = await storage.GetSignedReadUrlAsync(proof.FileKey, Archive.SignedUrlLifetime, fileName, ct);
        if (signed is not null)
            return new ComplianceArchiveDownload(signed, null, fileName, proof.ContentType);

        // The storage that cannot sign (development, tests) hands the content through the authenticated endpoint.
        var content = await storage.OpenReadAsync(StorageBucket.Private, proof.FileKey, ct);
        if (content is null)
        {
            logger.LogWarning("The file of compliance proof {ProofId} of org {OrgId} is missing from the private bucket", proofId, orgId);
            throw ProofNotFound();
        }

        return new ComplianceArchiveDownload(null, content, fileName, proof.ContentType);
    }

    // ---------------------------------------------------------------- export

    public async Task<ComplianceArchiveExportFile> ExportAsync(HostScope scope, ComplianceArchiveExportQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        if (query.From > query.To)
            throw new ArgumentException("The first day of the period is after the last.", nameof(query));

        await EnsurePropertyInScopeAsync(scope, query.PropertyId, ct);

        var settings = Archive;
        var fileLimit = settings.ExportFileLimit;
        var byteLimit = settings.ExportByteLimit;

        // One more than the limit tells the export is too big without counting the rest.
        var rows = await ProofsInScope(scope, query.PropertyId, null, query.From, query.To, query.MoneyProofs)
            .OrderBy(p => p.Day)
            .ThenBy(p => p.UploadedAt)
            .ThenBy(p => p.Id)
            .Select(p => new ExportRow(p.Id, p.Kind, p.PropertyId, p.Property.Name, p.Day, p.ContentType, p.UploadedAt, p.FileKey))
            .Take(fileLimit + 1)
            .ToListAsync(ct);
        if (rows.Count > fileLimit)
            throw new DomainRuleException(ComplianceArchiveErrorCodes.ExportTooLarge, "ComplianceExportTooLarge");

        var buffer = new MemoryStream();
        try
        {
            var included = 0;
            long bytes = 0;
            var index = new List<IReadOnlyList<object?>>();
            index.Add([.. ComplianceArchiveDocuments.IndexHeader]);
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var row in rows)
                {
                    var entry = ToEntry(new ProofRow(row.Id, row.Kind, row.PropertyId, row.PropertyName, row.Day, row.ContentType, row.UploadedAt));
                    string? name = null;

                    // A proof whose file is no longer in the bucket stays in the index, said so: the host sees what is missing, nothing is invented.
                    if (StorageKeys.IsValid(row.FileKey)
                        && await storage.OpenReadAsync(StorageBucket.Private, row.FileKey, ct) is { } source)
                    {
                        await using (source)
                        {
                            name = ZipName(row);
                            var zipEntry = zip.CreateEntry(name, CompressionLevel.Fastest);
                            await using var target = zipEntry.Open();
                            bytes += await CopyBoundedAsync(source, target, byteLimit - bytes, ct);
                        }

                        included++;
                    }
                    else
                    {
                        logger.LogWarning("The file of compliance proof {ProofId} of org {OrgId} is missing: left out of the export", row.Id, scope.OrgId);
                    }

                    index.Add(ComplianceArchiveDocuments.IndexRow(entry, name));
                }

                var indexEntry = zip.CreateEntry("index.csv", CompressionLevel.Optimal);
                await using (var indexTarget = indexEntry.Open())
                    await indexTarget.WriteAsync(CsvWriter.Write(index, CsvWriter.NoLimit), ct);

                // With the traffic light on, the pack also carries the coverage of the period: what is proven, what is missing and what only Octorate
                // holds («Da verificare nel Cassetto Compliance», source Octorate, no outcome), so whoever reads the ZIP never takes a gap for a proof.
                if (featureFlags.IsEnabled(FeatureFlags.ComplianceTrafficLight))
                {
                    var coverage = await GetCoverageReportAsync(scope, new ComplianceReportQuery(query.From, query.To, query.PropertyId), ct);
                    var coverageEntry = zip.CreateEntry(ComplianceArchiveDocuments.CoverageFileName, CompressionLevel.Optimal);
                    await using var coverageTarget = coverageEntry.Open();
                    await coverageTarget.WriteAsync(ComplianceArchiveDocuments.Csv(coverage), ct);
                }
            }

            buffer.Position = 0;
            logger.LogInformation(
                "Compliance proofs exported. OrgId={OrgId} Files={Files} Bytes={Bytes} From={From} To={To}",
                scope.OrgId, included, bytes, query.From, query.To);
            return new ComplianceArchiveExportFile(buffer, ComplianceArchiveDocuments.ExportFileName(query), included);
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    // ---------------------------------------------------------------- coverage report

    public async Task<ComplianceCoverageReport> GetCoverageReportAsync(HostScope scope, ComplianceReportQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        if (query.From > query.To)
            throw new ArgumentException("The first day of the period is after the last.", nameof(query));

        var orgId = scope.OrgId;
        await EnsurePropertyInScopeAsync(scope, query.PropertyId, ct);

        // The obligations and their proofs are the traffic light's: decided by ComplianceLightRules, over the days of the report.
        var evaluation = await status.EvaluateAsync(scope, new ComplianceStatusWindow(query.From, query.To, query.PropertyId), ct);
        var items = evaluation.Items
            .Where(i => i.Kind is ComplianceItemKind.Alloggiati or ComplianceItemKind.Istat or ComplianceItemKind.TouristTax)
            .ToList();

        // The stays that owe nothing: a no-show has nobody to communicate, and every reader of the status says so (AlloggiatiStatusRules).
        var from = query.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = query.To.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var noShowQuery = db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == orgId);
        if (query.PropertyId is { } onlyProperty)
            noShowQuery = noShowQuery.Where(b => b.PropertyId == onlyProperty);

        var noShows = await noShowQuery
            .InScope(scope)
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt != null)
            .Where(b => b.CheckInDate >= from && b.CheckInDate <= to)
            .Select(b => new StayRow(b.Id, b.PropertyId, b.Property.Name, b.BookingCode, b.CheckInDate))
            .ToListAsync(ct);

        var propertyIds = items.Select(i => i.PropertyId).Distinct().ToList();
        var propertyNames = propertyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Properties
                .AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var bookingIds = items.Where(i => i.BookingId.HasValue).Select(i => i.BookingId!.Value).Distinct().ToList();
        var stays = bookingIds.Count == 0
            ? new Dictionary<Guid, StayRow>()
            : await db.Bookings
                .AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .Where(b => b.OrgId == orgId && bookingIds.Contains(b.Id))
                .Select(b => new StayRow(b.Id, b.PropertyId, b.Property.Name, b.BookingCode, b.CheckInDate))
                .ToDictionaryAsync(b => b.Id, ct);

        var rows = new List<ComplianceReportRow>();
        foreach (var item in items)
        {
            stays.TryGetValue(item.BookingId ?? Guid.Empty, out var stay);
            rows.Add(ToRow(
                item,
                propertyNames.GetValueOrDefault(item.PropertyId, string.Empty),
                stay is null ? null : BookingCodes.Format(stay.BookingCode),
                stay is null ? null : RomeCalendar.DateInRome(stay.CheckInDate)));
        }

        rows.AddRange(noShows.Select(stay => new ComplianceReportRow(
            ComplianceItemKind.Alloggiati,
            stay.PropertyId,
            stay.PropertyName,
            stay.Id,
            BookingCodes.Format(stay.BookingCode),
            RomeCalendar.DateInRome(stay.CheckInDate),
            null,
            ComplianceEvidenceClass.NotApplicable,
            null,
            null,
            null,
            null,
            ComplianceReportReasons.NoShow,
            null)));

        // The stays first by arrival day, then the months; inside a day or a month by property, so the report reads the same each time.
        var ordered = rows
            .OrderBy(r => r.Day is null ? 1 : 0)
            .ThenBy(r => r.Day)
            .ThenBy(r => r.Period, StringComparer.Ordinal)
            .ThenBy(r => r.PropertyName, StringComparer.Ordinal)
            .ThenBy(r => r.Kind)
            .ThenBy(r => r.BookingCode, StringComparer.Ordinal)
            .ToList();

        var counts = new ComplianceCoverageCounts(
            ordered.Count(r => r.EvidenceClass == ComplianceEvidenceClass.OfficialProof),
            ordered.Count(r => r.EvidenceClass == ComplianceEvidenceClass.ExternalVerificationRequired),
            ordered.Count(r => r.EvidenceClass == ComplianceEvidenceClass.Missing),
            ordered.Count(r => r.EvidenceClass == ComplianceEvidenceClass.NotApplicable));

        // Complete only when nothing applicable is left without its proof: a row held only by Octorate is not a proof.
        var complete = counts.ExternalVerificationRequired == 0 && counts.Missing == 0;
        return new ComplianceCoverageReport(
            query.From, query.To, query.PropertyId, _clock.GetUtcNow().UtcDateTime, complete, counts, ordered);
    }

    public async Task<ComplianceReportFile> ExportReportAsync(
        HostScope scope,
        ComplianceReportQuery query,
        ComplianceReportFormat format,
        CancellationToken ct)
    {
        var report = await GetCoverageReportAsync(scope, query, ct);
        var name = ComplianceArchiveDocuments.ReportFileName(query, format);
        if (format == ComplianceReportFormat.Csv)
            return new ComplianceReportFile(ComplianceArchiveDocuments.Csv(report), "text/csv", name);

        // The properties the report is about, as the document names them: the one asked for, else those the person who made it can reach.
        var scopeLabel = "quelli accessibili a chi ha generato il rapporto";
        if (query.PropertyId is { } propertyId && await PropertyNameAsync(scope.OrgId, propertyId, ct) is { Length: > 0 } propertyName)
            scopeLabel = propertyName;

        var content = ComplianceArchiveDocuments.Pdf(report, scopeLabel);
        return new ComplianceReportFile(pdfRenderer.Render(content), "application/pdf", name);
    }

    // ---------------------------------------------------------------- upload

    public async Task<ComplianceArchiveEntry> UploadProofAsync(
        HostScope scope,
        UploadComplianceProofRequest request,
        Stream content,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UploadedByUserId);

        // What needs nothing from the database or the file comes first.
        if (!Enum.IsDefined(request.Kind))
            throw new DomainRuleException(ComplianceArchiveErrorCodes.ProofKindInvalid, "ComplianceProofKindInvalid");
        if (request.Day > _clock.TodayInRomeAsDateOnly() || request.Day.Year < ComplianceArchiveLimits.MinYear)
            throw new DomainRuleException(ComplianceArchiveErrorCodes.ProofDayInvalid, "ComplianceProofDayInvalid");

        var orgId = scope.OrgId;
        var propertyId = request.PropertyId;
        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .InScope(scope)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw PropertyNotFound(propertyId);

        // The file is read to the end and judged on its content before anything is stored.
        var (file, kind) = await ReadProofAsync(content, Archive.UploadMaxBytes, ct);
        await using (file)
        {
            var id = Guid.NewGuid();
            var now = _clock.GetUtcNow().UtcDateTime;
            var key = StorageKeys.ManualComplianceProof(orgId, id, StorageKeys.NewFileName(kind.Extension));

            // The file first, in the private bucket: a failed upload changes nothing, and a failed save removes the file.
            await storage.PutAsync(StorageBucket.Private, key, file, kind.ContentType, ct);
            var proof = new ManualComplianceProof
            {
                Id = id,
                OrgId = orgId,
                PropertyId = property.Id,
                Kind = request.Kind,
                Day = ComplianceArchiveRules.FileDay(request.Kind, request.Day),
                FileKey = key,
                ContentType = kind.ContentType,
                UploadedByUserId = request.UploadedByUserId,
                UploadedAt = now,
            };

            try
            {
                db.ManualComplianceProofs.Add(proof);
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                await TryDeleteFileAsync(key);
                throw;
            }

            logger.LogInformation(
                "Compliance proof uploaded. ProofId={ProofId} OrgId={OrgId} PropertyId={PropertyId} Kind={Kind}",
                proof.Id, orgId, property.Id, proof.Kind);
            return ToEntry(new ProofRow(proof.Id, proof.Kind, proof.PropertyId, property.Name, proof.Day, proof.ContentType, proof.UploadedAt));
        }
    }

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// The proofs of the caller's org, in the reach of its scope, the history of deleted properties included, narrowed as asked. The one place
    /// every read of the proofs goes through (list, download, export): the records of money (<see cref="ComplianceArchiveRules.MoneyKinds"/>) are
    /// in it only when <paramref name="moneyProofs"/> says the caller may read money; otherwise they are not there, for any kind asked.
    /// </summary>
    private IQueryable<ManualComplianceProof> ProofsInScope(
        HostScope scope,
        Guid? propertyId,
        ManualProofKind? kind,
        DateOnly? from,
        DateOnly? to,
        bool moneyProofs)
    {
        var orgId = scope.OrgId;
        var proofs = db.ManualComplianceProofs
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId)
            .InScope(scope);

        if (!moneyProofs)
            proofs = proofs.Where(p => !ComplianceArchiveRules.MoneyKinds.Contains(p.Kind));

        if (propertyId is { } property)
            proofs = proofs.Where(p => p.PropertyId == property);
        if (kind is { } proofKind)
            proofs = proofs.Where(p => p.Kind == proofKind);
        if (from is { } first)
            proofs = proofs.Where(p => p.Day >= first);
        if (to is { } last)
            proofs = proofs.Where(p => p.Day <= last);

        return proofs;
    }

    /// <summary>404 unless the property is the org's and in the reach of the caller (a property deleted since still counts: its history is kept).</summary>
    private async Task EnsurePropertyInScopeAsync(HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is not { } id)
            return;

        var orgId = scope.OrgId;
        var reachable = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId && p.Id == id)
            .InScope(scope)
            .AnyAsync(ct);
        if (!reachable)
            throw PropertyNotFound(id);
    }

    private async Task<string> PropertyNameAsync(Guid orgId, Guid propertyId, CancellationToken ct) =>
        await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct)
        ?? string.Empty;

    /// <summary>
    /// The names of the people of the caller's own org who declared a send. <c>User</c> is not tenant-filtered, so the org is in the predicate: a
    /// person of another org (or none) is nobody here, and the log shows no name for it.
    /// </summary>
    private async Task<Dictionary<string, string>> NamesOfMembersAsync(Guid orgId, IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var people = await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.OrgId == orgId)
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToListAsync(ct);
        return people
            .Select(u => (u.Id, Name: $"{u.FirstName} {u.LastName}".Trim()))
            .Where(u => u.Name.Length > 0)
            .ToDictionary(u => u.Id, u => u.Name);
    }

    // ---------------------------------------------------------------- shapes

    private static ComplianceArchiveEntry ToEntry(ProofRow row) =>
        new(
            row.Id,
            row.Kind,
            row.PropertyId,
            row.PropertyName,
            row.Day,
            ComplianceArchiveRules.PeriodOf(row.Kind, row.Day),
            ComplianceEvidenceSources.ManualUpload,
            row.UploadedAt,
            ComplianceArchiveRules.RetainUntil(row.UploadedAt),
            row.ContentType);

    /// <summary>
    /// One obligation of the traffic light as a row of the report. A stay is named by its code and arrival day, never by its guest (the label
    /// of the status item is the guest's name and stays out). A proven row has nothing left to do.
    /// </summary>
    private static ComplianceReportRow ToRow(ComplianceStatusItem item, string propertyName, string? bookingCode, DateOnly? arrival)
    {
        var verdict = item.Verdict;
        var proven = verdict.EvidenceClass == ComplianceEvidenceClass.OfficialProof;
        return new ComplianceReportRow(
            item.Kind,
            item.PropertyId,
            propertyName,
            item.BookingId,
            bookingCode,
            arrival,
            item.Period,
            verdict.EvidenceClass,
            verdict.EvidenceSource,
            verdict.LastVerifiedAt,
            item.DueAt,
            proven ? null : item.Action,
            verdict.Reason,
            verdict.HandoffTopic);
    }

    /// <summary>The name of a proof in the ZIP: the day, the kind and the start of its id, so two proofs of a day never meet.</summary>
    private static string ZipName(ExportRow row) =>
        $"prove/{row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{ComplianceArchiveRules.SlugOf(row.Kind)}_{row.Id.ToString("N")[..8]}"
        + ComplianceArchiveRules.ExtensionOf(row.ContentType);

    // ---------------------------------------------------------------- the file

    /// <summary>
    /// The whole content when it is a PDF, a JPEG or a PNG (by its first bytes) of at most <paramref name="limit"/>, positioned at the start.
    /// Nothing is trusted about the size or the type: the content is counted while it is read and judged by its signature.
    /// </summary>
    private static async Task<(MemoryStream Content, TouristTaxReceiptKind Kind)> ReadProofAsync(Stream source, long limit, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        try
        {
            var chunk = new byte[ReadChunkBytes];
            int read;
            while ((read = await source.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new DomainPayloadTooLargeException(
                        ComplianceArchiveErrorCodes.ProofTooLarge, "ComplianceProofTooLarge", limit / (1024 * 1024));
                }

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
                throw new DomainRuleException(ComplianceArchiveErrorCodes.ProofFileInvalid, "ComplianceProofFileInvalid");

            var kind = TouristTaxReceiptRules.Sniff(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
                ?? throw new DomainUnsupportedMediaTypeException(ComplianceArchiveErrorCodes.ProofTypeUnsupported, "ComplianceProofTypeUnsupported");
            buffer.Position = 0;
            return (buffer, kind);
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>, counting what it copies: beyond <paramref name="room"/> bytes the export is too big.</summary>
    private static async Task<long> CopyBoundedAsync(Stream source, Stream target, long room, CancellationToken ct)
    {
        var chunk = new byte[ReadChunkBytes];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            copied += read;
            if (copied > room)
                throw new DomainRuleException(ComplianceArchiveErrorCodes.ExportTooLarge, "ComplianceExportTooLarge");

            await target.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return copied;
    }

    private async Task TryDeleteFileAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete a compliance proof from the private bucket");
        }
    }

    private static NotFoundException ProofNotFound() =>
        new("Compliance proof not found.")
        {
            Code = ComplianceArchiveErrorCodes.ProofNotFound,
            MessageKey = "ComplianceProofNotFound",
        };

    private static NotFoundException BookingNotFound() =>
        new("Booking not found.")
        {
            Code = ComplianceArchiveErrorCodes.BookingNotFound,
            MessageKey = "ComplianceBookingNotFound",
        };

    private static NotFoundException PropertyNotFound(Guid propertyId) =>
        new($"Property {propertyId} not found") { MessageKey = "PropertyNotFound" };

    // ---------------------------------------------------------------- rows

    private sealed record ProofRow(Guid Id, ManualProofKind Kind, Guid PropertyId, string PropertyName, DateOnly Day, string ContentType, DateTime UploadedAt);

    private sealed record ExportRow(
        Guid Id,
        ManualProofKind Kind,
        Guid PropertyId,
        string PropertyName,
        DateOnly Day,
        string ContentType,
        DateTime UploadedAt,
        string FileKey);

    private sealed record SendLogRow(Guid Id, ComplianceSendKind Kind, ComplianceSender Sender, DateTime SentAt, string? Reference, string? ByUserId);

    private sealed record StayRow(Guid Id, Guid PropertyId, string PropertyName, string BookingCode, DateTime CheckInDate);
}
