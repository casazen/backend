using System.Globalization;
using System.Text;
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
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerIssuedDocumentsExport"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>No org of its own.</b> The owner portal has no org: the tenant filter of its request matches nothing, so every read here ignores it and names the org and the owner of
/// the documents (as <see cref="OwnerPortalService"/> does); the payment state of a PDF is read the same way (<see cref="FiscalDocumentService.CollectedByBookingAsync"/>).</item>
/// <item><b>The documents of the owner and no other.</b> They are found by the issuer profile of the owner (<c>OrgFiscalIssuerProfile.PropertyOwnerId</c>): the agency's own documents,
/// another owner's, a draft and a proforma are never reached, whatever id is asked for.</item>
/// <item><b>Composed, never recomputed.</b> The registers, the VAT for each rate and the totals are the tables of the accountant package (<see cref="AccountantPackageDocuments"/>) over
/// the documents as they store their amounts; the PDF is the one of every fiscal document (<see cref="FiscalDocumentPdfs"/>) from the issuer snapshot of the issue; the ZIP is written by
/// the writer of the package, with its caps and its index with the SHA-256 of every file.</item>
/// <item><b>Streamed and capped.</b> The files of the documents are counted before one is read, and made one at a time.</item>
/// </list>
/// </remarks>
public sealed class OwnerIssuedDocumentsExport(
    AppDbContext db,
    IOwnerStatementDocuments statementDocuments,
    IPdfDocumentRenderer pdfRenderer,
    IDocumentTexts documentTexts,
    IOptions<DocumentArchiveOptions> options,
    ILogger<OwnerIssuedDocumentsExport> logger) : IOwnerIssuedDocumentsExport
{
    /// <summary>The longest name a file of a document is offered under.</summary>
    private const int FileNameMaxLength = 100;

    public async Task<IReadOnlyList<OwnerIssuedDocumentInfo>> ListAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        AccountantPackagePeriod? period,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var profiles = await ProfilesOfAsync(owners, ct);
        if (profiles.Count == 0)
            return [];

        var rows = await ListRows([.. profiles.Keys], period, limit).ToListAsync(ct);

        return
        [
            .. rows
                .Where(r => profiles.TryGetValue(r.ProfileId, out var owner) && owner.OrgId == r.OrgId)
                .Select(r => new OwnerIssuedDocumentInfo(
                    r.Id, r.OrgId, profiles[r.ProfileId].OwnerId, r.Number!, r.IssueDate, r.Kind, r.Total, r.Currency, r.SdiStatus, r.HasXml)),
        ];
    }

    public async Task<OwnerIssuedDocumentFile?> GetPdfAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        Guid documentId,
        string? language,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owners);

        if (await FindAsync(owners, documentId, withLines: true, ct) is not { } document)
            return null;

        return new OwnerIssuedDocumentFile(await PdfOfAsync(document, language, ct), FileNameOf(document, ".pdf"), "application/pdf");
    }

    public async Task<OwnerIssuedDocumentFile?> GetXmlAsync(IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners, Guid documentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owners);

        if (await FindAsync(owners, documentId, withLines: false, ct) is not { } document)
            return null;

        if (string.IsNullOrEmpty(document.FatturaPaXml))
            throw XmlUnavailable(documentId);

        return new OwnerIssuedDocumentFile(Encoding.UTF8.GetBytes(document.FatturaPaXml), FileNameOf(document, ".xml"), "application/xml");
    }

    public async Task<AccountantPackageResult> BuildAsync(
        IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners,
        AccountantPackagePeriod period,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfZero(owners.Count);

        var asked = owners.Distinct().OrderBy(o => o.OrgId).ThenBy(o => o.OwnerId).ToList();
        var limits = options.Value;
        var t = new PackageTexts(documentTexts, PackageTexts.PackageLanguage);
        var year = period.Kind == AccountantPackagePeriodKind.Year;

        await RequireOwnersAsync(asked, ct);
        var profiles = await ProfilesOfAsync(asked, ct);

        // A period with more documents than the caps allow is refused before one is read: each document is at least one file.
        var count = await IssuedBy([.. profiles.Keys], period).CountAsync(ct);
        if (year)
        {
            foreach (var owner in asked)
                count += await InvoicesOfTheAgencyTo(owner, period).CountAsync(ct);
        }

        AccountantPackageService.CheckCaps(count, limits);

        var plan = new AccountantPackageService.Plan();
        for (var index = 0; index < asked.Count; index++)
        {
            var owner = asked[index];
            var profileId = profiles.Where(p => p.Value == owner).Select(p => (Guid?)p.Key).FirstOrDefault();
            await PlanOwnerAsync(plan, owner, profileId, asked.Count > 1 ? index + 1 : null, period, t, ct);
        }

        AccountantPackageService.CheckCaps(plan.DocumentFiles.Count, limits);

        var written = await AccountantPackageService.WriteAsync(plan, period, [], [], t, destination, ct);
        logger.LogInformation(
            "Owner issued documents of {Period} written. Owners={Owners} Documents={Documents} Files={Files} Bytes={Bytes}",
            period.Key,
            asked.Count,
            plan.Documents,
            written.Files,
            written.Bytes);
        return new AccountantPackageResult(plan.Documents, written.Files, written.Bytes, []);
    }

    // ---------------------------------------------------------------- what goes in the ZIP

    /// <summary>
    /// The files of one owner: the register of the documents issued in its name and the documents (always), and for a year their summary and the invoices of the agency to the owner with their
    /// recharges. <paramref name="number"/> is the place of the owner among several (each is then a folder of its own), <c>null</c> for one.
    /// </summary>
    private async Task PlanOwnerAsync(
        AccountantPackageService.Plan plan,
        OwnerIssuedDocumentsOwner owner,
        Guid? profileId,
        int? number,
        AccountantPackagePeriod period,
        PackageTexts t,
        CancellationToken ct)
    {
        var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == owner.OrgId, ct);
        var profile = profileId is { } id
            ? await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;
        var ownerName = await OwnerNameAsync(owner, ct);

        // The issuer of a document that has no snapshot (and of an empty period): the owner as its profile says now; an owner with no profile has no document, and is told by its name.
        var fallback = profile is null ? new FiscalIssuerSnapshot { LegalName = ownerName } : FiscalIssuerSnapshot.From(profile, org);
        var current = AccountantPackageFacts.IssuerOf(fallback);
        var documents = profileId is { } issuing ? await Project(IssuedBy([issuing], period)).ToListAsync(ct) : [];

        var issuers = AccountantPackageService.IssuersOf(documents, [], fallback, current);
        var slugs = AccountantPackageService.SlugsOf(issuers);
        var prefix = number is { } place ? $"proprietario-{place.ToString(CultureInfo.InvariantCulture)}-{slugs[current.Key]}/" : string.Empty;
        var folder = prefix + OwnerIssuedDocumentsStructure.IssuedFolder;

        foreach (var issuer in issuers)
        {
            var mine = documents.Where(d => AccountantPackageFacts.IssuerOf(d.IssuerSnapshot, fallback).Key == issuer.Key).ToList();
            IReadOnlyList<ReportTable> tables = [.. AccountantPackageDocuments.Register(mine, ownerInvoices: false, issuer, period, t), OwnerIssuedDocumentTables.PeriodTotals(mine, t)];
            plan.AddTables(folder, "registro-" + slugs[issuer.Key], tables);
        }

        foreach (var document in documents)
            PlanDocumentFiles(plan, folder, owner.OrgId, document, agencyInvoice: false, t);

        plan.Documents += documents.Count;
        if (period.Kind != AccountantPackagePeriodKind.Year)
            return;

        // The year: what was issued in the owner's name, month by month, and what the agency invoiced to the owner (its commission, its recharges and the expenses it advanced).
        var invoices = await Project(InvoicesOfTheAgencyTo(owner, period)).ToListAsync(ct);
        plan.AddTables(prefix + OwnerIssuedDocumentsStructure.AnnualSummaryFolder, "documenti-emessi", OwnerIssuedDocumentTables.AnnualSummary(documents, period, t));
        plan.AddTables(prefix + OwnerIssuedDocumentsStructure.AnnualSummaryFolder, "fatture-agenzia-e-riaddebiti", OwnerIssuedDocumentTables.AgencyInvoices(invoices, period, t));

        var invoicesFolder = prefix + OwnerIssuedDocumentsStructure.AgencyInvoicesFolder;
        plan.AddTables(invoicesFolder, "registro", AccountantPackageDocuments.Register(invoices, ownerInvoices: true, current, period, t));
        foreach (var invoice in invoices)
            PlanDocumentFiles(plan, invoicesFolder, owner.OrgId, invoice, agencyInvoice: true, t);

        plan.Documents += invoices.Count;
    }

    /// <summary>The PDF of a document CasaZen issued and its XML when it has one: the files the screens download, made when their turn comes.</summary>
    private void PlanDocumentFiles(AccountantPackageService.Plan plan, string folder, Guid orgId, PackageDocument document, bool agencyInvoice, PackageTexts t)
    {
        var stem = $"{DocumentArchiveExport.Date(document.IssueDate)}-{DocumentArchiveExport.Sanitize(document.Number ?? document.Id.ToString("N"))}";
        var label = $"{AccountantPackageDocuments.KindLabel(document.Kind, t)} {document.Number}".Trim();
        var id = document.Id;
        Func<CancellationToken, Task<byte[]>> pdf = agencyInvoice ? token => AgencyInvoicePdfAsync(orgId, id, token) : token => IssuedPdfAsync(orgId, id, token);
        plan.AddDocumentFile(folder, stem, ".pdf", $"{label} (PDF)", pdf);
        if (document.HasXml)
        {
            plan.AddDocumentFile(
                folder,
                stem,
                ".xml",
                $"{label} (XML FatturaPA)",
                async token => Encoding.UTF8.GetBytes(await XmlOfAsync(orgId, id, token)));
        }
    }

    // ---------------------------------------------------------------- what is read

    /// <summary>
    /// The issuer profile of each owner asked for, by the id of the profile: the profile of an owner of that org (<c>PropertyOwnerId</c>), never the organization's. An owner that has no
    /// profile issues nothing and is not in the answer.
    /// </summary>
    internal async Task<Dictionary<Guid, OwnerIssuedDocumentsOwner>> ProfilesOfAsync(IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners, CancellationToken ct)
    {
        if (owners.Count == 0)
            return [];

        var ownerIds = owners.Select(o => o.OwnerId).Distinct().ToList();
        var orgIds = owners.Select(o => o.OrgId).Distinct().ToList();
        var found = await db.OrgFiscalIssuerProfiles
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => p.PropertyOwnerId != null && ownerIds.Contains(p.PropertyOwnerId.Value) && orgIds.Contains(p.OrgId))
            .Select(p => new { p.Id, p.OrgId, OwnerId = p.PropertyOwnerId!.Value })
            .ToListAsync(ct);

        // The pairs, not the two lists: an owner of one agency is not the owner of another that happens to be asked for in the same breath.
        var wanted = owners.ToHashSet();
        return found
            .Where(p => wanted.Contains(new OwnerIssuedDocumentsOwner(p.OrgId, p.OwnerId)))
            .ToDictionary(p => p.Id, p => new OwnerIssuedDocumentsOwner(p.OrgId, p.OwnerId));
    }

    /// <summary>
    /// The documents issued on the issuer profiles <paramref name="profileIds"/> (an empty list is no document, never all): the issued ones of a stay that CasaZen made, a credit note
    /// included, a draft, a discarded one and a proforma never; the ones issued in <paramref name="period"/> when it is given.
    /// </summary>
    internal IQueryable<FiscalDocument> IssuedBy(IReadOnlyCollection<Guid> profileIds, AccountantPackagePeriod? period)
    {
        var query = db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.IssuerProfileId != null && profileIds.Contains(d.IssuerProfileId.Value)
                        && d.Purpose == FiscalDocumentPurpose.GuestStay && d.Source == FiscalDocumentSource.CasaZen
                        && d.Status == FiscalDocumentStatus.Issued && d.Kind != FiscalDocumentKind.Proforma
                        && d.IssueDate != null && d.Number != null);
        if (period is null)
            return query;

        var from = period.From;
        var to = period.To;
        return query.Where(d => d.IssueDate >= from && d.IssueDate <= to);
    }

    /// <summary>What the list shows of a document, with the issuer profile it was issued on (the owner it is issued in the name of).</summary>
    internal sealed record IssuedRow(
        Guid Id,
        Guid OrgId,
        Guid ProfileId,
        string? Number,
        DateOnly IssueDate,
        FiscalDocumentKind Kind,
        decimal Total,
        string Currency,
        string SdiStatus,
        bool HasXml);

    /// <summary>The rows of the list: the documents of <see cref="IssuedBy"/>, the newest first (the day, then the series and the number), at most <paramref name="limit"/>.</summary>
    internal IQueryable<IssuedRow> ListRows(IReadOnlyCollection<Guid> profileIds, AccountantPackagePeriod? period, int limit)
    {
        return IssuedBy(profileIds, period)
            .OrderByDescending(d => d.IssueDate)
            .ThenByDescending(d => d.Series)
            .ThenByDescending(d => d.SequenceNumber)
            .ThenBy(d => d.Id)
            .Take(limit)
            .Select(d => new IssuedRow(
                d.Id,
                d.OrgId,
                d.IssuerProfileId!.Value,
                d.Number,
                d.IssueDate!.Value,
                d.Kind,
                d.Total,
                d.Currency,
                d.SdiStatus,
                d.FatturaPaXml != null));
    }

    /// <summary>One document of <see cref="IssuedBy"/>, as stored, with its lines when asked.</summary>
    internal IQueryable<FiscalDocument> OneOf(IReadOnlyCollection<Guid> profileIds, Guid documentId, bool withLines)
    {
        var query = IssuedBy(profileIds, null).Where(d => d.Id == documentId);
        return withLines ? query.Include(d => d.Lines) : query;
    }

    /// <summary>The invoices (and credit notes) the agency issued to the owner on a day of the period: the documents with the purpose owner invoice made out to it.</summary>
    internal IQueryable<FiscalDocument> InvoicesOfTheAgencyTo(OwnerIssuedDocumentsOwner owner, AccountantPackagePeriod period)
    {
        var orgId = owner.OrgId;
        var ownerId = owner.OwnerId;
        var from = period.From;
        var to = period.To;
        return db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.RecipientOwnerId == ownerId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Source == FiscalDocumentSource.CasaZen
                        && d.Status == FiscalDocumentStatus.Issued && d.Kind != FiscalDocumentKind.Proforma
                        && d.IssueDate != null && d.IssueDate >= from && d.IssueDate <= to);
    }

    /// <summary>The documents as the accountant package reads them (what each stores, with its lines), oldest first: the day of the issue, then the series and the number.</summary>
    internal IQueryable<PackageDocument> Project(IQueryable<FiscalDocument> issued)
    {
        // The subquery is built outside the projection: an expression tree cannot hold the list of the filters to ignore.
        var all = db.FiscalDocuments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
        return issued
            .OrderBy(d => d.IssueDate)
            .ThenBy(d => d.Series)
            .ThenBy(d => d.SequenceNumber)
            .ThenBy(d => d.Id)
            .Select(d => new PackageDocument(
                d.Id,
                d.Purpose,
                d.Kind,
                d.Source,
                d.Number,
                d.IssueDate!.Value,
                d.Currency,
                d.IssuerSnapshot,
                d.RecipientName,
                d.RecipientOwnerId,
                d.PropertyId,
                d.PaymentChannel,
                d.TaxableBase,
                d.VatAmount,
                d.StampDuty,
                d.Total,
                d.VatRatePercent,
                d.VatNatureCode,
                d.PeriodYear,
                d.PeriodMonth,
                all.Where(c => c.OrgId == d.OrgId && c.Id == d.CreditedDocumentId).Select(c => (FiscalDocumentKind?)c.Kind).FirstOrDefault(),
                d.FatturaPaXml != null,
                d.Lines
                    .OrderBy(l => l.LineNumber)
                    .Select(l => new PackageDocumentLine(l.LineNumber, l.LineType, l.TotalPrice, l.VatRatePercent, l.VatNatureCode, l.VatAmount))
                    .ToList()));
    }

    /// <summary>
    /// One document issued in the name of one of <paramref name="owners"/>, as stored, with its lines when asked; <c>null</c> for anything else (the same answer for a document of another
    /// owner, of the agency, a draft and one that does not exist).
    /// </summary>
    private async Task<FiscalDocument?> FindAsync(IReadOnlyCollection<OwnerIssuedDocumentsOwner> owners, Guid documentId, bool withLines, CancellationToken ct)
    {
        var profiles = await ProfilesOfAsync(owners, ct);
        if (profiles.Count == 0)
            return null;

        var document = await OneOf([.. profiles.Keys], documentId, withLines).FirstOrDefaultAsync(ct);
        return document?.IssuerProfileId is { } profileId && profiles.TryGetValue(profileId, out var owner) && owner.OrgId == document.OrgId ? document : null;
    }

    /// <summary>A document of the org, as stored, with its lines (the ZIP asks for the ones it listed).</summary>
    private async Task<FiscalDocument> LoadAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        return await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Include(d => d.Lines)
            .FirstAsync(d => d.Id == documentId && d.OrgId == orgId, ct);
    }

    /// <summary>The owners exist, each in its org (404 <c>owner_not_found</c>); an anonymized owner is still there, its documents being kept for the legal retention.</summary>
    private async Task RequireOwnersAsync(IReadOnlyList<OwnerIssuedDocumentsOwner> owners, CancellationToken ct)
    {
        var ownerIds = owners.Select(o => o.OwnerId).Distinct().ToList();
        var orgIds = owners.Select(o => o.OrgId).Distinct().ToList();
        var found = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => ownerIds.Contains(o.Id) && orgIds.Contains(o.OrgId))
            .Select(o => new { o.Id, o.OrgId })
            .ToListAsync(ct);
        var existing = found.Select(o => new OwnerIssuedDocumentsOwner(o.OrgId, o.Id)).ToHashSet();
        if (owners.Any(o => !existing.Contains(o)))
        {
            var missing = owners.First(o => !existing.Contains(o));
            throw new NotFoundException($"Owner {missing.OwnerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };
        }
    }

    /// <summary>The name the agency keeps for the owner (the legal name, else first and last name); <c>null</c> for an owner whose data was anonymized.</summary>
    private async Task<string?> OwnerNameAsync(OwnerIssuedDocumentsOwner owner, CancellationToken ct)
    {
        var orgId = owner.OrgId;
        var ownerId = owner.OwnerId;
        var found = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && o.Id == ownerId)
            .Select(o => new { o.FirstName, o.LastName, o.LegalName, Anonymized = o.AnonymizedAt != null })
            .FirstOrDefaultAsync(ct);
        if (found is null || found.Anonymized)
            return null;

        return !string.IsNullOrWhiteSpace(found.LegalName) ? found.LegalName.Trim() : $"{found.FirstName} {found.LastName}".Trim();
    }

    // ---------------------------------------------------------------- the files

    /// <summary>
    /// The PDF of an issued document: the issuer of the snapshot of the issue (the owner's profile of today only for a document issued before the snapshot existed), the payment state the
    /// current one, the name of the property of the stay; the very content <c>FiscalDocumentService</c> renders for the agency.
    /// </summary>
    private async Task<byte[]> PdfOfAsync(FiscalDocument document, string? language, CancellationToken ct)
    {
        var orgId = document.OrgId;
        await ApplyCurrentPaymentStateAsync(document, ct);
        var credited = document.CreditedDocumentId is { } creditedId
            ? await db.FiscalDocuments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().FirstOrDefaultAsync(d => d.Id == creditedId && d.OrgId == orgId, ct)
            : null;
        var issuer = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot) ?? await CurrentIssuerOfAsync(document, ct);
        var propertyName = document.PropertyId is { } propertyId
            ? await db.Properties
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
                .AsNoTracking()
                .Where(p => p.Id == propertyId && p.OrgId == orgId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct)
            : null;

        return pdfRenderer.Render(FiscalDocumentPdfs.Pdf(document, issuer, documentTexts, DocumentLanguages.Normalize(language), credited, propertyName));
    }

    /// <summary>The payment state of a document of CasaZen is the current one, read from the payments of its booking: the PDF the owner gets never says «to collect» of what was paid.</summary>
    private async Task ApplyCurrentPaymentStateAsync(FiscalDocument document, CancellationToken ct)
    {
        if (!FiscalDocumentService.IsPaymentStateReadFromPayments(document.Source, document.Kind, document.BookingId, document.PaymentChannel))
            return;

        var bookingId = document.BookingId!.Value;
        var collected = await FiscalDocumentService.CollectedByBookingAsync(db, [bookingId], ct, document.OrgId);
        document.PaymentState = FiscalDocumentService.PaymentStateOf(collected.GetValueOrDefault(bookingId), document.Total);
    }

    private async Task<FiscalIssuerSnapshot> CurrentIssuerOfAsync(FiscalDocument document, CancellationToken ct)
    {
        var orgId = document.OrgId;
        var profile = document.IssuerProfileId is { } profileId
            ? await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId && p.OrgId == orgId, ct)
            : null;
        var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
        return FiscalIssuerSnapshot.From(profile, org);
    }

    /// <summary>The XML of an issued document of the org, as stored at the issue.</summary>
    private async Task<string> XmlOfAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        var xml = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.Id == documentId && d.OrgId == orgId)
            .Select(d => d.FatturaPaXml)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(xml) ? throw XmlUnavailable(documentId) : xml;
    }

    /// <summary>The PDF of a document of the org the ZIP listed (the ones issued in the owner's name), in the language of the package.</summary>
    private async Task<byte[]> IssuedPdfAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        return await PdfOfAsync(await LoadAsync(orgId, documentId, ct), PackageTexts.PackageLanguage, ct);
    }

    /// <summary>The PDF of an invoice of the agency to the owner: the one the owner downloads in its portal.</summary>
    private async Task<byte[]> AgencyInvoicePdfAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        var file = await statementDocuments.GetOwnerInvoicePdfAsync(orgId, documentId, PackageTexts.PackageLanguage, ct)
            ?? throw new NotFoundException($"Owner invoice {documentId} was not found") { Code = OwnerIssuedDocumentsErrors.NotFound, MessageKey = OwnerIssuedDocumentsErrors.NotFoundMessageKey };
        return file.Content;
    }

    /// <summary>The name a file of a document is offered under: what it is and its number, made safe (the number has a slash): <c>fattura-2026-12.pdf</c>.</summary>
    private static string FileNameOf(FiscalDocument document, string extension)
    {
        var word = document.Kind switch
        {
            FiscalDocumentKind.CreditNote => "nota-di-credito",
            FiscalDocumentKind.Receipt => "ricevuta",
            _ => "fattura",
        };
        return OwnerFileRules.SafeFileName($"{word}-{document.Number?.Replace('/', '-')}", extension, FileNameMaxLength);
    }

    private static NotFoundException XmlUnavailable(Guid documentId)
    {
        return new NotFoundException($"Fiscal document {documentId} has no XML") { Code = FiscalDocumentErrors.XmlUnavailable, MessageKey = "FiscalDocumentXmlUnavailable" };
    }
}

