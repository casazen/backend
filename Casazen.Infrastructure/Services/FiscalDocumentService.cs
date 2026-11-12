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
    IOptions<FiscalDocumentRulesOptions> options,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentService> logger) : IFiscalDocumentService
{
    /// <summary>The descriptions of the lines: fixed words of the document, never personal data.</summary>
    private const string LodgingKey = "FiscalDocLineLodging";
    private const string CleaningKey = "FiscalDocLineCleaning";
    private const string TouristTaxKey = "FiscalDocLineTouristTax";
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
        var rule = await issuers.RequireRuleAsync(booking.OrgId, ct);
        var (profile, org) = await RequireIssuerAsync(booking.OrgId, ct);
        await RequireNoOtherDocumentAsync(booking.Id, document.Id, ct);

        var today = timeProvider.TodayInRomeAsDateOnly();
        var series = SeriesOf(profile, rule, today);
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
        var rule = await issuers.RequireRuleAsync(booking.OrgId, ct);
        var (profile, org) = await RequireIssuerAsync(booking.OrgId, ct);

        var note = new FiscalDocument
        {
            OrgId = original.OrgId,
            BookingId = original.BookingId,
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
            var amounts = FiscalDocumentTotals.FromGross(FiscalDocumentTotals.ScaleForCredit(GrossLinesOf(original), value), Stamp());
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

    public async Task<FiscalDocument?> GetAsync(Guid documentId, CancellationToken ct = default) =>
        await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct);

    public async Task<FiscalDocumentPage> ListAsync(HostScope scope, FiscalDocumentCriteria criteria, int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, FiscalDocumentPage.MaxPageSize);

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
            var start = RomeCalendar.StartOfDayUtc(from);
            query = query.Where(d => d.IssueDate != null ? d.IssueDate >= from : d.CreatedAt >= start);
        }

        if (criteria.To is { } to && to != DateOnly.MaxValue)
        {
            var end = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(d => d.IssueDate != null ? d.IssueDate <= to : d.CreatedAt < end);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(d => d.IssueDate)
            .ThenByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new FiscalDocumentPage(items, total, page, pageSize);
    }

    public async Task<string> GetXmlAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw NotFound(documentId);
        if (document.Source != FiscalDocumentSource.CasaZen || document.Status != FiscalDocumentStatus.Issued || string.IsNullOrEmpty(document.FatturaPaXml))
            throw new NotFoundException($"Fiscal document {documentId} has no XML") { Code = FiscalDocumentErrors.XmlUnavailable, MessageKey = "FiscalDocumentXmlUnavailable" };

        return document.FatturaPaXml;
    }

    public async Task<byte[]> GetPdfAsync(Guid documentId, string language, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw NotFound(documentId);
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw ReadOnly();

        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == document.OrgId, ct);
        var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == document.OrgId, ct);
        var credited = document.CreditedDocumentId is { } creditedId
            ? await db.FiscalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == creditedId, ct)
            : null;
        return pdfRenderer.Render(FiscalDocumentPdfs.Pdf(document, profile, org, texts, language, credited));
    }

    // ---------------------------------------------------------------- draft

    /// <summary>The draft made from the booking: the checks of the issuer, the rule, the profile and the invoice data, then the lines and the amounts.</summary>
    private async Task<FiscalDocument> RebuildAsync(FiscalDocument draft, CancellationToken ct, bool isNew = false)
    {
        var booking = await BookingOfAsync(draft.BookingId, ct);
        await RequireCasazenIssuesAsync(booking, ct);
        var rule = await issuers.RequireRuleAsync(booking.OrgId, ct);
        await RequireIssuerAsync(booking.OrgId, ct);
        if (isNew)
            await RequireNoOtherDocumentAsync(booking.Id, draft.Id, ct);

        var recipient = await invoiceData.GetAsync(booking.Id, ct);
        var missing = MissingInvoiceData(recipient);
        if (missing.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentErrors.InvoiceDataMissing, "FiscalDocumentInvoiceDataMissing")
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = missing },
            };
        }

        var kind = rule.DocumentKind == RuleDocumentKind.Receipt ? DocumentKind.Receipt : DocumentKind.Invoice;
        var amounts = FiscalDocumentTotals.FromGross(GrossLinesOf(booking, rule, kind), Stamp());

        draft.OrgId = booking.OrgId;
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

    /// <summary>The lines of a booking as the guest pays them: lodging and cleaning at the VAT of the rule, the tourist tax without VAT.</summary>
    private IReadOnlyList<FiscalDocumentGrossLine> GrossLinesOf(BookingFacts booking, FiscalDocumentRule rule, DocumentKind kind)
    {
        var language = DocumentLanguages.Italian;
        var rate = kind == DocumentKind.Receipt ? 0m : rule.VatRatePercent ?? 0m;
        var nature = rate == 0m && kind != DocumentKind.Receipt ? rule.VatNatureCode : null;
        var lines = new List<FiscalDocumentGrossLine>();
        var lodging = booking.BasePrice - booking.CleaningFee;
        if (lodging > 0m)
            lines.Add(new FiscalDocumentGrossLine(texts.Get(LodgingKey, language, booking.Nights), 1m, lodging, rate, nature));
        if (booking.CleaningFee > 0m)
            lines.Add(new FiscalDocumentGrossLine(texts.Get(CleaningKey, language), 1m, booking.CleaningFee, rate, nature));
        if (booking.TouristTax > 0m)
        {
            lines.Add(new FiscalDocumentGrossLine(
                texts.Get(TouristTaxKey, language), 1m, booking.TouristTax, 0m, kind == DocumentKind.Receipt ? null : rule.TouristTaxNatureCode));
        }

        return lines;
    }

    /// <summary>The lines of an issued document as the guest paid them (net plus its share of the VAT), for a partial credit note.</summary>
    private static IReadOnlyList<FiscalDocumentGrossLine> GrossLinesOf(FiscalDocument document)
    {
        var lines = document.Lines.Where(l => l.TotalPrice > 0m).OrderBy(l => l.LineNumber).ToList();
        var taxed = lines.Where(l => l.VatRatePercent > 0m).ToList();
        var taxedNet = taxed.Sum(l => l.TotalPrice);
        var gross = lines.Select(l => new FiscalDocumentGrossLine(
                l.Description,
                1m,
                l.VatRatePercent > 0m && taxedNet > 0m
                    ? l.TotalPrice + Math.Round(document.VatAmount * l.TotalPrice / taxedNet, 2, MidpointRounding.AwayFromZero)
                    : l.TotalPrice,
                l.VatRatePercent,
                l.VatNatureCode))
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
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                TotalPrice = line.TotalPrice,
                VatRatePercent = line.VatRatePercent,
                VatNatureCode = line.NatureCode,
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

    private FiscalStampDutyRule Stamp() =>
        new(options.Value.StampDutyThreshold, options.Value.StampDutyAmount, options.Value.StampDutyChargedToRecipient);

    // ---------------------------------------------------------------- issue

    private async Task IssueInTransactionAsync(
        FiscalDocument document,
        BookingFacts booking,
        FiscalDocumentRule rule,
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
        document.Series = number.Series;
        document.SequenceNumber = number.Number;
        document.Number = number.Display;
        document.IssueDate = today;

        if (document.Kind is DocumentKind.Invoice or DocumentKind.CreditNote)
        {
            document.FatturaPaXml = xmlBuilder.Build(FatturaPaInputOf(document, profile, org, rule, credited));
            document.SdiStatus = FiscalDocumentSdiStatuses.ToSend;
        }
        else
        {
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

    private static FatturaPaInput FatturaPaInputOf(FiscalDocument document, OrgFiscalIssuerProfile profile, Org org, FiscalDocumentRule rule, FiscalDocument? credited)
    {
        var lines = document.Lines.OrderBy(l => l.LineNumber).ToList();
        var groups = lines.GroupBy(l => (l.VatRatePercent, Nature: l.VatRatePercent == 0m ? l.VatNatureCode : null)).ToList();
        var taxedGroups = groups.Count(g => g.Key.VatRatePercent > 0m);
        var summary = groups
            .Select(g =>
            {
                var taxable = g.Sum(l => l.TotalPrice);
                var tax = g.Key.VatRatePercent == 0m ? 0m
                    : taxedGroups == 1 ? document.VatAmount
                    : Math.Round(taxable * g.Key.VatRatePercent / 100m, 2, MidpointRounding.AwayFromZero);
                return new FiscalDocumentVatSummary(g.Key.VatRatePercent, g.Key.Nature, taxable, tax);
            })
            .OrderByDescending(s => s.VatRatePercent)
            .ToList();

        var vatNumber = DigitsOf(org.PartitaIvaNumber);
        var issuerCountry = string.IsNullOrWhiteSpace(profile.CountryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : profile.CountryCode;
        var issuer = new FatturaPaParty(
            vatNumber is null ? null : new FatturaPaId(issuerCountry, vatNumber),
            org.FiscalCode,
            profile.LegalName,
            null,
            null,
            new FatturaPaAddress(profile.AddressStreet ?? string.Empty, profile.AddressCivic, profile.PostalCode ?? string.Empty, profile.City ?? string.Empty, profile.Province, issuerCountry),
            profile.FatturaPaRegimeCode ?? rule.FatturaPaRegimeCode);

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
            new FatturaPaId(issuerCountry, org.FiscalCode ?? vatNumber ?? string.Empty),
            document.Id.ToString("N")[..10].ToUpperInvariant(),
            issuer,
            recipient,
            document.RecipientSdiCode,
            document.RecipientPec,
            document.Kind == DocumentKind.CreditNote ? rule.CreditNoteTypeCode : rule.InvoiceTypeCode,
            document.Number!,
            document.IssueDate!.Value,
            document.Currency,
            lines.Select(l => new FiscalDocumentLineAmounts(l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.TotalPrice, l.VatRatePercent, l.VatNatureCode)).ToList(),
            summary,
            document.StampDuty,
            document.Total,
            credited is null ? null : new FatturaPaRelatedDocument(credited.Number ?? string.Empty, credited.IssueDate));
    }

    // ---------------------------------------------------------------- rules

    private async Task RequireCasazenIssuesAsync(BookingFacts booking, CancellationToken ct)
    {
        // The one definition of a direct booking first: never an OTA booking, whatever the setting says.
        if (!FiscalDocumentRules.IsDirectSource(booking.Source))
            throw new DomainRuleException(FiscalDocumentErrors.BookingNotDirect, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.BookingNotDirect));

        var decision = await issuers.ResolveIssuerAsync(booking.Id, ct);
        switch (decision.Kind)
        {
            case IssuerDecisionKind.CasaZen:
                return;
            case IssuerDecisionKind.None when decision.Reason == IssuerReasons.IssuanceOff:
                throw new DomainRuleException(FiscalDocumentErrors.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));
            case IssuerDecisionKind.None when decision.Reason == IssuerReasons.BookingNotDirect:
                throw new DomainRuleException(FiscalDocumentErrors.BookingNotDirect, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.BookingNotDirect));
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

    /// <summary>One invoice (or receipt) per booking, unless it was credited in full: 409 <c>document_already_issued</c>.</summary>
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

    /// <summary>The fields of the invoice data a document needs: the name, the identification of the recipient and the address.</summary>
    internal static IReadOnlyList<string> MissingInvoiceData(InvoiceDataView? data)
    {
        if (data is null)
            return [InvoiceDataFields.Name];

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(data.Name))
            missing.Add(InvoiceDataFields.Name);
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

    private string SeriesOf(OrgFiscalIssuerProfile profile, FiscalDocumentRule rule, DateOnly today) =>
        FiscalNumberFormat.SeriesFor(string.IsNullOrWhiteSpace(profile.DefaultSeries) ? rule.SeriesPattern : profile.DefaultSeries, today);

    private async Task<FiscalDocumentPaymentState> PaymentStateAsync(Guid bookingId, decimal total, CancellationToken ct)
    {
        var collected = await db.Payments
            .AsNoTracking()
            .Where(p => p.BookingId == bookingId && (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded))
            .SumAsync(p => p.Amount - p.RefundedAmount, ct);
        return collected >= total ? FiscalDocumentPaymentState.Paid : FiscalDocumentPaymentState.ToCollect;
    }

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
                b.Id, b.OrgId, b.Source, b.BasePrice, b.CleaningFee, b.TouristTax, b.Property.Currency, b.CheckInDate, b.CheckOutDate))
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
        Description = line.Description,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        TotalPrice = line.TotalPrice,
        VatRatePercent = line.VatRatePercent,
        VatNatureCode = line.VatNatureCode,
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
        BookingSource Source,
        decimal BasePrice,
        decimal CleaningFee,
        decimal TouristTax,
        string? Currency,
        DateTime CheckIn,
        DateTime CheckOut)
    {
        /// <summary>The nights of the stay (calendar days between check-in and check-out).</summary>
        public int Nights => Math.Max(0, (CheckOut.Date - CheckIn.Date).Days);
    }
}
