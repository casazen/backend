using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAccountantPackageService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Composed, never recomputed.</b> The agency summary is <see cref="IAgencySummaryService"/>'s; the collections, the balances to collect, the costs of the channels and the
/// tourist tax summary are the files of their own services (<see cref="HostCollectionsReportDocuments"/>, <see cref="ChannelCostsReportDocuments"/>,
/// <see cref="TouristTaxSummaryDocuments"/>); the PDF and the XML of a document, the PDF and the CSV of a statement and the PDF of an F24 are the ones the screens download.
/// What the package adds are the registers and the VAT tables of the documents, written from what the documents store, and the index.</item>
/// <item><b>The same structure every time.</b> Every file of <see cref="AccountantPackageStructure"/> is always written, header only when its list is empty; the registers and the
/// VAT tables are one for each issuer of the period and always one for the issuer of the org today. The only folders that are not there are the omitted ones, and the index says so.</item>
/// <item><b>The owner accounting.</b> The invoices to the owners, the statements, the withholding and the owner parts of the summary are read only when
/// <see cref="AccountantPackageAccess.AllowsOwnerAccounting"/> says so (the permission of the caller and a scope that reaches the whole org): otherwise nothing of them is read, the folders
/// are not in the ZIP and <c>indice.csv</c> lists them as omitted.</item>
/// <item><b>Streamed and capped.</b> The registers and the tables are made first (they are text), then the documents are rendered and written one at a time, and <c>indice.csv</c> last, because the
/// hash of a file is known once it was written. The number of the files of documents is checked against <c>DocumentArchive:MaxExportFiles</c> before anything is written, and every
/// file counts <see cref="EstimatedFileBytes"/> against <c>DocumentArchive:MaxExportBytes</c> (a generated file has no size until it is made: the cap is an upper bound of the response, as the
/// 10 MB of an undeclared property document are).</item>
/// <item><b>Nothing personal in the log.</b> The log carries the ids of the org and the counts, never a name, a number or an amount.</item>
/// </list>
/// </remarks>
public sealed class AccountantPackageService(
    AppDbContext db,
    IFiscalDocumentService documents,
    IOwnerStatementDocuments statementFiles,
    IStatementExportService statementExport,
    IAgencySummaryService agencySummary,
    IHostCollectionsReportService collections,
    IChannelCostsReportService channelCosts,
    ITouristTaxSummaryService touristTax,
    IPortalAccountHolderResolver holders,
    IPdfDocumentRenderer pdfRenderer,
    IDocumentTexts documentTexts,
    IOptions<DocumentArchiveOptions> options,
    ILogger<AccountantPackageService> logger) : IAccountantPackageService
{
    /// <summary>What a file of the package counts against the cap of bytes: 256 KB, far above a PDF of a document (30 to 80 KB), because the real size is known only once the file is made.</summary>
    public const long EstimatedFileBytes = 256L * 1024;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<AccountantPackageResult> BuildAsync(
        HostScope scope,
        AccountantPackageAccess access,
        AccountantPackagePeriod period,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(destination);

        var limits = options.Value;
        var ownerAccounting = access.AllowsOwnerAccounting(scope);
        var t = new PackageTexts(documentTexts, PackageTexts.PackageLanguage);
        var plan = new Plan();

        // A period with more documents than the cap allows is refused before the documents are read: each is at least one file.
        var guestCount = await AccountantPackageFacts.IssuedDocuments(db, scope, period, FiscalDocumentPurpose.GuestStay).CountAsync(cancellationToken);
        var ownerCount = ownerAccounting
            ? await AccountantPackageFacts.IssuedDocuments(db, scope, period, FiscalDocumentPurpose.OwnerInvoice).CountAsync(cancellationToken)
            : 0;
        CheckCaps(guestCount + ownerCount, limits);

        var fallback = await AccountantPackageFacts.CurrentIssuerAsync(db, scope.OrgId, cancellationToken);
        var current = AccountantPackageFacts.IssuerOf(fallback);
        var guestDocuments = await AccountantPackageFacts.ReadAsync(db, scope, period, FiscalDocumentPurpose.GuestStay, cancellationToken);
        var ownerInvoices = ownerAccounting
            ? await AccountantPackageFacts.ReadAsync(db, scope, period, FiscalDocumentPurpose.OwnerInvoice, cancellationToken)
            : [];

        var slugs = SlugsOf(IssuersOf(guestDocuments, ownerInvoices, fallback, current));
        PlanGuestDocuments(plan, guestDocuments, fallback, current, slugs, period, t);
        if (ownerAccounting)
        {
            PlanOwnerInvoices(plan, ownerInvoices, current, period, t);
            await PlanStatementsAsync(plan, scope.OrgId, period, t, cancellationToken);
            await PlanWithholdingAsync(plan, scope.OrgId, period, t, cancellationToken);
        }

        await PlanAgencySummaryAsync(plan, scope, access, period, t, cancellationToken);
        await PlanVatAsync(plan, scope, ownerAccounting, guestDocuments, ownerInvoices, fallback, current, slugs, period, t, cancellationToken);
        await PlanTouristTaxAsync(plan, scope, period, guestDocuments, t, cancellationToken);
        await PlanCollectionsAsync(plan, scope, period, cancellationToken);

        CheckCaps(plan.DocumentFiles.Count, limits);
        var omitted = ownerAccounting ? [] : AccountantPackageStructure.OwnerAccountingFolders;

        var written = await WriteAsync(plan, period, omitted, t, destination, cancellationToken);
        logger.LogInformation(
            "Accountant package of {Period} written. OrgId={OrgId} Documents={Documents} Files={Files} Bytes={Bytes} OwnerAccounting={OwnerAccounting}",
            period.Key,
            scope.OrgId,
            plan.Documents,
            written.Files,
            written.Bytes,
            ownerAccounting);
        return new AccountantPackageResult(plan.Documents, written.Files, written.Bytes, omitted);
    }

    // ---------------------------------------------------------------- what goes in the package

    private void PlanGuestDocuments(
        Plan plan,
        IReadOnlyList<PackageDocument> guest,
        FiscalIssuerSnapshot fallback,
        AgencyIssuer current,
        IReadOnlyDictionary<string, string> slugs,
        AccountantPackagePeriod period,
        PackageTexts t)
    {
        var folder = AccountantPackageStructure.GuestDocumentsFolder;
        foreach (var issuer in IssuersOf(guest, [], fallback, current))
        {
            var mine = guest.Where(d => AccountantPackageFacts.IssuerOf(d.IssuerSnapshot, fallback).Key == issuer.Key).ToList();
            plan.AddTables(folder, "registro-" + slugs[issuer.Key], AccountantPackageDocuments.Register(mine, ownerInvoices: false, issuer, period, t));
        }

        foreach (var document in guest)
            PlanDocumentFiles(plan, folder, document, t);

        plan.Documents += guest.Count;
    }

    private void PlanOwnerInvoices(Plan plan, IReadOnlyList<PackageDocument> invoices, AgencyIssuer current, AccountantPackagePeriod period, PackageTexts t)
    {
        var folder = AccountantPackageStructure.OwnerInvoicesFolder;
        plan.AddTables(folder, "registro", AccountantPackageDocuments.Register(invoices, ownerInvoices: true, current, period, t));
        foreach (var invoice in invoices)
            PlanDocumentFiles(plan, folder, invoice, t);

        plan.Documents += invoices.Count;
    }

    /// <summary>The PDF of a document CasaZen issued and its XML when it has one: the ones the screens download. A document imported from another system has neither.</summary>
    private void PlanDocumentFiles(Plan plan, string folder, PackageDocument document, PackageTexts t)
    {
        if (document.Source != FiscalDocumentSource.CasaZen)
            return;

        var stem = $"{DocumentArchiveExport.Date(document.IssueDate)}-{DocumentArchiveExport.Sanitize(document.Number ?? document.Id.ToString("N"))}";
        var label = $"{AccountantPackageDocuments.KindLabel(document.Kind, t)} {document.Number}".Trim();
        var id = document.Id;
        plan.AddDocumentFile(
            folder,
            stem,
            ".pdf",
            $"{label} (PDF)",
            async token => await documents.GetPdfAsync(id, PackageTexts.PackageLanguage, token));
        if (document.HasXml)
        {
            plan.AddDocumentFile(
                folder,
                stem,
                ".xml",
                $"{label} (XML FatturaPA)",
                async token => Encoding.UTF8.GetBytes(await documents.GetXmlAsync(id, token)));
        }
    }

    /// <summary>The published statements of the months of the period: the list, and the PDF and the CSV of each (the latest published version of each owner and month).</summary>
    private async Task PlanStatementsAsync(Plan plan, Guid orgId, AccountantPackagePeriod period, PackageTexts t, CancellationToken cancellationToken)
    {
        var fromKey = (period.From.Year * 100) + period.From.Month;
        var toKey = (period.To.Year * 100) + period.To.Month;
        var versions = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PublishedAt != null && ((v.PeriodYear * 100) + v.PeriodMonth) >= fromKey && ((v.PeriodYear * 100) + v.PeriodMonth) <= toKey)
            .Select(v => new
            {
                v.Id,
                v.PropertyOwnerId,
                v.PeriodYear,
                v.PeriodMonth,
                v.VersionNumber,
                v.Currency,
                v.Revenue,
                v.ChannelFees,
                v.Commission,
                v.Charges,
                v.Payments,
                v.ClosingBalance,
                PublishedAt = v.PublishedAt!.Value,
                v.PublishedSha256,
            })
            .ToListAsync(cancellationToken);
        var latest = versions
            .GroupBy(v => (v.PropertyOwnerId, v.PeriodYear, v.PeriodMonth))
            .Select(g => g.MaxBy(v => v.VersionNumber)!)
            .OrderBy(v => v.PeriodYear)
            .ThenBy(v => v.PeriodMonth)
            .ThenBy(v => v.PropertyOwnerId)
            .ToList();
        var names = await AccountantPackageFacts.OwnerNamesAsync(db, orgId, [.. latest.Select(v => v.PropertyOwnerId).Distinct()], cancellationToken);

        var rows = latest
            .Select(v => new PackageStatement(
                v.Id,
                v.PropertyOwnerId,
                names.GetValueOrDefault(v.PropertyOwnerId),
                v.PeriodYear,
                v.PeriodMonth,
                v.VersionNumber,
                v.Currency,
                v.Revenue,
                v.ChannelFees,
                v.Commission,
                v.Charges,
                v.Payments,
                v.ClosingBalance,
                v.PublishedAt,
                v.PublishedSha256))
            .ToList();
        var folder = AccountantPackageStructure.StatementsFolder;
        plan.AddTables(folder, "elenco-rendiconti", [AccountantPackageDocuments.Statements(rows, t)]);
        foreach (var statement in rows)
        {
            var versionId = statement.VersionId;
            var owner = statement.OwnerName is { Length: > 0 } name ? DocumentArchiveExport.Sanitize(name) : "proprietario-" + statement.OwnerId.ToString("N")[..8];
            var stem = string.Create(CultureInfo.InvariantCulture, $"{statement.Year:0000}-{statement.Month:00}-{owner}-v{statement.VersionNumber}");
            var label = $"{AccountantPackageDocuments.OwnerLabel(statement.OwnerName, t)} {statement.Year:0000}-{statement.Month:00} v{statement.VersionNumber}";
            plan.AddDocumentFile(
                folder, stem, ".pdf", $"{label} (PDF)", async token => (await statementFiles.GetPdfAsync(orgId, versionId, PackageTexts.PackageLanguage, token)).Content);
            plan.AddDocumentFile(
                folder, stem, ".csv", $"{label} (CSV)", async token => (await statementFiles.GetCsvAsync(orgId, versionId, PackageTexts.PackageLanguage, token)).Content);
        }

        plan.Documents += rows.Count;
    }

    /// <summary>The withholding of the owners, the F24 data and the Certificazione Unica data of the period: stored evidence and records only, never generated here (a read writes nothing).</summary>
    private async Task PlanWithholdingAsync(Plan plan, Guid orgId, AccountantPackagePeriod period, PackageTexts t, CancellationToken cancellationToken)
    {
        var fromKey = (period.From.Year * 100) + period.From.Month;
        var toKey = (period.To.Year * 100) + period.To.Month;
        var folder = AccountantPackageStructure.WithholdingFolder;

        var evidence = await db.TaxObligationEvidence.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.Decision == TaxDecision.Applicable && ((e.PeriodYear * 100) + e.PeriodMonth) >= fromKey && ((e.PeriodYear * 100) + e.PeriodMonth) <= toKey)
            .Select(e => new { e.PropertyOwnerId, e.PeriodYear, e.PeriodMonth, e.TaxCode, e.OwnerPayoutId, e.ReversesEvidenceId, e.GrossBase, e.Amount })
            .ToListAsync(cancellationToken);
        var names = await AccountantPackageFacts.OwnerNamesAsync(db, orgId, [.. evidence.Select(e => e.PropertyOwnerId).Distinct()], cancellationToken);
        var withholding = evidence
            .GroupBy(e => (e.PropertyOwnerId, e.PeriodYear, e.PeriodMonth, e.TaxCode))
            .Select(g => new PackageWithholding(
                names.GetValueOrDefault(g.Key.PropertyOwnerId),
                g.Key.PeriodYear,
                g.Key.PeriodMonth,
                g.Key.TaxCode,
                g.Where(e => e.ReversesEvidenceId is null).Select(e => e.OwnerPayoutId).Distinct().Count(),
                g.Sum(e => e.GrossBase ?? 0m),
                g.Sum(e => e.Amount ?? 0m)))
            .OrderBy(r => r.Year)
            .ThenBy(r => r.Month)
            .ThenBy(r => r.OwnerName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        plan.AddTables(folder, "ritenute-proprietari", [AccountantPackageDocuments.Withholding(withholding, t)]);

        var f24 = await db.F24Records.AsNoTracking()
            .Where(f => f.OrgId == orgId && ((f.ReferenceYear * 100) + f.ReferenceMonth) >= fromKey && ((f.ReferenceYear * 100) + f.ReferenceMonth) <= toKey)
            .OrderBy(f => f.ReferenceYear)
            .ThenBy(f => f.ReferenceMonth)
            .ToListAsync(cancellationToken);
        plan.AddTables(folder, "f24", [AccountantPackageDocuments.F24(f24, t)]);
        foreach (var record in f24)
        {
            var (month, year) = (record.ReferenceMonth, record.ReferenceYear);
            plan.AddDocumentFile(
                folder,
                string.Create(CultureInfo.InvariantCulture, $"f24-{year:0000}-{month:00}"),
                ".pdf",
                string.Create(CultureInfo.InvariantCulture, $"F24 {year:0000}-{month:00} (PDF)"),
                async token => (await statementExport.ExportF24Async(orgId, month, year, PackageTexts.PackageLanguage, token)).Content);
        }

        plan.Documents += f24.Count;

        // The Certificazione Unica is a year's data: a month has the file with its header only.
        var cuYear = period.From.Year;
        var cuRows = new List<PackageCuRow>();
        if (period.Kind == AccountantPackagePeriodKind.Year)
        {
            var dataset = await db.TaxReportingDatasets.AsNoTracking()
                .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Kind == TaxReportingDataset.CertificazioneUnica && d.Year == cuYear, cancellationToken);
            if (dataset is not null)
            {
                var owners = ReadCu(dataset.PayloadJson);
                var cuNames = await AccountantPackageFacts.OwnerNamesAsync(db, orgId, [.. owners.Select(o => o.OwnerId)], cancellationToken);
                cuRows.AddRange(owners.Select(o => new PackageCuRow(cuNames.GetValueOrDefault(o.OwnerId), o.GrossBase, o.Withheld, o.Evidence)));
            }
        }

        plan.AddTables(folder, string.Create(CultureInfo.InvariantCulture, $"cu-{cuYear:0000}"), [AccountantPackageDocuments.CertificazioneUnica(cuYear, cuRows, t)]);
    }

    private async Task PlanAgencySummaryAsync(Plan plan, HostScope scope, AccountantPackageAccess access, AccountantPackagePeriod period, PackageTexts t, CancellationToken cancellationToken)
    {
        var summary = await agencySummary.GetAsync(scope, access, period, cancellationToken);
        var tables = AgencySummaryDocuments.Tables(summary, t);
        var folder = AccountantPackageStructure.AgencySummaryFolder;
        plan.AddTables(folder, "riepilogo-agenzia", tables);
        plan.AddGenerated(
            $"{folder}/riepilogo-agenzia.pdf",
            $"{t.Get(PackageLabels.SummaryTitle, period.Key)} (PDF)",
            pdfRenderer.Render(AgencySummaryDocuments.Pdf(summary, t)),
            null);
    }

    /// <summary>The VAT tables of every issuer of the period: the documents of the guests and, when the caller may read them, the invoices to the owners (and the portal commissions of the agency, for information).</summary>
    private async Task PlanVatAsync(
        Plan plan,
        HostScope scope,
        bool ownerAccounting,
        IReadOnlyList<PackageDocument> guest,
        IReadOnlyList<PackageDocument> owner,
        FiscalIssuerSnapshot fallback,
        AgencyIssuer current,
        IReadOnlyDictionary<string, string> slugs,
        AccountantPackagePeriod period,
        PackageTexts t,
        CancellationToken cancellationToken)
    {
        var portalCommissions = ownerAccounting
            ? await AccountantPackageFacts.PortalCommissionsBorneAsync(db, holders, scope.OrgId, period, cancellationToken)
            : [];
        foreach (var issuer in IssuersOf(guest, owner, fallback, current))
        {
            var mine = guest.Concat(owner).Where(d => AccountantPackageFacts.IssuerOf(d.IssuerSnapshot, fallback).Key == issuer.Key).ToList();
            var portals = issuer.Key == current.Key ? portalCommissions : [];
            plan.AddTables(AccountantPackageStructure.VatFolder, "iva-" + slugs[issuer.Key], AccountantPackageDocuments.Vat(issuer, mine, portals, t));
        }
    }

    private async Task PlanTouristTaxAsync(
        Plan plan, HostScope scope, AccountantPackagePeriod period, IReadOnlyList<PackageDocument> guest, PackageTexts t, CancellationToken cancellationToken)
    {
        var folder = AccountantPackageStructure.TouristTaxFolder;
        var summary = await touristTax.GetSummaryAsync(scope, period.FiscalPeriod, cancellationToken);
        var texts = new TouristTaxCsvTexts(t.Get("TouristTaxStatusNothingRecorded"), t.Get("TouristTaxOwedIncomplete"));
        var tables = TouristTaxSummaryDocuments.Tables(summary, texts);
        var title = tables[0].Title;
        plan.AddGenerated($"{folder}/riepilogo-tassa-soggiorno.csv", $"{title} (CSV)", TouristTaxSummaryDocuments.Csv(summary, texts), RowsOf(tables));
        plan.AddGenerated($"{folder}/riepilogo-tassa-soggiorno.xlsx", $"{title} (Excel)", TouristTaxSummaryDocuments.Xlsx(summary, texts), RowsOf(tables));

        var swiss = await AccountantPackageFacts.SwissTouristTaxAsync(db, scope.OrgId, guest, cancellationToken);
        plan.AddTables(folder, "tassa-soggiorno-svizzera", [AccountantPackageDocuments.SwissTouristTax(swiss, t)]);
    }

    private async Task PlanCollectionsAsync(Plan plan, HostScope scope, AccountantPackagePeriod period, CancellationToken cancellationToken)
    {
        var folder = AccountantPackageStructure.CollectionsFolder;

        var collected = await collections.GetCollectionsAsync(scope, period.ReportQuery, cancellationToken);
        var collectedTables = HostCollectionsReportDocuments.Tables(collected);
        plan.AddGenerated($"{folder}/incassato.csv", $"{collectedTables[0].Title} (CSV)", HostCollectionsReportDocuments.Csv(collected), RowsOf(collectedTables));
        plan.AddGenerated($"{folder}/incassato.xlsx", $"{collectedTables[0].Title} (Excel)", HostCollectionsReportDocuments.Xlsx(collected), RowsOf(collectedTables));

        var balances = await collections.GetBalancesToCollectAsync(scope, new HostBalancesQuery(), cancellationToken);
        var balancesTable = HostCollectionsReportDocuments.Table(balances);
        plan.AddGenerated($"{folder}/saldi-da-incassare.csv", $"{balancesTable.Title} (CSV)", HostCollectionsReportDocuments.Csv(balances), RowsOf([balancesTable]));
        plan.AddGenerated($"{folder}/saldi-da-incassare.xlsx", $"{balancesTable.Title} (Excel)", HostCollectionsReportDocuments.Xlsx(balances), RowsOf([balancesTable]));

        var costs = await channelCosts.GetChannelCostsAsync(scope, period.ReportQuery, cancellationToken);
        var costsTable = ChannelCostsReportDocuments.Table(costs);
        plan.AddGenerated($"{folder}/costi-dei-canali.csv", $"{costsTable.Title} (CSV)", ChannelCostsReportDocuments.Csv(costs), RowsOf([costsTable]));
        plan.AddGenerated($"{folder}/costi-dei-canali.xlsx", $"{costsTable.Title} (Excel)", ChannelCostsReportDocuments.Xlsx(costs), RowsOf([costsTable]));
    }

    // ---------------------------------------------------------------- the ZIP

    /// <summary>Writes the generated files, then each file of a document (rendered when its turn comes), then <c>indice.csv</c>, to <paramref name="destination"/>.</summary>
    private async Task<(int Files, long Bytes)> WriteAsync(
        Plan plan, AccountantPackagePeriod period, IReadOnlyList<string> omitted, PackageTexts t, Stream destination, CancellationToken cancellationToken)
    {
        var entries = new List<PackageIndexEntry>();
        var sink = new DocumentArchiveExport.DeferredWriteStream(destination);
        long bytes = 0;
        await using (var archive = await ZipArchive.CreateAsync(sink, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
        {
            foreach (var file in plan.GeneratedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes += await AddAsync(archive, entries, file.Path, file.Description, file.Content, file.Rows, cancellationToken);
            }

            foreach (var file in plan.DocumentFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await file.Load(cancellationToken);
                bytes += await AddAsync(archive, entries, file.Path, file.Description, content, null, cancellationToken);
            }

            var index = CsvWriter.Write(AccountantPackageDocuments.Index(period, entries, omitted, t), CsvWriter.NoLimit);
            await WriteEntryAsync(archive, AccountantPackageStructure.IndexFile, index, cancellationToken);
            bytes += index.Length;
        }

        await sink.FlushAsync(cancellationToken);
        return (entries.Count + 1, bytes);
    }

    private static async Task<long> AddAsync(
        ZipArchive archive, List<PackageIndexEntry> entries, string path, string description, byte[] content, int? rows, CancellationToken cancellationToken)
    {
        await WriteEntryAsync(archive, path, content, cancellationToken);
        entries.Add(new PackageIndexEntry(path, description, rows, Convert.ToHexStringLower(SHA256.HashData(content))));
        return content.Length;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string path, byte[] content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        await using var target = await entry.OpenAsync(cancellationToken);
        await target.WriteAsync(content, cancellationToken);
    }

    // ---------------------------------------------------------------- the caps, the issuers, the names

    /// <summary>422 <c>accountant_package_too_large</c> when <paramref name="files"/> files of documents (each counted at <see cref="EstimatedFileBytes"/>) are more than the caps of the archive allow.</summary>
    private static void CheckCaps(int files, DocumentArchiveOptions limits)
    {
        if (files <= limits.MaxExportFiles && files * EstimatedFileBytes <= limits.MaxExportBytes)
            return;

        throw new DomainRuleException(
            AccountantPackageErrors.TooLarge,
            PackageLabels.TooLargeMessage,
            files,
            limits.MaxExportFiles,
            limits.MaxExportBytes / (1024 * 1024),
            EstimatedFileBytes / 1024)
        {
            Extensions = new Dictionary<string, object?>
            {
                ["fileCount"] = files,
                ["maxFiles"] = limits.MaxExportFiles,
                ["maxBytes"] = limits.MaxExportBytes,
            },
        };
    }

    /// <summary>The issuers of the period, by key, and always the issuer of the org today (so the structure is the same for an empty period).</summary>
    private static IReadOnlyList<AgencyIssuer> IssuersOf(
        IReadOnlyList<PackageDocument> guest, IReadOnlyList<PackageDocument> owner, FiscalIssuerSnapshot fallback, AgencyIssuer current) =>
        guest.Concat(owner)
            .Select(d => AccountantPackageFacts.IssuerOf(d.IssuerSnapshot, fallback))
            .Append(current)
            .GroupBy(i => i.Key)
            .Select(g => g.First())
            .OrderBy(i => i.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>A name for the files of each issuer: its country and tax number (else its name), safe on any file system and told apart from the others (<c>-2</c>, <c>-3</c>...); the register and the VAT file of an issuer share it.</summary>
    private static IReadOnlyDictionary<string, string> SlugsOf(IReadOnlyList<AgencyIssuer> issuers)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var issuer in issuers)
        {
            var slug = DocumentArchiveExport.Sanitize($"{issuer.CountryCode}-{issuer.TaxId ?? issuer.Name}").ToLowerInvariant();
            var candidate = slug;
            for (var attempt = 2; !taken.Add(candidate); attempt++)
                candidate = $"{slug}-{attempt.ToString(CultureInfo.InvariantCulture)}";

            slugs[issuer.Key] = candidate;
        }

        return slugs;
    }

    private static int RowsOf(IReadOnlyList<ReportTable> tables) => tables.Sum(table => table.Rows.Count);

    /// <summary>The owners of the annual dataset as it was stored (a payload that cannot be read is a damaged dataset: it fails the build, never an empty file).</summary>
    private static IReadOnlyList<CuOwner> ReadCu(string payload) => JsonSerializer.Deserialize<List<CuOwner>>(payload, Web) ?? [];

    private sealed record CuOwner(Guid OwnerId, decimal GrossBase, decimal Withheld, int Evidence);

    /// <summary>A file the package holds that was made in memory (a table as CSV or Excel, a PDF), and its rows when it is a table.</summary>
    private sealed record GeneratedFile(string Path, string Description, byte[] Content, int? Rows);

    /// <summary>A file of a document, made when its turn comes (a PDF is rendered, an XML is read): at most one is in memory at a time.</summary>
    private sealed record DocumentFile(string Path, string Description, Func<CancellationToken, Task<byte[]>> Load);

    /// <summary>What the package will hold, in the order it is written; the names are taken once, so no two files share a path.</summary>
    private sealed class Plan
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        public List<GeneratedFile> GeneratedFiles { get; } = [];

        public List<DocumentFile> DocumentFiles { get; } = [];

        /// <summary>The fiscal documents, the statements and the F24 the package holds (a PDF with its XML counts once).</summary>
        public int Documents { get; set; }

        /// <summary>A table (or several, one under the other) as a CSV and as an Excel file with the same rows.</summary>
        public void AddTables(string folder, string name, IReadOnlyList<ReportTable> tables)
        {
            var rows = RowsOf(tables);
            var title = tables[0].Title;
            AddGenerated($"{folder}/{name}.csv", $"{title} (CSV)", CsvWriter.Write(tables), rows);
            AddGenerated(
                $"{folder}/{name}.xlsx",
                $"{title} (Excel)",
                XlsxWriter.Write([.. tables.Select(table => new ReportSheet(SheetNameOf(table.Title), table))]),
                rows);
        }

        public void AddGenerated(string path, string description, byte[] content, int? rows) =>
            GeneratedFiles.Add(new GeneratedFile(Unique(path), description, content, rows));

        public void AddDocumentFile(string folder, string stem, string extension, string description, Func<CancellationToken, Task<byte[]>> load) =>
            DocumentFiles.Add(new DocumentFile(Unique($"{folder}/{stem}{extension}"), description, load));

        private string Unique(string path)
        {
            var dot = path.LastIndexOf('.');
            var stem = dot > 0 ? path[..dot] : path;
            var extension = dot > 0 ? path[dot..] : string.Empty;
            var candidate = path;
            for (var attempt = 2; !_paths.Add(candidate); attempt++)
                candidate = $"{stem}-{attempt.ToString(CultureInfo.InvariantCulture)}{extension}";

            return candidate;
        }

        private static string SheetNameOf(string title)
        {
            var cut = title.IndexOfAny([',', ':', ';', '(', '.']);
            return (cut > 0 ? title[..cut] : title).Trim();
        }
    }
}
