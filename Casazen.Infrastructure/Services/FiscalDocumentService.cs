using System.Data;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using RuleDocumentKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFiscalDocumentService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Every active booking.</b> CasaZen issues for every source (direct, manual, the portals through Octorate) since 2026-11-14 (E11/F01/US01/T01);
/// a booking that is not active (the one definition of the code) gets no document, nor does one that already has a document issued elsewhere (a document
/// imported from Octorate, or a legacy lock to Octorate): never two documents for one stay.</item>
/// <item><b>The issuer as it was.</b> The issue writes the snapshot of the issuer and the version of the rule on the document; the PDF and the XML of an
/// issued document read the issuer from the snapshot, never from the profile of today.</item>
/// <item><b>Payment state.</b> A document is <c>Paid</c> when the completed payments of its booking, net of the refunds, reach its total; else <c>ToCollect</c>.</item>
/// <item><b>Logs</b> carry ids, codes and counts: never a value of the recipient or an amount.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentService(
    AppDbContext db,
    IDocumentIssuerService issuers,
    IInvoiceDataService invoiceData,
    IFiscalNumberingService numbering,
    IFatturaPaXmlBuilder xmlBuilder,
    IDomainEventRecorder events,
    IPdfDocumentRenderer pdfRenderer,
    IDocumentTexts texts,
    IFiscalTaxRuleResolver taxRules,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentService> logger) : IFiscalDocumentService
{
    /// <summary>The descriptions of the lines: fixed words of the document, never personal data.</summary>
    private const string LodgingKey = "FiscalDocLineLodging";
    private const string CleaningKey = "FiscalDocLineCleaning";
    private const string TouristTaxKey = "FiscalDocLineTouristTax";
    private const string SwissTouristTaxKey = "FiscalDocLineTouristTaxCh";
    private const string CreditKey = "FiscalDocLineCredit";

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<FiscalDocument> CreateDraftAsync(Guid bookingId, string? userId, CancellationToken ct = default)
    {
        var existing = await db.FiscalDocuments
            .Include(d => d.Lines)
            .Where(d => d.BookingId == bookingId && d.Source == FiscalDocumentSource.CasaZen && d.Status == FiscalDocumentStatus.Draft
                        && d.Kind != DocumentKind.CreditNote)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return await RebuildAsync(existing, ct);

        var draft = new FiscalDocument { Source = FiscalDocumentSource.CasaZen, Status = FiscalDocumentStatus.Draft, CreatedAt = UtcNow, CreatedByUserId = userId };
        draft.BookingId = bookingId;
        return await RebuildAsync(draft, ct, isNew: true);
    }

    public async Task<FiscalDocument> RefreshDraftAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        return await RebuildAsync(document, ct);
    }

    public async Task DiscardDraftAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        document.Status = FiscalDocumentStatus.Discarded;
        document.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Fiscal document draft {DocumentId} discarded", document.Id);
    }

    public async Task<FiscalDocument> IssueAsync(Guid documentId, bool confirm, CancellationToken ct = default)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");

        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        var booking = await BookingOfAsync(document.BookingId, ct);
        await RequireCasazenIssuesAsync(booking, ct);
        var (profile, org) = await RequireIssuerAsync(booking.OrgId, ct);
        var rule = RuleOf(profile, booking);
        RequireDraftOfTheRule(document, rule);
        await RequireNoOtherDocumentAsync(booking.Id, document.Id, ct);

        var today = timeProvider.TodayInRomeAsDateOnly();
        var series = SeriesOf(profile, today);
        await IssueInTransactionAsync(document, booking, rule, profile, org, series, today, credited: null, ct);
        logger.LogInformation("Fiscal document {DocumentId} of booking {BookingId} issued", document.Id, booking.Id);
        return document;
    }

    public async Task<FiscalDocument> CreateCreditNoteAsync(
        Guid documentId,
        decimal? amount,
        string reason,
        bool confirm,
        string? userId,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");

        var original = await RequireAsync(documentId, ct);
        if (original.Source != FiscalDocumentSource.CasaZen)
            throw ReadOnly();
        if (original.Status != FiscalDocumentStatus.Issued || original.Kind == DocumentKind.CreditNote || original.Kind == DocumentKind.Proforma)
            throw new DomainRuleException(FiscalDocumentErrors.NotCreditable, "FiscalDocumentNotCreditable");

        var credited = await CreditedSoFarAsync(original.Id, ct);
        var remaining = original.Total - credited;
        var value = amount ?? remaining;
        if (value <= 0m || value > remaining)
            throw new DomainRuleException(FiscalDocumentErrors.CreditAmountExceeds, "FiscalDocumentCreditAmountExceeds", remaining);

        var booking = await BookingOfAsync(original.BookingId, ct);
        var (profile, org) = await RequireIssuerAsync(booking.OrgId, ct);
        var rule = RuleOf(profile, booking);

        var note = new FiscalDocument
        {
            OrgId = original.OrgId,
            BookingId = original.BookingId,
            PropertyId = original.PropertyId,
            Purpose = original.Purpose,
            RecipientOwnerId = original.RecipientOwnerId,
            OwnerStatementVersionId = original.OwnerStatementVersionId,
            PeriodYear = original.PeriodYear,
            PeriodMonth = original.PeriodMonth,
            ServicePeriodFrom = original.ServicePeriodFrom,
            ServicePeriodTo = original.ServicePeriodTo,
            PaymentChannel = original.PaymentChannel,
            Source = FiscalDocumentSource.CasaZen,
            Kind = DocumentKind.CreditNote,
            Status = FiscalDocumentStatus.Draft,
            Currency = original.Currency,
            CreditedDocumentId = original.Id,
            CreatedAt = UtcNow,
            CreatedByUserId = userId,
        };
        CopyRecipient(original, note);

        // The whole document: its lines and amounts as stored, never recomputed. A part: spread over its lines in proportion.
        var language = DocumentLanguages.Italian;
        var description = texts.Get(CreditKey, language, original.Number ?? string.Empty, Truncate(reason, FiscalDocumentErrors.CreditReasonMaxLength));
        if (credited == 0m && value == original.Total)
        {
            note.TaxableBase = original.TaxableBase;
            note.VatAmount = original.VatAmount;
            note.StampDuty = original.StampDuty;
            note.Total = original.Total;
            note.VatRatePercent = original.VatRatePercent;
            note.VatNatureCode = original.VatNatureCode;
            foreach (var line in original.Lines.OrderBy(l => l.LineNumber))
                note.Lines.Add(CopyLine(note, line));
        }
        else
        {
            var amounts = FiscalDocumentTotals.FromGross(FiscalDocumentTotals.ScaleForCredit(GrossLinesOf(original), value), rule.StampDuty);
            ApplyAmounts(note, amounts);
        }

        // The reason goes with every line («Storno del documento …: <reason> - <line>»): a credit note has no line of its own without an amount.
        foreach (var line in note.Lines)
            line.Description = Truncate($"{description} - {line.Description}", FiscalDocumentLine.DescriptionMaxLength);

        db.FiscalDocuments.Add(note);
        var today = timeProvider.TodayInRomeAsDateOnly();
        await IssueInTransactionAsync(note, booking, rule, profile, org, original.Series, today, original, ct);
        logger.LogInformation("Credit note {CreditNoteId} of fiscal document {DocumentId} issued", note.Id, original.Id);
        return note;
    }

    public async Task<FiscalDocument?> GetAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is not null)
            await ApplyCurrentPaymentStateAsync(db, [document], ct);
        return document;
    }

    public async Task<FiscalDocumentPage> ListAsync(HostScope scope, FiscalDocumentCriteria criteria, int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, FiscalDocumentPage.MaxPageSize);

        var query = Matching(scope, criteria);
        var total = await query.CountAsync(ct);
        var items = await InListOrder(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        await ApplyCurrentPaymentStateAsync(db, items, ct);
        return new FiscalDocumentPage(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<FiscalDocumentExportRow>> ListForExportAsync(HostScope scope, FiscalDocumentCriteria criteria, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);
        limit = Math.Max(1, limit);

        // The same scope and filters as the list, and only the documents that were issued. The columns the file has and no more: the XML of an invoice is
        // the heavy part of a document and is never read here.
        var documents = await InListOrder(Matching(scope, criteria with { Status = FiscalDocumentStatus.Issued }))
            .Take(limit)
            .Select(d => new ExportSource(
                d.Id, d.BookingId, d.Kind, d.Number, d.IssueDate, d.RecipientName, d.TaxableBase, d.VatAmount, d.StampDuty, d.Total, d.Currency, d.PaymentState, d.SdiStatus, d.Source))
            .ToListAsync(ct);
        if (documents.Count == 0)
            return [];

        var bookingIds = documents.Where(d => d.BookingId != null).Select(d => d.BookingId!.Value).Distinct().ToList();
        var bookings = await db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == scope.OrgId && bookingIds.Contains(b.Id))
            .Select(b => new { b.Id, b.BookingCode, PropertyName = b.Property.Name })
            .ToDictionaryAsync(b => b.Id, ct);
        var collected = await CollectedByBookingAsync(
            db,
            documents.Where(d => IsPaymentStateReadFromPayments(d.Source, d.Kind, d.BookingId)).Select(d => d.BookingId!.Value).Distinct().ToList(),
            ct);

        return documents
            .Select(d =>
            {
                var booking = d.BookingId is { } id && bookings.TryGetValue(id, out var found) ? found : null;
                return new FiscalDocumentExportRow(
                    d.Id,
                    d.Kind,
                    d.Number,
                    d.IssueDate,
                    booking?.BookingCode,
                    booking?.PropertyName,
                    d.RecipientName,
                    d.TaxableBase,
                    d.VatAmount,
                    d.StampDuty,
                    d.Total,
                    d.Currency,
                    IsPaymentStateReadFromPayments(d.Source, d.Kind, d.BookingId) ? PaymentStateOf(collected.GetValueOrDefault(d.BookingId!.Value), d.Total) : d.PaymentState,
                    d.SdiStatus,
                    d.Source);
            })
            .ToList();
    }

    /// <summary>The earliest day the list starts from: no document was created before it, and the start of a day long before it cannot be told in UTC from Europe/Rome.</summary>
    private static readonly DateOnly FirstDayOfTheList = new(1900, 1, 1);

    /// <summary>The documents of the scope that the criteria keep, narrowed in SQL: the one query behind the list and the export.</summary>
    private IQueryable<FiscalDocument> Matching(HostScope scope, FiscalDocumentCriteria criteria)
    {
        var query = db.FiscalDocuments.AsNoTracking().Where(d => d.OrgId == scope.OrgId);
        var bookings = db.Bookings.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]).Where(b => b.OrgId == scope.OrgId).InScope(scope);
        if (!scope.IsOrgWide)
            query = query.Where(d => d.BookingId != null && bookings.Any(b => b.Id == d.BookingId));
        if (criteria.BookingId is { } bookingId)
            query = query.Where(d => d.BookingId == bookingId);
        if (criteria.PropertyId is { } propertyId)
            query = query.Where(d => bookings.Any(b => b.Id == d.BookingId && b.PropertyId == propertyId));
        if (criteria.Kind is { } kind)
            query = query.Where(d => d.Kind == kind);
        if (criteria.Status is { } status)
            query = query.Where(d => d.Status == status);
        if (criteria.Source is { } source)
            query = query.Where(d => d.Source == source);
        if (criteria.From is { } from)
        {
            // The start of 0001-01-01 in Rome falls before the first instant a DateTime holds, so RomeCalendar throws for it (a 500 on from=0001-01-01): a day before
            // FirstDayOfTheList starts the list where no document is older anyway. The issue day is still compared with the day that was asked for.
            var start = RomeCalendar.StartOfDayUtc(from < FirstDayOfTheList ? FirstDayOfTheList : from);
            query = query.Where(d => d.IssueDate != null ? d.IssueDate >= from : d.CreatedAt >= start);
        }

        if (criteria.To is { } to && to != DateOnly.MaxValue)
        {
            var end = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(d => d.IssueDate != null ? d.IssueDate <= to : d.CreatedAt < end);
        }

        return query;
    }

    private static IOrderedQueryable<FiscalDocument> InListOrder(IQueryable<FiscalDocument> query) =>
        query.OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.CreatedAt).ThenBy(d => d.Id);

    /// <summary>What the export reads of a document: its columns, without the XML, the lines and the rest of the recipient.</summary>
    private sealed record ExportSource(
        Guid Id,
        Guid? BookingId,
        DocumentKind Kind,
        string? Number,
        DateOnly? IssueDate,
        string? RecipientName,
        decimal TaxableBase,
        decimal VatAmount,
        decimal StampDuty,
        decimal Total,
        string Currency,
        FiscalDocumentPaymentState PaymentState,
        string SdiStatus,
        FiscalDocumentSource Source);

    public async Task<string> GetXmlAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw NotFound(documentId);
        if (document.Source != FiscalDocumentSource.CasaZen || document.Status != FiscalDocumentStatus.Issued || string.IsNullOrEmpty(document.FatturaPaXml))
            throw new NotFoundException($"Fiscal document {documentId} has no XML") { Code = FiscalDocumentErrors.XmlUnavailable, MessageKey = "FiscalDocumentXmlUnavailable" };

        return document.FatturaPaXml;
    }

    public async Task<byte[]> GetPdfAsync(Guid documentId, string language, CancellationToken ct = default) =>
        pdfRenderer.Render(await PdfContentAsync(documentId, language, ct));

    /// <summary>
    /// What the PDF of a CasaZen document shows (<see cref="GetPdfAsync"/> renders it): the issuer of the snapshot for an issued document, the profile of
    /// today only for a draft and for a document issued before the snapshot existed; the current payment state; the name of the property.
    /// </summary>
    internal async Task<PdfDocumentContent> PdfContentAsync(Guid documentId, string language, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw NotFound(documentId);
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw ReadOnly();

        await ApplyCurrentPaymentStateAsync(db, [document], ct);
        var issuer = document.Status == FiscalDocumentStatus.Issued ? FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot) : null;
        if (issuer is null)
        {
            var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == document.OrgId, ct);
            var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == document.OrgId, ct);
            issuer = FiscalIssuerSnapshot.From(profile, org);
        }

        var credited = document.CreditedDocumentId is { } creditedId
            ? await db.FiscalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == creditedId, ct)
            : null;
        var propertyName = document.PropertyId is { } propertyId
            ? await db.Properties.AsNoTracking().IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]).Where(p => p.Id == propertyId).Select(p => p.Name).FirstOrDefaultAsync(ct)
            : null;
        return FiscalDocumentPdfs.Pdf(document, issuer, texts, language, credited, propertyName);
    }

    // ---------------------------------------------------------------- draft

    /// <summary>
    /// The draft made from the booking: the checks of the issuer and of its profile, the tax rule in force at the check-out of the stay, the invoice data
    /// the kind of document needs, then the lines (one VAT per line type) and the amounts.
    /// </summary>
    private async Task<FiscalDocument> RebuildAsync(FiscalDocument draft, CancellationToken ct, bool isNew = false)
    {
        var booking = await BookingOfAsync(draft.BookingId, ct);
        await RequireCasazenIssuesAsync(booking, ct);
        var (profile, _) = await RequireIssuerAsync(booking.OrgId, ct);
        var rule = RuleOf(profile, booking);
        if (isNew)
            await RequireNoOtherDocumentAsync(booking.Id, draft.Id, ct);

        var kind = rule.DocumentKind == RuleDocumentKind.Receipt ? DocumentKind.Receipt : DocumentKind.Invoice;
        var recipient = await invoiceData.GetAsync(booking.Id, ct);
        var missing = MissingInvoiceData(recipient, NeedsOnlyTheName(rule, kind));
        if (missing.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentErrors.InvoiceDataMissing, "FiscalDocumentInvoiceDataMissing")
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = missing },
            };
        }

        var amounts = FiscalDocumentTotals.FromGross(GrossLinesOf(booking, rule), rule.StampDuty);

        draft.OrgId = booking.OrgId;
        draft.PropertyId = booking.PropertyId;
        draft.Purpose = FiscalDocumentPurpose.GuestStay;
        draft.ServicePeriodFrom = DateOnly.FromDateTime(booking.CheckIn);
        draft.ServicePeriodTo = DateOnly.FromDateTime(booking.CheckOut);
        draft.RuleVersion = rule.Version;
        draft.Kind = kind;
        draft.Currency = StayCurrency.Normalize(booking.Currency);
        CopyRecipient(recipient!, draft);
        foreach (var line in draft.Lines.ToList())
            db.FiscalDocumentLines.Remove(line);
        draft.Lines.Clear();
        ApplyAmounts(draft, amounts);
        draft.PaymentState = await PaymentStateAsync(booking.Id, draft.Total, ct);
        draft.UpdatedAt = UtcNow;
        if (isNew)
            db.FiscalDocuments.Add(draft);
        else
            foreach (var line in draft.Lines)
                db.FiscalDocumentLines.Add(line);

        await db.SaveChangesAsync(ct);
        return draft;
    }

    /// <summary>
    /// The lines of a booking as the guest pays them, each at the VAT the rule gives its type: the stay (<c>BasePrice - CleaningFee</c>), the cleaning
    /// (with the stay or as a service, as the profile says) and the tourist tax (outside the VAT: <c>N1</c> in Italy, no nature in Switzerland, where it is
    /// labelled as the cantonal tax).
    /// </summary>
    private List<FiscalDocumentGrossLine> GrossLinesOf(BookingFacts booking, FiscalTaxRule rule)
    {
        var language = DocumentLanguages.Italian;
        var lines = new List<FiscalDocumentGrossLine>();
        var lodging = booking.BasePrice - booking.CleaningFee;
        if (lodging > 0m)
            lines.Add(Line(texts.Get(LodgingKey, language, booking.Nights), lodging, FiscalLineType.Accommodation));
        if (booking.CleaningFee > 0m)
            lines.Add(Line(texts.Get(CleaningKey, language), booking.CleaningFee, FiscalLineType.Cleaning));
        if (booking.TouristTax > 0m)
            lines.Add(Line(texts.Get(rule.IsSwiss ? SwissTouristTaxKey : TouristTaxKey, language), booking.TouristTax, FiscalLineType.TouristTax));

        return lines;

        FiscalDocumentGrossLine Line(string description, decimal gross, FiscalLineType type)
        {
            var rate = rule.RateOf(type);
            return new FiscalDocumentGrossLine(description, 1m, gross, rate.Percent, rate.NatureCode, type);
        }
    }

    /// <summary>
    /// The lines of an issued document as the guest paid them, for a partial credit note: the net plus the stored VAT of the line, or, for the lines
    /// written before their VAT was stored, plus their share of the VAT of the document.
    /// </summary>
    private static IReadOnlyList<FiscalDocumentGrossLine> GrossLinesOf(FiscalDocument document)
    {
        var lines = document.Lines.Where(l => l.TotalPrice > 0m).OrderBy(l => l.LineNumber).ToList();
        if (lines.All(l => l.VatAmount is not null))
        {
            return lines
                .Select(l => new FiscalDocumentGrossLine(l.Description, 1m, l.TotalPrice + l.VatAmount!.Value, l.VatRatePercent, l.VatNatureCode, l.LineType))
                .ToList();
        }

        var taxed = lines.Where(l => l.VatRatePercent > 0m).ToList();
        var taxedNet = taxed.Sum(l => l.TotalPrice);
        var gross = lines.Select(l => new FiscalDocumentGrossLine(
                l.Description,
                1m,
                l.VatRatePercent > 0m && taxedNet > 0m
                    ? l.TotalPrice + Math.Round(document.VatAmount * l.TotalPrice / taxedNet, 2, MidpointRounding.AwayFromZero)
                    : l.TotalPrice,
                l.VatRatePercent,
                l.VatNatureCode,
                l.LineType))
            .ToList();

        // The cent of difference of the shares of the VAT goes to the largest taxed line, so the lines add up to what the guest paid.
        var difference = document.TaxableBase + document.VatAmount - gross.Sum(l => l.GrossAmount);
        if (difference != 0m && gross.Count > 0)
        {
            var largest = gross.Select((l, i) => (Line: l, Index: i)).OrderByDescending(x => x.Line.GrossAmount).First();
            gross[largest.Index] = largest.Line with { GrossAmount = largest.Line.GrossAmount + difference };
        }

        return gross;
    }

    private static void ApplyAmounts(FiscalDocument document, FiscalDocumentAmounts amounts)
    {
        foreach (var line in amounts.Lines)
        {
            document.Lines.Add(new FiscalDocumentLine
            {
                OrgId = document.OrgId,
                DocumentId = document.Id,
                LineNumber = line.LineNumber,
                LineType = line.LineType,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                TotalPrice = line.TotalPrice,
                VatRatePercent = line.VatRatePercent,
                VatNatureCode = line.NatureCode,
                VatAmount = line.VatAmount,
            });
        }

        document.TaxableBase = amounts.TaxableBase;
        document.VatAmount = amounts.VatAmount;
        document.StampDuty = amounts.StampDuty;
        document.Total = amounts.Total;
        var taxedRates = amounts.Summary.Where(s => s.VatRatePercent > 0m).Select(s => s.VatRatePercent).Distinct().ToList();
        document.VatRatePercent = taxedRates.Count == 1 ? taxedRates[0] : taxedRates.Count == 0 ? 0m : null;
        document.VatNatureCode = taxedRates.Count == 0 ? amounts.Summary.Select(s => s.NatureCode).FirstOrDefault(n => n is not null) : null;
    }

    /// <summary>
    /// The tax rule of a document of the booking: the rule set in force at the check-out of the stay (a stay that ended before a new version keeps the old
    /// rate) for the country, the regime and the treatments of the issuer; 422 <c>document_rule_not_configured</c> when none applies.
    /// </summary>
    private FiscalTaxRule RuleOf(OrgFiscalIssuerProfile profile, BookingFacts booking) =>
        taxRules.Resolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, booking.StayDate);

    // ---------------------------------------------------------------- issue

    private async Task IssueInTransactionAsync(
        FiscalDocument document,
        BookingFacts booking,
        FiscalTaxRule rule,
        OrgFiscalIssuerProfile profile,
        Org org,
        string series,
        DateOnly today,
        FiscalDocument? credited,
        CancellationToken ct)
    {
        // READ COMMITTED: the advisory lock of the numbering joins it; a failure rolls the number back with the document (no gap).
        await using var transaction = PostgresAdvisoryLocks.IsSupported(db)
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;

        var number = await numbering.ReserveNextAsync(booking.OrgId, series, ct);

        // What the document may be is decided again here, under the lock of the series (a credit note takes the series of its original, so the same lock):
        // the checks before the transaction are quick refusals, but two parallel requests both pass them, and neither the original nor the booking changes to
        // tell them apart. A refusal rolls the transaction back with the number (no gap).
        if (credited is null)
        {
            await RequireNoOtherDocumentAsync(booking.Id, document.Id, ct);
        }
        else
        {
            var remaining = credited.Total - await CreditedSoFarAsync(credited.Id, ct);
            if (document.Total <= 0m || document.Total > remaining)
                throw new DomainRuleException(FiscalDocumentErrors.CreditAmountExceeds, "FiscalDocumentCreditAmountExceeds", remaining);

            // The credit note that completes the credit marks the original: an owner invoice credited in full frees its owner and period (E14).
            if (document.Total == remaining)
                credited.CreditedInFull = true;
        }

        document.Series = number.Series;
        document.SequenceNumber = number.Number;
        document.Number = number.Display;
        document.IssueDate = today;

        // The issuer as it is now, kept with the document: its PDF and its XML read it from here from now on. The version of the rule is the one the draft
        // was made with (a document made before the versions existed takes the one in force).
        var issuer = FiscalIssuerSnapshot.From(profile, org, rule.FatturaPaRegimeCode);
        document.IssuerSnapshot = issuer.ToJson();
        document.RuleVersion ??= rule.Version;

        // The electronic invoice is Italian: a Swiss document has no XML and nothing to send to the SDI, nor has a receipt or the credit note of a receipt.
        if (IsElectronicInvoice(rule, document.Kind, credited?.Kind))
        {
            document.FatturaPaXml = xmlBuilder.Build(FatturaPaInputOf(document, issuer, rule, credited));
            document.SdiStatus = FiscalDocumentSdiStatuses.ToSend;
        }
        else
        {
            document.FatturaPaXml = null;
            document.SdiStatus = FiscalDocumentSdiStatuses.NotApplicable;
        }

        if (credited is null)
            await issuers.LockIssuerAsync(booking.Id, BookingDocumentIssuerKind.CasaZen, document.Id, save: false, ct);

        document.Status = FiscalDocumentStatus.Issued;
        document.SdiStatusUpdatedAt = UtcNow;
        document.UpdatedAt = UtcNow;
        events.Record(
            credited is null ? DomainEventType.FiscalDocumentIssued : DomainEventType.FiscalDocumentCreditNoteIssued,
            document.Id,
            new FiscalDocumentIssuedPayload(document.Id, document.BookingId, document.Kind.ToString(), credited?.Id),
            dedupKey: $"fiscal-document-issued:{document.Id:N}",
            orgId: document.OrgId);

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The input of the XML of an issued document: the issuer from its snapshot, the recipient from the document, the stored lines and the VAT summary of
    /// their stored VAT per rate (<see cref="FiscalDocumentTotals.SummaryOf"/>), never <c>taxable × rate</c> again.
    /// </summary>
    private static FatturaPaInput FatturaPaInputOf(FiscalDocument document, FiscalIssuerSnapshot issuerSnapshot, FiscalTaxRule rule, FiscalDocument? credited)
    {
        var lines = AmountsOf(document);
        var summary = FiscalDocumentTotals.SummaryOf(lines, document.VatAmount);

        var vatNumber = DigitsOf(issuerSnapshot.VatNumber);
        var issuerCountry = string.IsNullOrWhiteSpace(issuerSnapshot.CountryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : issuerSnapshot.CountryCode;
        var issuer = new FatturaPaParty(
            vatNumber is null ? null : new FatturaPaId(issuerCountry, vatNumber),
            issuerSnapshot.FiscalCode,
            issuerSnapshot.LegalName,
            null,
            null,
            new FatturaPaAddress(
                issuerSnapshot.AddressStreet ?? string.Empty,
                issuerSnapshot.AddressCivic,
                issuerSnapshot.PostalCode ?? string.Empty,
                issuerSnapshot.City ?? string.Empty,
                issuerSnapshot.Province,
                issuerCountry),
            issuerSnapshot.FatturaPaRegimeCode ?? rule.FatturaPaRegimeCode);

        var recipientCountry = document.RecipientCountryCode ?? "IT";
        var (first, last) = SplitName(document.RecipientName);
        var isCompany = document.RecipientKind == InvoiceRecipientKind.Company;
        var recipientVat = RecipientVatId(document.RecipientVatNumber, recipientCountry, document.RecipientFiscalCode);
        var recipient = new FatturaPaParty(
            recipientVat,
            document.RecipientFiscalCode,
            isCompany ? document.RecipientName : null,
            isCompany ? null : first,
            isCompany ? null : last,
            new FatturaPaAddress(
                document.RecipientAddressStreet ?? string.Empty,
                document.RecipientAddressCivic,
                string.Equals(recipientCountry, "IT", StringComparison.OrdinalIgnoreCase) ? document.RecipientPostalCode ?? string.Empty : "00000",
                document.RecipientCity ?? string.Empty,
                string.Equals(recipientCountry, "IT", StringComparison.OrdinalIgnoreCase) ? document.RecipientProvince : null,
                recipientCountry));

        return new FatturaPaInput(
            new FatturaPaId(issuerCountry, issuerSnapshot.FiscalCode ?? vatNumber ?? string.Empty),
            document.Id.ToString("N")[..10].ToUpperInvariant(),
            issuer,
            recipient,
            document.RecipientSdiCode,
            document.RecipientPec,
            document.Kind == DocumentKind.CreditNote ? rule.CreditNoteTypeCode : rule.InvoiceTypeCode,
            document.Number!,
            document.IssueDate!.Value,
            document.Currency,
            lines,
            summary,
            document.StampDuty,
            document.Total,
            credited is null ? null : new FatturaPaRelatedDocument(credited.Number ?? string.Empty, credited.IssueDate));
    }

    /// <summary>The stored lines of a document as amounts, in order, with their type and their stored VAT.</summary>
    internal static IReadOnlyList<FiscalDocumentLineAmounts> AmountsOf(FiscalDocument document) =>
        document.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new FiscalDocumentLineAmounts(
                l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.TotalPrice, l.VatRatePercent, l.VatNatureCode, l.LineType, l.VatAmount))
            .ToList();

    // ---------------------------------------------------------------- rules

    /// <summary>
    /// CasaZen issues for every active booking of every source (direct, manual, local, the portals through Octorate) while the issuance is on
    /// (E11/F01/US01/T01): the source is no longer a gate. A booking that is not active, the issuance switched off, and a booking a legacy row locked to
    /// Octorate are refused.
    /// </summary>
    private async Task RequireCasazenIssuesAsync(BookingFacts booking, CancellationToken ct)
    {
        // A booking that is not active (a request, a hold, a cancelled booking, a no-show) owes no document: no draft, no number, no lock of the issuer. «Active»
        // is the one definition of the code (AlloggiatiStatusRules), never a list of its own.
        if (!AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) || booking.NoShowAt is not null)
            throw new DomainRuleException(FiscalDocumentErrors.BookingNotActive, "FiscalDocumentBookingNotActive");

        var decision = await issuers.ResolveIssuerAsync(booking.Id, ct);
        switch (decision.Kind)
        {
            case IssuerDecisionKind.CasaZen:
                return;
            case IssuerDecisionKind.None:
                throw new DomainRuleException(FiscalDocumentErrors.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));
            default:
                throw new DomainConflictException(FiscalDocumentErrors.IssuerConflict, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuerConflict))
                {
                    Extensions = new Dictionary<string, object?> { ["reason"] = decision.Reason },
                };
        }
    }

    private async Task<(OrgFiscalIssuerProfile Profile, Org Org)> RequireIssuerAsync(Guid orgId, CancellationToken ct)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgId, ct);
        var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
        var settings = await issuers.GetSettingsAsync(orgId, ct);
        if (profile is null || settings.MissingFields.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentErrors.ProfileIncomplete, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ProfileIncomplete))
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = settings.MissingFields },
            };
        }

        return (profile, org);
    }

    /// <summary>
    /// One invoice (or receipt) per booking, unless it was credited in full: 409 <c>document_already_issued</c>. Whoever issued it counts: a document imported
    /// from Octorate (a document issued elsewhere) holds the booking too, so CasaZen never issues a second document for the stay; a proforma is not a document.
    /// </summary>
    private async Task RequireNoOtherDocumentAsync(Guid bookingId, Guid exceptId, CancellationToken ct)
    {
        var issued = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId && d.Id != exceptId && d.Status == FiscalDocumentStatus.Issued
                        && d.Kind != DocumentKind.CreditNote && d.Kind != DocumentKind.Proforma)
            .Select(d => new { d.Id, d.Total })
            .ToListAsync(ct);
        foreach (var document in issued)
        {
            if (await CreditedSoFarAsync(document.Id, ct) < document.Total)
                throw new DomainConflictException(FiscalDocumentErrors.AlreadyIssued, "FiscalDocumentAlreadyIssued");
        }
    }

    private Task<decimal> CreditedSoFarAsync(Guid documentId, CancellationToken ct) =>
        db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.CreditedDocumentId == documentId && d.Status == FiscalDocumentStatus.Issued && d.Kind == DocumentKind.CreditNote)
            .SumAsync(d => d.Total, ct);

    private static void RequireCasazenDraft(FiscalDocument document)
    {
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw ReadOnly();
        if (document.Status != FiscalDocumentStatus.Draft)
            throw new DomainRuleException(FiscalDocumentErrors.NotDraft, "FiscalDocumentNotDraft");
    }

    /// <summary>
    /// The fields of the invoice data a document needs. A Swiss document (any kind) and an Italian receipt need only the name of the recipient: its address
    /// is printed when there is one, and an Italian fiscal code is never asked (E11/F01/US01/T02). An Italian invoice needs the name, the identification
    /// (the fiscal code of a person in Italy or the country abroad; the VAT number of a company, with its SDI code or PEC checked on its own) and the address.
    /// </summary>
    internal static IReadOnlyList<string> MissingInvoiceData(InvoiceDataView? data, bool nameOnly = false)
    {
        if (data is null)
            return [InvoiceDataFields.Name];

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(data.Name))
            missing.Add(InvoiceDataFields.Name);
        if (nameOnly)
            return missing;

        if (InvoiceDataFormats.MissingIdentification(data.RecipientKind, data.CountryCode, data.FiscalCode, data.VatNumber) is { } identification)
            missing.Add(identification);
        if (string.IsNullOrWhiteSpace(data.AddressStreet))
            missing.Add(InvoiceDataFields.AddressStreet);
        if (string.IsNullOrWhiteSpace(data.PostalCode))
            missing.Add(InvoiceDataFields.PostalCode);
        if (string.IsNullOrWhiteSpace(data.City))
            missing.Add(InvoiceDataFields.City);
        return missing;
    }

    /// <summary>A Swiss document (any kind) and an Italian receipt need only the name of the recipient; an Italian invoice the whole of its data.</summary>
    internal static bool NeedsOnlyTheName(FiscalTaxRule rule, DocumentKind kind) => rule.IsSwiss || kind == DocumentKind.Receipt;

    /// <summary>
    /// The draft was made with the tax rule the document is about to be issued with: the version of its rule, the kind of document and the VAT of each of its
    /// lines (by type, with the treatments of the issuer) are those of <paramref name="rule"/>. When the regime, the country, a treatment of the issuer or the
    /// catalog changed since the draft was made, its amounts are the old rule's while the XML, the snapshot of the issuer and the layout would be the new one's
    /// and the host confirmed other amounts: 409 <c>document_draft_outdated</c>, the draft has to be refreshed first. A draft without a version (made before the
    /// versions existed) is not checked.
    /// </summary>
    private static void RequireDraftOfTheRule(FiscalDocument draft, FiscalTaxRule rule)
    {
        if (draft.RuleVersion is null)
            return;

        var kind = rule.DocumentKind == RuleDocumentKind.Receipt ? DocumentKind.Receipt : DocumentKind.Invoice;
        var sameRule = string.Equals(draft.RuleVersion, rule.Version, StringComparison.Ordinal) && draft.Kind == kind;
        if (sameRule && draft.Lines.All(line => HasTheVatOfTheRule(line, rule)))
            return;

        throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");
    }

    private static bool HasTheVatOfTheRule(FiscalDocumentLine line, FiscalTaxRule rule) =>
        rule.Rates.TryGetValue(line.LineType, out var rate) && line.VatRatePercent == rate.Percent && string.Equals(line.VatNatureCode, rate.NatureCode, StringComparison.Ordinal);

    /// <summary>
    /// An Italian invoice, or the credit note of one, is an electronic invoice (FatturaPA, to send to the SDI). A Swiss document is not, nor is a receipt or the
    /// credit note of a receipt: a private person has no VAT number to put on an XML, and nothing is sent to the SDI.
    /// </summary>
    internal static bool IsElectronicInvoice(FiscalTaxRule rule, DocumentKind kind, DocumentKind? creditedKind) =>
        !rule.IsSwiss && (kind == DocumentKind.Invoice || (kind == DocumentKind.CreditNote && creditedKind != DocumentKind.Receipt));

    /// <summary>The series of a guest document: the pattern of the profile (<c>GuestDocumentSeries</c>), else the default pattern (the year).</summary>
    private static string SeriesOf(OrgFiscalIssuerProfile profile, DateOnly today) =>
        FiscalNumberFormat.SeriesFor(profile.GuestDocumentSeries, today);

    private async Task<FiscalDocumentPaymentState> PaymentStateAsync(Guid bookingId, decimal total, CancellationToken ct)
    {
        var collected = await CollectedByBookingAsync(db, [bookingId], ct);
        return PaymentStateOf(collected.GetValueOrDefault(bookingId), total);
    }

    /// <summary>The money collected for each booking: its completed and partially refunded payments, net of the refunds.</summary>
    internal static async Task<Dictionary<Guid, decimal>> CollectedByBookingAsync(AppDbContext db, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return [];

        return await db.Payments
            .AsNoTracking()
            .Where(p => bookingIds.Contains(p.BookingId) && (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded))
            .GroupBy(p => p.BookingId)
            .Select(g => new { BookingId = g.Key, Collected = g.Sum(p => p.Amount - p.RefundedAmount) })
            .ToDictionaryAsync(x => x.BookingId, x => x.Collected, ct);
    }

    /// <summary>A document is <c>Paid</c> when what was collected for its booking reaches its total, else <c>ToCollect</c>.</summary>
    internal static FiscalDocumentPaymentState PaymentStateOf(decimal collected, decimal total) =>
        collected >= total ? FiscalDocumentPaymentState.Paid : FiscalDocumentPaymentState.ToCollect;

    /// <summary>
    /// The payment state of the documents CasaZen issued is the current one, read from the payments, not the one of the day the draft was made (an
    /// Octorate document keeps the state Octorate gave it). Applied on the rows read without tracking: nothing is saved.
    /// </summary>
    internal static async Task ApplyCurrentPaymentStateAsync(AppDbContext db, IReadOnlyCollection<FiscalDocument> documents, CancellationToken ct)
    {
        var own = documents.Where(d => IsPaymentStateReadFromPayments(d.Source, d.Kind, d.BookingId)).ToList();
        if (own.Count == 0)
            return;

        var collected = await CollectedByBookingAsync(db, own.Select(d => d.BookingId!.Value).Distinct().ToList(), ct);
        foreach (var document in own)
            document.PaymentState = PaymentStateOf(collected.GetValueOrDefault(document.BookingId!.Value), document.Total);
    }

    /// <summary>
    /// Whether the payment state of a document is read from the payments of its booking: the documents CasaZen issued for a booking, except a credit note
    /// (which credits money and is not collected). Any other document keeps the state it was given. The one rule of the list, the detail and the export.
    /// </summary>
    internal static bool IsPaymentStateReadFromPayments(FiscalDocumentSource source, DocumentKind kind, Guid? bookingId) =>
        source == FiscalDocumentSource.CasaZen && kind != DocumentKind.CreditNote && bookingId is not null;

    // ---------------------------------------------------------------- reads

    private async Task<FiscalDocument> RequireAsync(Guid documentId, CancellationToken ct) =>
        await db.FiscalDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw NotFound(documentId);

    private async Task<BookingFacts> BookingOfAsync(Guid? bookingId, CancellationToken ct)
    {
        if (bookingId is not { } id)
            throw new DomainRuleException(FiscalDocumentErrors.BookingNotDirect, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.BookingNotDirect));

        return await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new BookingFacts(
                b.Id, b.OrgId, b.PropertyId, b.Source, b.BasePrice, b.CleaningFee, b.TouristTax, b.Property.Currency, b.CheckInDate, b.CheckOutDate, b.Status, b.NoShowAt))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Booking {id} was not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
    }

    private static void CopyRecipient(InvoiceDataView data, FiscalDocument document)
    {
        document.RecipientKind = data.RecipientKind;
        document.RecipientName = data.Name;
        document.RecipientFiscalCode = data.FiscalCode;
        document.RecipientVatNumber = data.VatNumber;
        document.RecipientAddressStreet = data.AddressStreet;
        document.RecipientAddressCivic = data.AddressCivic;
        document.RecipientPostalCode = data.PostalCode;
        document.RecipientCity = data.City;
        document.RecipientProvince = data.Province;
        document.RecipientCountryCode = data.CountryCode;
        document.RecipientSdiCode = data.SdiRecipientCode;
        document.RecipientPec = data.Pec;
    }

    private static void CopyRecipient(FiscalDocument from, FiscalDocument to)
    {
        to.RecipientKind = from.RecipientKind;
        to.RecipientName = from.RecipientName;
        to.RecipientFiscalCode = from.RecipientFiscalCode;
        to.RecipientVatNumber = from.RecipientVatNumber;
        to.RecipientAddressStreet = from.RecipientAddressStreet;
        to.RecipientAddressCivic = from.RecipientAddressCivic;
        to.RecipientPostalCode = from.RecipientPostalCode;
        to.RecipientCity = from.RecipientCity;
        to.RecipientProvince = from.RecipientProvince;
        to.RecipientCountryCode = from.RecipientCountryCode;
        to.RecipientSdiCode = from.RecipientSdiCode;
        to.RecipientPec = from.RecipientPec;
    }

    private static FiscalDocumentLine CopyLine(FiscalDocument document, FiscalDocumentLine line) => new()
    {
        OrgId = document.OrgId,
        DocumentId = document.Id,
        LineNumber = line.LineNumber,
        LineType = line.LineType,
        Description = line.Description,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        TotalPrice = line.TotalPrice,
        VatRatePercent = line.VatRatePercent,
        VatNatureCode = line.VatNatureCode,
        VatAmount = line.VatAmount,
    };

    /// <summary>The VAT id of a recipient: its VAT number (the country prefix taken off), or, abroad without one, the code of a foreign party.</summary>
    private static FatturaPaId? RecipientVatId(string? vatNumber, string country, string? fiscalCode)
    {
        if (!string.IsNullOrWhiteSpace(vatNumber))
        {
            var trimmed = vatNumber.Trim().ToUpperInvariant();
            var prefixed = trimmed.Length > 2 && char.IsAsciiLetter(trimmed[0]) && char.IsAsciiLetter(trimmed[1]);
            return new FatturaPaId(prefixed ? trimmed[..2] : country.ToUpperInvariant(), prefixed ? trimmed[2..] : trimmed);
        }

        return !string.Equals(country, "IT", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(fiscalCode)
            ? new FatturaPaId(country.ToUpperInvariant(), "99999999999")
            : null;
    }

    private static string? DigitsOf(string? vatNumber)
    {
        if (string.IsNullOrWhiteSpace(vatNumber))
            return null;

        var trimmed = vatNumber.Trim().ToUpperInvariant();
        return trimmed.StartsWith("IT", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }

    /// <summary>A person's name as first and last name: the last word is the last name.</summary>
    private static (string First, string Last) SplitName(string? name)
    {
        var words = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (words[0], words[0]),
            _ => (string.Join(' ', words[..^1]), words[^1]),
        };
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

    private static NotFoundException NotFound(Guid documentId) =>
        new($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };

    private static DomainRuleException ReadOnly() => new(FiscalDocumentErrors.ReadOnly, "FiscalDocumentReadOnly");

    private sealed record BookingFacts(
        Guid Id,
        Guid OrgId,
        Guid PropertyId,
        BookingSource Source,
        decimal BasePrice,
        decimal CleaningFee,
        decimal TouristTax,
        string? Currency,
        DateTime CheckIn,
        DateTime CheckOut,
        BookingStatus Status,
        DateTime? NoShowAt)
    {
        /// <summary>The nights of the stay (calendar days between check-in and check-out).</summary>
        public int Nights => Math.Max(0, (CheckOut.Date - CheckIn.Date).Days);

        /// <summary>The date that chooses the tax rule of a document of the stay: its check-out.</summary>
        public DateOnly StayDate => DateOnly.FromDateTime(CheckOut);
    }
}