/// <summary>
/// The tables of the ZIP of the documents issued in the name of an owner that the accountant package has not (E17/F02/US01/T01, acceptance step D2): the totals of the period with the stamp
/// duty, the annual summary of the documents and the invoices of the agency to the owner with their recharges. The words are the resource files' (<see cref="PackageTexts"/>); an amount is a
/// number to the cent in the currency of its own column, a credit note is negative and nothing is worked out again: every figure is added up from what the documents store.
/// </summary>
internal static class OwnerIssuedDocumentTables
{
    /// <summary>The documents, the taxable amount, the VAT, the stamp duty and the total of a set of documents for each currency (credit notes taken away): the figures the register adds up to.</summary>
    public static ReportTable PeriodTotals(IReadOnlyList<PackageDocument> documents, PackageTexts t)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(t);

        var table = new ReportTable(
            t[PackageLabels.OwnDocsTotalsTitle],
            [t["FiscalExportColCurrency"], t[PackageLabels.ColDocuments], t["FiscalExportColTaxableBase"], t["FiscalExportColVat"], t["FiscalExportColStampDuty"], t["FiscalExportColTotal"]]);
        foreach (var group in documents.GroupBy(d => d.Currency).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            table.Add(
                group.Key,
                group.Count(),
                ReportCell.Money(group.Sum(d => d.Sign * d.TaxableBase)),
                ReportCell.Money(group.Sum(d => d.Sign * d.VatAmount)),
                ReportCell.Money(group.Sum(d => d.Sign * d.StampDuty)),
                ReportCell.Money(group.Sum(d => d.Sign * d.Total)));
        }

