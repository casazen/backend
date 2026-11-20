using System.Globalization;
using System.Security.Cryptography;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IOwnerStatementDocuments"/> (E13/F01/US02/T01, E13/F01/US02/T02, NF-110, NF-111, NF-115): what a statement version says, its PDF and CSV, the PDF stored
/// at the publication as an owner document and read by the portal, and the e-mail that tells the owner. The layout is <see cref="OwnerStatementLayout"/>; this
/// class reads the lines and the facts around them and stores and hands out the file.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The lines of the version.</b> Its ledger lines (<see cref="OwnerAccountingService.LinesOf"/>: the ones of the owner's month posted up to the cut-off), grouped
/// by type. The totals are the version's own: before a file is made the lines are summed and every part is compared with what the version stores, so a file never
/// shows a net the version does not have (409 <c>owner_ledger_immutable</c>).</item>
/// <item><b>Facts around a line.</b> The stay of a line (a booking: from its source, or from the booking a payment is of) gives the days, the channel and the code;
/// the property its name. A line whose booking is gone keeps the days of the ledger line.</item>
/// <item><b>Tourist tax box.</b> The tax of the stays of the statement (the stays with revenue in it) that the agency collects: <see cref="Booking.TouristTax"/> of
/// each, less the stays whose channel collects it itself (a channel of the list the guest documents use for the issuer: the one it confirmed, else the shipped default of a Swiss
/// issuer, <see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>; or the collector of the stay as the host's tourist tax decides:
/// the active rule of the channel in the comune, else the one the host declared, else the one confirmed with the calculation), and only for a stay that owes the tax
/// (an active booking that is not a no-show). For information only, in the currency of the property, never in a total.</item>
/// <item><b>Who holds the portal account.</b> The channel fee of a stay is flagged as recharged by the agency when the invoice of the property manager recharges it:
/// the agency holds the account of the listing on that portal and the organization recharges the commissions of the accounts it holds
/// (<see cref="PortalAccountHolders.RechargesCommissionOf"/> of <see cref="IPortalAccountHolderResolver"/>, E14, ⚖ I10 I11, the very test of the invoice), so the
/// label «riaddebitate dal gestore (fattura n. …)» never names an invoice without that recharge. The commission of an account of the agency that the organization does not
/// recharge is the agency's (decision 1 of 20/11/2026, FIX/BE-V2-NO-RECHARGE): the posting wrote it as <see cref="OwnerLedgerEntryType.ChannelFeeBorneByAgency"/>, a line for
/// information only that the statement shows under a label of its own («sostenuta dall'agenzia, non addebitata al proprietario») and counts in no total; it is read from the
/// line, never from the setting of today. A channel fee the owner was charged on such an account (posted before that decision, or before the posting that follows a change of
/// the setting) keeps its amount in the totals and is told as what it is (<see cref="OwnerStatementLine.AgencyAccountChargedWithoutRecharge"/>), so a statement made from it
/// never reads otherwise than it was made. Only an account of the owner (V1) is «Trattenuta dal portale (fattura del portale a tuo nome)». A service built without the
/// resolver reads every listing as V1.</item>
/// <item><b>Language.</b> There is no language kept for an owner yet: Italian unless the caller asks for English. The stored PDF is in Italian.</item>
/// <item><b>The notice of the publication.</b> The e-mail that tells the owner is queued after the publication was saved. A notice that cannot be prepared or queued never
/// turns the publication into an error, and is never silent: it is logged with its ids and the agency gets a «To do» (<see cref="ActivityType.OwnerStatementNoticeFailed"/>,
/// once for each version) to tell the owner by hand.</item>
/// <item><b>Logs</b> carry ids and counts: never a name, an amount or a line.</item>
/// </list>
/// </remarks>
public sealed class OwnerStatementDocuments(
    AppDbContext db,
    IPdfDocumentRenderer pdf,
    IDocumentTexts texts,
    IFileStorage storage,
    IOwnerFileScanner scanner,
    IFeatureFlags featureFlags,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IInternalActivityService activities,
    TimeProvider time,
    ILogger<OwnerStatementDocuments> logger,
    IPortalAccountHolderResolver? holders = null,
    IGuestDocumentLineBuilder? documentLines = null,
    AgencyDomainLinks? agencyLinks = null) : IOwnerStatementDocuments
{
    /// <summary>The entity type of the «To do» about a statement whose owner could not be told that it was published.</summary>
    public const string OwnerStatementVersionEntityType = "OwnerStatementVersion";

    /// <summary>The line of that «To do»: a code, never a name or an amount.</summary>
    public const string NoticeFailedReason = "owner_statement_notice_failed";

    public async Task<OwnerStatementContent> GetContentAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default)
    {
        var version = await VersionAsync(orgId, versionId, ct);
        var entries = await EntriesAsync(version, ct);
        return await ContentAsync(version, entries, DocumentLanguages.Normalize(language), ct);
    }

    public async Task<OwnerStatementFile> GetPdfAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default)
    {
        var version = await VersionAsync(orgId, versionId, ct);

        // A published version has its file: exactly what the owner downloads, in the language it was published in.
        if (version.PublishedAt is not null && await StoredFileAsync(orgId, versionId, ct) is { } file)
        {
            if (await ReadStoredAsync(file, ct) is { } bytes)
                return new OwnerStatementFile(bytes, file.FileName, file.ContentType);

            logger.LogError("Owner statement version {VersionId}: the stored PDF {FileId} cannot be handed out as it was published, it is built again from the lines", versionId, file.Id);
        }

        var entries = await EntriesAsync(version, ct);
        var content = await ContentAsync(version, entries, DocumentLanguages.Normalize(language), ct);
        return new OwnerStatementFile(pdf.Render(OwnerStatementLayout.Pdf(content, texts)), FileName(version, ".pdf"), "application/pdf");
    }

    public async Task<OwnerStatementFile> GetCsvAsync(Guid orgId, Guid versionId, string? language = null, CancellationToken ct = default)
    {
        var version = await VersionAsync(orgId, versionId, ct);
        var entries = await EntriesAsync(version, ct);
        var content = await ContentAsync(version, entries, DocumentLanguages.Normalize(language), ct);
        return new OwnerStatementFile(OwnerStatementLayout.Csv(content, texts), FileName(version, ".csv"), "text/csv");
    }

    public async Task<StoredStatementPdf> StorePublishedPdfAsync(
        OwnerStatementVersion version,
        IReadOnlyList<OwnerLedgerEntry> entries,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(entries);

        // One file per version: the document is the version's id, so a version that has it stores nothing more.
        if (await db.OwnerDocuments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AnyAsync(d => d.Id == version.Id, ct))
            return StoredStatementPdf.None;

        var content = await ContentAsync(version, entries, DocumentLanguages.Italian, ct);
        var bytes = pdf.Render(OwnerStatementLayout.Pdf(content, texts));
        var verdict = scanner.Scan(bytes);
        if (!verdict.Clean)
        {
            var code = verdict.RejectionCode ?? OwnerFileRules.TypeNotAllowed;
            logger.LogError("Owner statement version {VersionId}: the generated PDF was refused by the scan ({Engine}): {Code}", version.Id, scanner.Engine, code);
            throw new DomainRuleException(code, OwnerFileRules.MessageKeyOf(code));
        }

        var now = Now();
        var actor = string.IsNullOrWhiteSpace(userId) ? "system" : userId;
        var fileId = Guid.NewGuid();
        var key = StorageKeys.OwnerDocumentFile(version.OrgId, version.Id, $"{fileId:N}{verdict.Extension}");
        var title = Truncate(texts.Get("StatementFileDocumentTitle", content.Language, PeriodLabel(version), version.VersionNumber), OwnerDocument.TitleMaxLength);

        using (var stream = new MemoryStream(bytes, writable: false))
            await storage.PutAsync(StorageBucket.Private, StorageKeys.EnsureValid(key), stream, verdict.ContentType!, ct);

        db.OwnerDocuments.Add(new OwnerDocument
        {
            Id = version.Id,
            OrgId = version.OrgId,
            PropertyOwnerId = version.PropertyOwnerId,
            PropertyId = null,
            Type = OwnerDocumentType.Statement,
            Title = title,
            CreatedAt = now,
            CreatedByUserId = actor,
            UpdatedAt = now,
        });
        db.OwnerDocumentVersions.Add(new OwnerDocumentVersion
        {
            Id = fileId,
            OrgId = version.OrgId,
            OwnerDocumentId = version.Id,
            VersionNumber = 1,
            StorageKey = key,
            FileName = OwnerFileRules.SafeFileName(FileName(version, ".pdf"), verdict.Extension!, OwnerDocumentVersion.FileNameMaxLength),
            ContentType = verdict.ContentType!,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ScanEngine = scanner.Engine,
            UploadedAt = now,
            UploadedByUserId = actor,
        });

        logger.LogInformation("Owner statement version {VersionId}: the PDF {FileId} is stored ({Bytes} bytes)", version.Id, fileId, bytes.LongLength);
        return new StoredStatementPdf(key);
    }

    public async Task DiscardStoredPdfAsync(StoredStatementPdf stored, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.StorageKey is not { } key)
            return;

        try
        {
            await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The save that failed is the error to report; a file left in the bucket is told, not hidden.
            logger.LogWarning(ex, "The stored owner statement PDF {StorageKey} could not be removed after a failed save", key);
        }
    }

    public async Task<OwnerFileContent?> OpenPublishedPdfAsync(Guid orgId, Guid versionId, Guid ownerUserId, CancellationToken ct = default)
    {
        // The agency can withdraw a document from the portal (RevokedAt): the owner stops seeing it at the next request, a statement PDF included.
        var document = await db.OwnerDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == versionId && d.OrgId == orgId && d.Type == OwnerDocumentType.Statement && d.RevokedAt == null, ct);
        if (document is null || await StoredFileAsync(orgId, versionId, ct) is not { } file)
            return null;

        // What the owner gets is what was published: a file that is missing, or no longer has the SHA-256 kept at the publication, is not handed out.
        var bytes = await ReadStoredAsync(file, ct);
        if (bytes is null)
            return null;

        // The download is written before the file is handed out, as every download of an owner document is.
        db.OwnerDocumentDownloads.Add(new OwnerDocumentDownload
        {
            OrgId = orgId,
            OwnerDocumentId = document.Id,
            OwnerDocumentVersionId = file.Id,
            DownloadedAt = Now(),
            OwnerUserId = ownerUserId,
        });
        await db.SaveChangesAsync(ct);

        return new OwnerFileContent(new MemoryStream(bytes, writable: false), file.FileName, file.ContentType);
    }

    public async Task TellOwnerPublishedAsync(OwnerStatementVersion version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        // The e-mail points to the owner portal, which is behind Features:AgencyTier: with the flag off nobody is told.
        if (!featureFlags.IsEnabled(FeatureFlags.AgencyTier))
        {
            logger.LogInformation("Owner statement version {VersionId}: the owner portal is off, nobody is told", version.Id);
            return;
        }

        // The statement is published whatever happens here, so a notice that fails is never turned into an error of the publication, and never left silent either: it is
        // logged with its ids and handed to the agency as a To do, which tells the owner by hand.
        var told = false;
        try
        {
            told = await QueueNoticeAsync(version, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Owner statement version {VersionId} of owner {OwnerId} (org {OrgId}) is published, the e-mail that tells the owner could not be prepared", version.Id, version.PropertyOwnerId, version.OrgId);
        }

        if (!told)
            await RaiseNoticeFailedAsync(version, ct);
    }

    /// <summary>
    /// Queues the e-mail for the people of the owner portal who read the statements of the owner. <c>true</c> when everyone was told or there is nobody to tell; <c>false</c>
    /// when someone could not be (no public address to link to, or an e-mail the queue refused).
    /// </summary>
    private async Task<bool> QueueNoticeAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        // The people who read the statements of the owner: the owner on its ownerships in force, and the delegated contacts that hold accounting.read.
        var now = time.GetUtcNow().UtcDateTime;
        var owners = await OwnerPortalAccessResolver.InForce(db, now)
            .Where(o => o.OrgId == version.OrgId && o.PropertyOwnerId == version.PropertyOwnerId && o.OwnerUserId != null)
            .Select(o => o.OwnerUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var contacts = await db.OwnerPortalContacts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.OrgId == version.OrgId && c.PropertyOwnerId == version.PropertyOwnerId && c.Status == OwnerPortalContactStatus.Active && c.OwnerUserId != null)
            .Select(c => new { c.OwnerUserId, c.Permissions })
            .ToListAsync(ct);
        var people = owners
            .Concat(contacts.Where(c => c.Permissions.HasFlag(OwnerPortalPermission.AccountingRead)).Select(c => c.OwnerUserId!.Value))
            .Distinct()
            .ToList();
        if (people.Count == 0)
        {
            logger.LogInformation("Owner statement version {VersionId}: nobody in the owner portal to tell", version.Id);
            return true;
        }

        if (!links.IsConfigured)
        {
            logger.LogWarning("Owner statement version {VersionId} of owner {OwnerId} (org {OrgId}): no e-mail, the public address of the web app is not configured", version.Id, version.PropertyOwnerId, version.OrgId);
            return false;
        }

        var addresses = await db.OwnerUsers.AsNoTracking()
            .Where(u => people.Contains(u.Id))
            .Select(u => u.Email)
            .Distinct()
            .ToListAsync(ct);
        var agency = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == version.OrgId)
            .Select(o => new { o.Name, o.DefaultLanguage })
            .FirstOrDefaultAsync(ct);

        // The words are the agency and the link: the amounts, the lines and the stays are in the portal, never in the e-mail. E40/F01/US01/T03: the
        // portal of the agency's own domain only once the owner sign-in works there.
        var accounting = (await agencyLinks.ForOrgOrPlatformAsync(links, version.OrgId, ct)).OwnerPortalAccounting();
        var content = EmailTemplates.OwnerStatementPublished(OrgInvitationEmails.CultureOf(agency?.DefaultLanguage), agency?.Name ?? string.Empty, accounting);
        var queued = 0;
        foreach (var address in addresses)
        {
            if (emailQueue.Enqueue(address, content, EmailTemplates.Names.OwnerStatementPublished))
                queued++;
        }

        logger.LogInformation("Owner statement version {VersionId}: {Queued} of {Addresses} e-mails queued", version.Id, queued, addresses.Count);
        if (queued == addresses.Count)
            return true;

        logger.LogWarning(
            "Owner statement version {VersionId} of owner {OwnerId} (org {OrgId}): {Missing} of {Addresses} e-mails could not be queued",
            version.Id,
            version.PropertyOwnerId,
            version.OrgId,
            addresses.Count - queued,
            addresses.Count);
        return false;
    }

    /// <summary>
    /// The To do that tells the agency the owner could not be told that the statement was published, so it tells the owner by hand: one for each version (raising it again
    /// finds the open one), organization-level, a code as its reason and no personal data.
    /// </summary>
    private async Task RaiseNoticeFailedAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.OwnerStatementNoticeFailed,
                Action = ActivityAction.ReviewOwnerStatement,
                EntityType = OwnerStatementVersionEntityType,
                EntityId = version.Id,
                Urgency = ActivityUrgency.Medium,
                OrgId = version.OrgId,
                Reason = NoticeFailedReason,
            },
            ct);
        logger.LogWarning("Owner statement version {VersionId} of owner {OwnerId} (org {OrgId}): the owner was not told, a To do asks the agency to tell the owner by hand", version.Id, version.PropertyOwnerId, version.OrgId);
    }

    public async Task<OwnerStatementFile?> GetOwnerInvoicePdfAsync(Guid orgId, Guid documentId, string? language = null, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Status == FiscalDocumentStatus.Issued,
                ct);
        if (document is null)
            return null;

        // The issuer as it was at the issue; the profile of today only for a document issued before the snapshot existed.
        var issuer = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot);
        if (issuer is null)
        {
            var profile = await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
            var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
            issuer = FiscalIssuerSnapshot.From(profile, org);
        }

        var credited = document.CreditedDocumentId is { } creditedId
            ? await db.FiscalDocuments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().FirstOrDefaultAsync(d => d.Id == creditedId && d.OrgId == orgId, ct)
            : null;
        var content = FiscalDocumentPdfs.Pdf(document, issuer, texts, DocumentLanguages.Normalize(language), credited);
        var name = OwnerFileRules.SafeFileName("fattura-" + document.Number?.Replace('/', '-'), ".pdf", OwnerDocumentVersion.FileNameMaxLength);
        return new OwnerStatementFile(pdf.Render(content), name, "application/pdf");
    }

    // ---------------------------------------------------------------- the content

    /// <summary>
    /// What the statement says: the lines of the version grouped by part, the totals the version stores (checked against the lines), the invoice of the property
    /// manager when one is linked, and the tourist tax box.
    /// </summary>
    private async Task<OwnerStatementContent> ContentAsync(OwnerStatementVersion version, IReadOnlyList<OwnerLedgerEntry> entries, string language, CancellationToken ct)
    {
        var orgId = version.OrgId;
        var bookingIds = entries.Select(BookingIdOf).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var bookings = (await db.Bookings
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(b => b.OrgId == orgId && bookingIds.Contains(b.Id))
                .Select(b => new BookingFacts(b.Id, b.PropertyId, b.CheckInDate, b.CheckOutDate, b.Source, b.ChannelLabel, b.BookingCode, b.TouristTax, b.Status, b.NoShowAt))
                .ToListAsync(ct))
            .ToDictionary(b => b.Id);
        var propertyIds = entries.Where(e => e.PropertyId != null).Select(e => e.PropertyId!.Value).Concat(bookings.Values.Select(b => b.PropertyId)).Distinct().ToList();
        var properties = (await db.Properties
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Currency, p.Country, p.ComuneIstatCode })
                .ToListAsync(ct))
            .ToDictionary(p => p.Id, p => new PropertyFacts(p.Name, p.Currency, TouristTaxComune.IsItalianCountry(p.Country) ? p.ComuneIstatCode : null));

        // Who holds the account of each listing on each portal (E14, ⚖ I10), read once for the statement.
        var portalAccounts = holders is null ? PortalAccountHolders.Defaults : await holders.ForOrgAsync(orgId, ct);
        var lines = entries
            .Select(entry => LineOf(entry, BookingOf(entry, bookings), properties.GetValueOrDefault(entry.PropertyId ?? Guid.Empty)?.Name, portalAccounts))
            .OrderBy(line => line.Section)
            .ThenBy(line => line.Date)
            .ToList();

        // The totals are the version's: every part of the lines must give what the version stores, or the file would show a net it does not have.
        var totals = OwnerStatementLayout.TotalsOf(version.OpeningBalance, lines);
        if (totals != OwnerStatementTotals.Of(version))
        {
            logger.LogError("Owner statement version {VersionId}: its lines no longer give the totals it stores", version.Id);
            throw new DomainConflictException(OwnerAccountingErrors.LedgerImmutable, "OwnerLedgerImmutable");
        }

        var agency = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var owner = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => o.Id == version.PropertyOwnerId && o.OrgId == orgId)
            .Select(o => new { o.FirstName, o.LastName, o.LegalName })
            .FirstOrDefaultAsync(ct);
        var invoice = await OwnerInvoiceAsync(version, ct);

        return new OwnerStatementContent
        {
            VersionId = version.Id,
            OwnerId = version.PropertyOwnerId,
            AgencyName = agency,
            OwnerName = owner is null ? string.Empty : OwnerFieldRules.DisplayName(new PropertyOwner { FirstName = owner.FirstName, LastName = owner.LastName, LegalName = owner.LegalName }),
            Year = version.PeriodYear,
            Month = version.PeriodMonth,
            VersionNumber = version.VersionNumber,
            Status = version.Status,
            Currency = version.Currency,
            Language = language,
            PublishedAt = version.PublishedAt,
            EntriesSha256 = version.EntriesSha256,
            Totals = totals,
            Lines = lines,
            OwnerInvoiceNumber = invoice?.Number,
            OwnerInvoiceDate = invoice?.IssueDate,
            TouristTax = await TouristTaxAsync(orgId, entries, bookings, properties, ct),
        };
    }

    /// <summary>
    /// The line of a ledger entry: its part, its day, the stay (days, channel, code) and property it is about, and, for the commission a portal kept, whether the
    /// invoice of the property manager recharges it (<paramref name="portalAccounts"/>: the agency holds the account of the listing on that portal and recharges), or the
    /// owner was charged it on an account of the agency that the organization does not recharge now (<see cref="OwnerStatementLine.AgencyAccountChargedWithoutRecharge"/>).
    /// The commission the agency bears is told by the type of the line (<see cref="OwnerStatementLine.AgencyHoldsPortalAccountWithoutRecharge"/>).
    /// </summary>
    private static OwnerStatementLine LineOf(OwnerLedgerEntry entry, BookingFacts? booking, string? propertyName, PortalAccountHolders portalAccounts)
    {
        DateOnly? checkIn = booking is null ? null : DateOnly.FromDateTime(booking.CheckIn);
        DateOnly? checkOut = booking is null ? null : DateOnly.FromDateTime(booking.CheckOut);
        var isPayment = entry.EntryType is OwnerLedgerEntryType.OwnerPayment or OwnerLedgerEntryType.AgencyPayment;

        // The days of a stay come from its booking; a line whose booking is gone keeps the check-in the ledger line was posted with.
        if (booking is null && BookingIdOf(entry) is not null)
            checkIn = entry.ServiceDate;

        return new OwnerStatementLine(
            entry.Id,
            OwnerStatementLayout.SectionOf(entry.EntryType),
            entry.EntryType,
            isPayment ? entry.PaymentDate ?? entry.RecognitionDate : entry.RecognitionDate,
            checkIn,
            checkOut,
            propertyName,
            booking is null ? null : ChannelOf(booking),
            booking is null ? null : BookingCodes.Format(booking.BookingCode),
            entry.SourceType is OwnerLedgerSourceType.MandateFlatFee or OwnerLedgerSourceType.OwnerInvoice ? entry.SourceReference : null,
            entry.Description,
            entry.SharePercent,
            entry.Amount,
            entry.ReversesEntryId is not null,

            // V2 with the recharge (the test of the invoice, OwnerInvoiceService): the portal bills the agency, which recharges the commission on its invoice. V1 (the
            // default), or an account of the agency whose commissions it does not recharge: no invoice of the property manager carries it.
            AgencyHoldsPortalAccount: entry.EntryType == OwnerLedgerEntryType.ChannelFee
                && booking is not null
                && portalAccounts.RechargesCommissionOf(entry.PropertyId ?? booking.PropertyId, booking.Source))
        {
            // The other half of the test of the invoice: a channel fee the owner was charged on an account of the agency whose commissions it does not recharge now (a line posted
            // before FIX/BE-V2-NO-RECHARGE, which posts such a commission as ChannelFeeBorneByAgency). It is not the owner's portal invoice (V1) and no invoice recharges it; its
            // amount stays the one of the ledger line, in the totals, as the statement was made.
            AgencyAccountChargedWithoutRecharge = entry.EntryType == OwnerLedgerEntryType.ChannelFee
                && booking is not null
                && portalAccounts.IsAgencyAccountWithoutRecharge(entry.PropertyId ?? booking.PropertyId, booking.Source),
            LedgerEvidence = new OwnerStatementLedgerEvidence(
                entry.SourceType, entry.SourceId, entry.SourceReference, entry.ServiceDate, entry.RecognitionDate, entry.PaymentDate,
                entry.OriginalAmount, entry.OriginalCurrency, entry.StatementCurrency, entry.FxRate, entry.FxSource, entry.FxDate),
        };
    }

    /// <summary>The booking a ledger line is about: the source of a stay line or a flat fee, the booking a payment is of (its reference, with no dashes); none for a charge, a wire or a payout.</summary>
    private static Guid? BookingIdOf(OwnerLedgerEntry entry) => entry.SourceType switch
    {
        OwnerLedgerSourceType.Booking or OwnerLedgerSourceType.MandateFlatFee => entry.SourceId,
        OwnerLedgerSourceType.Payment => Guid.TryParseExact(entry.SourceReference, "N", out var id) ? id : null,
        _ => null,
    };

    private static BookingFacts? BookingOf(OwnerLedgerEntry entry, Dictionary<Guid, BookingFacts> bookings) =>
        BookingIdOf(entry) is { } id && bookings.TryGetValue(id, out var booking) ? booking : null;

    /// <summary>True when the stay owes its tourist tax: an active booking (confirmed, checked in or checked out) that is not a no-show, the one definition of the code.</summary>
    private static bool IsDue(BookingFacts booking) =>
        AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null;

    /// <summary>The ISTAT code of the comune of the property of a stay (only an Italian property has one), as the tourist tax looks its channel rule up; <c>null</c> without one.</summary>
    private static string? ComuneOf(BookingFacts stay, Dictionary<Guid, PropertyFacts> properties) =>
        properties.TryGetValue(stay.PropertyId, out var property) && !string.IsNullOrWhiteSpace(property.ComuneIstatCode) ? property.ComuneIstatCode.Trim() : null;

    private static string ChannelOf(BookingFacts booking) =>
        booking.Source == BookingSource.OtherChannel && !string.IsNullOrWhiteSpace(booking.ChannelLabel) ? booking.ChannelLabel.Trim() : booking.Source.ToString();

    /// <summary>
    /// The channels whose portal collects the tourist tax itself for the issuer of the org, the very answer the guest documents use
    /// (<see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>): the list the issuer confirmed (an empty one included: «no portal collects it» is an answer), and
    /// while it has confirmed none the shipped default of a Swiss issuer (<c>SwissTouristTax:PlatformCollected</c>), so the box of the statement and the documents of the stays
    /// never disagree on who collects. An org with no issuer profile is read as a new one (Italy, nothing confirmed). A service built without the line builder reads only what
    /// the issuer confirmed.
    /// </summary>
    private IReadOnlyList<BookingSource> PlatformCollectedChannelsOf(Guid orgId, OrgFiscalIssuerProfile? issuer)
    {
        if (documentLines is null)
            return issuer?.PlatformCollectedTouristTaxChannels ?? [];

        return documentLines.PlatformCollectedChannelsOf(issuer ?? new OrgFiscalIssuerProfile { OrgId = orgId });
    }

    /// <summary>
    /// The tourist tax of the stays that have revenue in the statement, collected by the agency: the tax stored on the booking, except where the channel collects
    /// it itself (a channel of the list of the issuer as the guest documents read it, <see cref="PlatformCollectedChannelsOf"/>, or the collector of the stay:
    /// <see cref="TouristTaxCollectorRules.Decide"/> from the active rule of
    /// the channel in the comune and the collector the host declared, else the one confirmed with the calculation). The stays whose revenue a reversal took back to
    /// nothing are not stays of the statement, and neither is a stay that owes nothing: a cancelled booking, a request or a no-show can keep revenue (a payment the
    /// guest does not get back) but its tax is not due, so it is never in the box (<see cref="AlloggiatiStatusRules"/>, the one definition of an active booking).
    /// </summary>
    private async Task<IReadOnlyList<OwnerStatementTouristTax>> TouristTaxAsync(
        Guid orgId,
        IReadOnlyList<OwnerLedgerEntry> entries,
        Dictionary<Guid, BookingFacts> bookings,
        Dictionary<Guid, PropertyFacts> properties,
        CancellationToken ct)
    {
        var stays = entries
            .Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && BookingIdOf(e) is not null)
            .GroupBy(e => BookingIdOf(e)!.Value)
            .Where(g => g.Sum(e => e.Amount) != 0m && bookings.TryGetValue(g.Key, out var b) && b.TouristTax > 0m && IsDue(b))
            .Select(g => bookings[g.Key])
            .ToList();
        if (stays.Count == 0)
            return [];

        // Who collects the tax of a stay is what the host's tourist tax says (TouristTaxCollectorRules: the active rule of the channel in the comune of the property,
        // else the collector the host declared for the stay), else the one the host confirmed with the calculation; and any channel of the list of the issuer as the
        // guest documents read it (the confirmed one, else the shipped default of a Swiss issuer). A stay that nobody is known to collect for is the agency's: never an
        // assumed 0.
        var ids = stays.Select(b => b.Id).ToList();
        var records = (await db.StayTouristTaxes
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(t => t.OrgId == orgId && ids.Contains(t.BookingId))
                .Select(t => new StayTaxFacts(t.BookingId, t.Collector, t.DeclaredCollector))
                .ToListAsync(ct))
            .ToDictionary(t => t.BookingId);
        var comuni = stays.Select(b => ComuneOf(b, properties)).Where(code => code is not null).Select(code => code!).Distinct().ToList();
        var rules = comuni.Count == 0
            ? []
            : await db.TouristTaxChannelRules.AsNoTracking().Where(r => r.IsActive && comuni.Contains(r.IstatCode)).ToListAsync(ct);
        var issuer = await db.OrgFiscalIssuerProfiles
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .OfOrganization(orgId)
            .FirstOrDefaultAsync(ct);
        var platformCollected = PlatformCollectedChannelsOf(orgId, issuer);

        bool ChannelCollects(BookingFacts stay)
        {
            if (platformCollected.Contains(stay.Source))
                return true;

            var comune = ComuneOf(stay, properties);
            var rule = comune is null ? null : rules.FirstOrDefault(r => r.Channel == stay.Source && r.IstatCode == comune);
            records.TryGetValue(stay.Id, out var record);
            var collector = TouristTaxCollectorRules.Decide(stay.Source, rule, record?.DeclaredCollector).Collector ?? record?.Collector;
            return collector == TouristTaxCollector.Channel;
        }

        return
        [
            .. stays
                .Where(b => !ChannelCollects(b))
                .OrderBy(b => b.CheckIn)
                .ThenBy(b => b.Id)
                .Select(b => new OwnerStatementTouristTax(
                    properties.TryGetValue(b.PropertyId, out var property) ? property.Name : null,
                    DateOnly.FromDateTime(b.CheckIn),
                    DateOnly.FromDateTime(b.CheckOut),
                    ChannelOf(b),
                    BookingCodes.Format(b.BookingCode),
                    StayCurrency.Normalize(properties.TryGetValue(b.PropertyId, out var currency) ? currency.Currency : null),
                    b.TouristTax)),
        ];
    }

    /// <summary>The invoice of the property manager to the owner for this statement, when E14 linked one that was issued: through the version, or through the owner and the month.</summary>
    private async Task<InvoiceFacts?> OwnerInvoiceAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        var orgId = version.OrgId;
        var rows = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Status == FiscalDocumentStatus.Issued
                && d.Kind != FiscalDocumentKind.CreditNote && !d.CreditedInFull && d.Number != null
                && (d.OwnerStatementVersionId == version.Id
                    || (d.RecipientOwnerId == version.PropertyOwnerId && d.PeriodYear == version.PeriodYear && d.PeriodMonth == version.PeriodMonth)))
            .Select(d => new InvoiceFacts(d.Number, d.IssueDate, d.OwnerStatementVersionId == version.Id))
            .ToListAsync(ct);

        // The one linked to this very version first, else the latest issued for the owner and the month.
        return rows.OrderByDescending(r => r.OfThisVersion).ThenByDescending(r => r.IssueDate).FirstOrDefault();
    }

    // ---------------------------------------------------------------- reads and names

    private async Task<OwnerStatementVersion> VersionAsync(Guid orgId, Guid versionId, CancellationToken ct) =>
        await db.OwnerStatementVersions
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct)
        ?? throw new NotFoundException($"Owner statement version {versionId} was not found")
        {
            Code = OwnerAccountingErrors.VersionNotFound,
            MessageKey = "OwnerStatementVersionNotFound",
        };

    private async Task<List<OwnerLedgerEntry>> EntriesAsync(OwnerStatementVersion version, CancellationToken ct) =>
        (await OwnerAccountingService.LinesOf(db.OwnerLedgerEntries.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking(), version).ToListAsync(ct))
        .OrderBy(e => e.PostedAt)
        .ThenBy(e => e.Id)
        .ToList();

    /// <summary>
    /// The file stored at the publication of the version: the version 1 of its owner document (its id is the id of the version), or <c>null</c> when none was stored.
    /// A statement has no other: the owner documents refuse a new version of it, and a row that got there some other way is never the file that was published.
    /// </summary>
    private async Task<OwnerDocumentVersion?> StoredFileAsync(Guid orgId, Guid versionId, CancellationToken ct) =>
        await db.OwnerDocumentVersions
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.OrgId == orgId && v.OwnerDocumentId == versionId && v.VersionNumber == 1, ct);

    /// <summary>
    /// The stored file as it was published: its bytes, read from the bucket and checked against the SHA-256 kept at the publication. <c>null</c>, with an error in the log
    /// (the ids only), when the file is missing from the bucket or is no longer that file.
    /// </summary>
    private async Task<byte[]?> ReadStoredAsync(OwnerDocumentVersion file, CancellationToken ct)
    {
        var stored = await storage.OpenReadAsync(StorageBucket.Private, file.StorageKey, ct);
        if (stored is null)
        {
            logger.LogError("Owner statement version {VersionId}: the stored PDF {FileId} is missing from the storage", file.OwnerDocumentId, file.Id);
            return null;
        }

        await using (stored)
        {
            using var buffer = new MemoryStream();
            await stored.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Owner statement version {VersionId}: the stored PDF {FileId} is not the file that was published (its SHA-256 differs)", file.OwnerDocumentId, file.Id);
                return null;
            }

            return bytes;
        }
    }

    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(time.GetUtcNow().UtcDateTime);

    /// <summary>The name a file of a version is offered under: the same as the published JSON of the version, with its own extension.</summary>
    private static string FileName(OwnerStatementVersion version, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"rendiconto-{version.PeriodYear:D4}-{version.PeriodMonth:D2}-v{version.VersionNumber}-{version.Id:N}{extension}");

    private static string PeriodLabel(OwnerStatementVersion version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.PeriodMonth:00}/{version.PeriodYear:0000}");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>What a ledger line needs of its booking.</summary>
    private sealed record BookingFacts(
        Guid Id,
        Guid PropertyId,
        DateTime CheckIn,
        DateTime CheckOut,
        BookingSource Source,
        string? ChannelLabel,
        string BookingCode,
        decimal TouristTax,
        BookingStatus Status,
        DateTime? NoShowAt);

    /// <summary>What a statement needs of a property: its name, its currency and, for an Italian one, the ISTAT code of its comune.</summary>
    private sealed record PropertyFacts(string Name, string Currency, string? ComuneIstatCode);

    /// <summary>What the tourist tax of a stay says about who collects: the collector confirmed with the calculation and the one the host declared.</summary>
    private sealed record StayTaxFacts(Guid BookingId, TouristTaxCollector? Collector, TouristTaxCollector? DeclaredCollector);

    private sealed record InvoiceFacts(string? Number, DateOnly? IssueDate, bool OfThisVersion);
}