        return table;
    }

    /// <summary>
    /// The annual summary of the documents issued in the owner's name: for each month, kind and currency how many and what they add up to, then the totals of the year and the VAT for each rate
    /// and nature (the base of the VAT of the owner).
    /// </summary>
    public static IReadOnlyList<ReportTable> AnnualSummary(IReadOnlyList<PackageDocument> documents, AccountantPackagePeriod period, PackageTexts t)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(t);

        var months = new ReportTable(
            t.Get(PackageLabels.OwnDocsSummaryTitle, period.Key),
            [
                t[PackageLabels.ColPeriod],
                t["FiscalExportColKind"],
                t["FiscalExportColCurrency"],
                t[PackageLabels.ColDocuments],
                t["FiscalExportColTaxableBase"],
                t["FiscalExportColVat"],
                t["FiscalExportColStampDuty"],
                t["FiscalExportColTotal"],
            ]);
        foreach (var group in documents
                     .GroupBy(d => (Month: AccountantPackageFacts.MonthOf(d.IssueDate), d.Kind, d.Currency))
                     .OrderBy(g => g.Key.Month, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Kind)
                     .ThenBy(g => g.Key.Currency, StringComparer.Ordinal))
        {
            months.Add(
                group.Key.Month,
                AccountantPackageDocuments.KindLabel(group.Key.Kind, t),
                group.Key.Currency,
                group.Count(),
                ReportCell.Money(group.Sum(d => d.Sign * d.TaxableBase)),
                ReportCell.Money(group.Sum(d => d.Sign * d.VatAmount)),
                ReportCell.Money(group.Sum(d => d.Sign * d.StampDuty)),
                ReportCell.Money(group.Sum(d => d.Sign * d.Total)));
        }

        return [months, PeriodTotals(documents, t), AccountantPackageDocuments.RateTotals(documents, t)];
    }

    /// <summary>
    /// The invoices of the agency to the owner in the year and their recharges: for each currency the commission, the recharges (with VAT) and the expenses advanced (outside the VAT) the invoices
    /// say, then the same for each invoice, then the VAT for each rate; credit notes are taken away. The commission, recharges and expenses are the lines of the invoices of each type.
    /// </summary>
    public static IReadOnlyList<ReportTable> AgencyInvoices(IReadOnlyList<PackageDocument> invoices, AccountantPackagePeriod period, PackageTexts t)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(t);

        var currency = t["FiscalExportColCurrency"];
        var totals = new ReportTable(
            t.Get(PackageLabels.OwnDocsAgencyInvoicesTitle, period.Key),
            [
                currency,
                t[PackageLabels.ColDocuments],
                t[PackageLabels.SumCommissionTaxable],
                t[PackageLabels.SumCommissionVat],
                t[PackageLabels.SumCommissionTotal],
                t[PackageLabels.SumRechargeTaxable],
                t[PackageLabels.SumRechargeVat],
                t[PackageLabels.SumRechargeTotal],
                t[PackageLabels.SumAdvanced],
                t[PackageLabels.SumInvoiced],
            ]);
        foreach (var row in AgencySummaryService.CommissionRows(invoices, new Dictionary<Guid, string?>()))
        {
            totals.Add(
                row.Currency,
                row.Documents,
                ReportCell.Money(row.Commission.Taxable),
                ReportCell.Money(row.Commission.Vat),
                ReportCell.Money(row.Commission.Total),
                ReportCell.Money(row.Recharges.Taxable),
                ReportCell.Money(row.Recharges.Vat),
                ReportCell.Money(row.Recharges.Total),
                ReportCell.Money(row.AdvancedExpenses),
                ReportCell.Money(row.InvoicedTotal));
        }

        var detail = new ReportTable(
            t[PackageLabels.OwnDocsAgencyInvoiceDetailTitle],
            [
                t["FiscalExportColNumber"],
                t["FiscalExportColIssueDate"],
                t["FiscalExportColKind"],
                currency,
                t[PackageLabels.SumCommissionTaxable],
                t[PackageLabels.SumRechargeTaxable],
                t[PackageLabels.SumRechargeVat],
                t[PackageLabels.SumAdvanced],
                t[PackageLabels.SumInvoiced],
            ]);
        foreach (var invoice in invoices)
        {
            var recharges = invoice.Lines.Where(l => l.LineType == FiscalLineType.Recharge).ToList();
            detail.Add(
                invoice.Number,
                ReportCell.Date(invoice.IssueDate),
                AccountantPackageDocuments.KindLabel(invoice.Kind, t),
                invoice.Currency,
                ReportCell.Money(invoice.Sign * invoice.Lines.Where(l => l.LineType == FiscalLineType.Commission).Sum(l => l.TotalPrice)),
                ReportCell.Money(invoice.Sign * recharges.Sum(l => l.TotalPrice)),
                ReportCell.Money(invoice.Sign * recharges.Sum(l => AccountantPackageFacts.VatOfLine(l))),
                ReportCell.Money(invoice.Sign * invoice.Lines.Where(l => l.LineType == FiscalLineType.AdvancedExpense).Sum(l => l.TotalPrice)),
                ReportCell.Money(invoice.Sign * invoice.Total));
        }

        return [totals, detail, AccountantPackageDocuments.RateTotals(invoices, t)];
    }
}
