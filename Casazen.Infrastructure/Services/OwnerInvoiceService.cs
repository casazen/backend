using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using RuleDocumentKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerInvoiceService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The lines are the version's.</b> They are made from the ledger lines of the owner and month posted up to the cut-off of the version (a reversal
/// and the line it reverses cancel out), grouped: the commission of each property; one recharge for each flat fee of a stay, each approved charge, the card
/// fees of each property passed on by the mandate and the commissions of each portal the agency holds the account of (⚖ I10, I11); one expense advanced for
/// each expense of the owner (art. 15, outside the VAT). The VAT of each line comes from the rule of the last day of the month by line type.</item>
/// <item><b>The VAT of the mandate (⚖ R4)</b> is read from the mandate of the owner in force on the last day of the month (the default «+ IVA» without one).</item>
/// <item><b>The issue</b> makes the lines again from the version and refuses a draft whose money is no longer the version's (409 <c>document_draft_outdated</c>);
/// then the issue of the fiscal documents numbers it and posts its VAT on top to the ledger, once.</item>
/// <item><b>Logs</b> carry ids, codes and counts: never a value of the owner or an amount.</item>
/// </list>
/// </remarks>
public sealed class OwnerInvoiceService(
    AppDbContext db,
    IFiscalDocumentService documents,
    IDocumentIssuerService issuers,
    IFiscalTaxRuleResolver taxRules,
    IPortalAccountHolderResolver holders,
    IDocumentTexts texts,
    TimeProvider time,
    ILogger<OwnerInvoiceService> logger) : IOwnerInvoiceService
{
    /// <summary>The most invoices one call of the list answers (the newest periods first).</summary>
    public const int MaxListRows = 1000;

    /// <summary>The language of the lines of a document (the descriptions are stored with it, as for the documents of the guests).</summary>
    private const string LineLanguage = DocumentLanguages.Italian;

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<FiscalDocument> CreateDraftAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default)
    {
        var version = await db.OwnerStatementVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct)
            ?? throw VersionNotFound(versionId);
        if (version.Status == OwnerStatementVersionStatus.Superseded)
            throw new DomainConflictException(OwnerInvoiceErrors.VersionSuperseded, "OwnerInvoiceVersionSuperseded");

        var profile = await RequireIssuerAsync(orgId, ct);
        var composition = await ComposeAsync(version, profile, ct);
        var (draft, created) = await SaveDraftAsync(version, composition, userId, ct);
        logger.LogInformation(
            "Owner invoice draft {DocumentId} of owner {OwnerId} for {Year}-{Month} {Action} from version {VersionId}",
            draft.Id, version.PropertyOwnerId, version.PeriodYear, version.PeriodMonth, created ? "made" : "made again", version.Id);
        return draft;
    }

    public async Task<FiscalDocument> IssueAsync(Guid orgId, Guid documentId, bool confirm, string? userId, CancellationToken ct = default)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");

        var draft = await db.FiscalDocuments.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Kind != DocumentKind.CreditNote, ct)
            ?? throw DocumentNotFound(documentId);
        if (draft.Status != FiscalDocumentStatus.Draft)
            throw new DomainRuleException(FiscalDocumentErrors.NotDraft, "FiscalDocumentNotDraft");

        // The draft is issued only while it is still what its version says: the version not superseded, and the lines, the rates and the recipient those the
        // version, the mandate, the holders and the rule give today. Otherwise the host confirmed other amounts: the draft is made again first.
        var version = draft.OwnerStatementVersionId is { } versionId
            ? await db.OwnerStatementVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct)
            : null;
        if (version is null || version.Status == OwnerStatementVersionStatus.Superseded)
            throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");

        var profile = await RequireIssuerAsync(orgId, ct);
        var fresh = await ComposeAsync(version, profile, ct);
        if (!IsTheSameDraft(draft, fresh))
            throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");

        return await documents.IssueOwnerInvoiceAsync(draft.Id, fresh.VatToPost, userId, ct);
    }

    public async Task<FiscalDocument> CreateCreditNoteAsync(
        Guid orgId,
        Guid documentId,
        decimal? amount,
        string reason,
        bool confirm,
        string? userId,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");

        // The document is an owner invoice of this org (the web layer decided who may call: the whole organization with agency.statement.manage); the credit note
        // gives the VAT back to the owner ledger in its own transaction.
        await RequireOwnerInvoiceOfAsync(orgId, documentId, ct);
        var note = await documents.CreateOwnerInvoiceCreditNoteAsync(documentId, amount, reason, confirm, userId, ct);
        logger.LogInformation("Credit note {CreditNoteId} of owner invoice {DocumentId} issued by {UserId}", note.Id, documentId, userId);
        return note;
    }

    public async Task DiscardDraftAsync(Guid orgId, Guid documentId, CancellationToken ct = default)
    {
        await RequireOwnerInvoiceOfAsync(orgId, documentId, ct);
        await documents.DiscardOwnerInvoiceDraftAsync(documentId, ct);
    }

    public async Task<IReadOnlyList<OwnerInvoiceRow>> ListAsync(Guid orgId, OwnerInvoiceCriteria criteria, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var query = db.FiscalDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Kind != DocumentKind.CreditNote
                && d.Status != FiscalDocumentStatus.Discarded && d.RecipientOwnerId != null);
        if (criteria.Year is { } year && criteria.Month is { } month)
            query = query.Where(d => d.PeriodYear == year && d.PeriodMonth == month);
        if (criteria.OwnerId is { } ownerId)
            query = query.Where(d => d.RecipientOwnerId == ownerId);

        var invoices = await query
            .OrderByDescending(d => d.PeriodYear)
            .ThenByDescending(d => d.PeriodMonth)
            .ThenByDescending(d => d.CreatedAt)
            .Take(MaxListRows)
            .ToListAsync(ct);
        var settlements = await OwnerInvoiceSettlementReader.ReadAsync(db, invoices, ct);

        return invoices
            .OrderByDescending(d => d.PeriodYear)
            .ThenByDescending(d => d.PeriodMonth)
            .ThenBy(d => d.RecipientName, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new OwnerInvoiceRow(
                d.Id,
                d.RecipientOwnerId!.Value,
                d.RecipientName,
                d.PeriodYear ?? 0,
                d.PeriodMonth ?? 0,
                d.OwnerStatementVersionId,
                d.Number,
                d.IssueDate,
                d.Status,
                d.Kind,
                d.TaxableBase,
                d.VatAmount,
                d.StampDuty,
                d.Total,
                d.Currency,
                d.CreditedInFull,
                settlements.GetValueOrDefault(d.Id)))
            .ToList();
    }

    public async Task<OwnerInvoiceGeneration> CreateDraftsForPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            throw new DomainRuleException(OwnerAccountingErrors.PeriodInvalid, "OwnerPeriodInvalid");

        // An issuer that cannot issue stops the whole call before anything is written: every draft would be refused for the same reason.
        var profile = await RequireIssuerAsync(orgId, ct);
        RuleOf(profile, new DateOnly(year, month, 1).AddMonths(1).AddDays(-1));

        var versions = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PeriodYear == year && v.PeriodMonth == month && v.Status != OwnerStatementVersionStatus.Superseded)
            .ToListAsync(ct);
        var current = versions
            .GroupBy(v => v.PropertyOwnerId)
            .Select(g => g.MaxBy(v => v.VersionNumber)!)
            .OrderBy(v => v.PropertyOwnerId)
            .ToList();

        int created = 0, updated = 0, alreadyIssued = 0, nothing = 0;
        var failed = new List<OwnerInvoiceFailure>();
        foreach (var version in current)
        {
            var live = await LiveInvoiceAsync(orgId, version.PropertyOwnerId, year, month, tracked: false, ct);
            if (live is { Status: FiscalDocumentStatus.Issued })
            {
                alreadyIssued++;
                continue;
            }

            try
            {
                var composition = await ComposeAsync(version, profile, ct);
                var (_, made) = await SaveDraftAsync(version, composition, userId, ct);
                if (made)
                    created++;
                else
                    updated++;
            }
            catch (DomainRuleException ex) when (ex.Code == OwnerInvoiceErrors.NothingToInvoice)
            {
                nothing++;
            }
            catch (DomainException ex)
            {
                // The owner is told in the answer with the code of the reason; what this owner left in the unit of work is dropped so the next owner saves alone.
                db.ChangeTracker.Clear();
                failed.Add(new OwnerInvoiceFailure(version.PropertyOwnerId, ex.Code));
            }
        }

        logger.LogInformation(
            "Owner invoice drafts of org {OrgId} for {Year}-{Month} by {UserId}: {Owners} owners, {Created} made, {Updated} made again, {Issued} issued already, {Nothing} with nothing to invoice, {Failed} failed",
            orgId, year, month, userId, current.Count, created, updated, alreadyIssued, nothing, failed.Count);
        return new OwnerInvoiceGeneration(year, month, current.Count, created, updated, alreadyIssued, nothing, failed);
    }

    // ---------------------------------------------------------------- composition

    /// <summary>What a draft is made of: the rule and the kind, the amounts with the lines, the recipient and the VAT the issue posts to the ledger.</summary>
    private sealed record Composition(FiscalTaxRule Rule, DocumentKind Kind, FiscalDocumentAmounts Amounts, Recipient Recipient, decimal VatToPost);

    /// <summary>The owner as the recipient of the invoice: the fiscal profile in force on the last day of the month, else the registry of the owner.</summary>
    private sealed record Recipient(
        InvoiceRecipientKind Kind,
        string Name,
        string? FiscalCode,
        string? VatNumber,
        string? Street,
        string? Civic,
        string? PostalCode,
        string? City,
        string? Province,
        string? Country);

    /// <summary>A line before its VAT: what it is, where it goes in the invoice, its description and its amount as the ledger has it.</summary>
    private sealed record InvoiceLine(FiscalLineType Type, int Order, string Description, decimal Amount);

    /// <summary>What the descriptions of the lines name: the properties and the stays of the ledger lines.</summary>
    private sealed record LineFacts(IReadOnlyDictionary<Guid, string> Properties, IReadOnlyDictionary<Guid, StayFacts> Stays);

    private sealed record StayFacts(BookingSource Source, string BookingCode, DateTime CheckOut, string? ChannelLabel);

    private async Task<Composition> ComposeAsync(OwnerStatementVersion version, OrgFiscalIssuerProfile profile, CancellationToken ct)
    {
        var first = new DateOnly(version.PeriodYear, version.PeriodMonth, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var rule = RuleOf(profile, last);
        var kind = rule.DocumentKind == RuleDocumentKind.Receipt ? DocumentKind.Receipt : DocumentKind.Invoice;

        // The ledger lines the version is made of: the owner's lines of the month posted up to its cut-off.
        var entries = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == version.OrgId && e.PropertyOwnerId == version.PropertyOwnerId && e.PeriodYear == version.PeriodYear
                && e.PeriodMonth == version.PeriodMonth && e.PostedAt <= version.CutoffAt)
            .ToListAsync(ct);
        var facts = await LineFactsAsync(version.OrgId, entries, ct);
        var holderMap = await holders.ForOrgAsync(version.OrgId, ct);
        var lines = LinesOf(entries, facts, holderMap, FiscalDocumentPdfs.PeriodLabel(version.PeriodYear, version.PeriodMonth));
        if (lines.Count == 0)
            throw new DomainRuleException(OwnerInvoiceErrors.NothingToInvoice, "OwnerInvoiceNothingToInvoice");

        // ⚖ R4: «+ IVA» (the default) adds the VAT to the amounts of the ledger; «IVA inclusa» takes it out of them.
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == version.OrgId && m.PropertyOwnerId == version.PropertyOwnerId)
            .ToListAsync(ct);
        var mandate = OwnerMandateRules.InForce(mandates, version.PropertyOwnerId, null, last)
            ?? mandates.Where(m => OwnerMandateRules.Covers(m, last)).MaxBy(m => m.ValidFrom);
        var includesVat = mandate?.CommissionIncludesVat ?? false;

        var ordered = lines.OrderBy(l => l.Order).ThenBy(l => l.Description, StringComparer.Ordinal).ToList();
        var amounts = includesVat
            ? FiscalDocumentTotals.FromGross(
                ordered.Select(l => new FiscalDocumentGrossLine(l.Description, 1m, l.Amount, rule.RateOf(l.Type).Percent, rule.RateOf(l.Type).NatureCode, l.Type)).ToList(),
                rule.StampDuty)
            : FiscalDocumentTotals.FromNet(
                ordered.Select(l => new FiscalDocumentNetLine(l.Description, 1m, l.Amount, rule.RateOf(l.Type).Percent, rule.RateOf(l.Type).NatureCode, l.Type)).ToList(),
                rule.StampDuty);

        var recipient = await RecipientAsync(version.OrgId, version.PropertyOwnerId, last, ct);
        var missing = MissingRecipientData(recipient, FiscalDocumentService.NeedsOnlyTheName(rule, kind));
        if (missing.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentErrors.InvoiceDataMissing, "FiscalDocumentInvoiceDataMissing")
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = missing },
            };
        }

        return new Composition(rule, kind, amounts, recipient, includesVat ? 0m : amounts.VatAmount);
    }

    /// <summary>The lines of the invoice from the ledger lines of the version (see the remarks of the class): only what nets to an amount the agency charges.</summary>
    private List<InvoiceLine> LinesOf(IReadOnlyList<OwnerLedgerEntry> entries, LineFacts facts, PortalAccountHolders holderMap, string period)
    {
        var lines = new List<InvoiceLine>();

        // The commission of the agency, one line per property.
        foreach (var group in entries.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission).GroupBy(e => e.PropertyId))
            Add(FiscalLineType.Commission, 0, Text("OwnerInvoiceLineCommission", PropertyName(group.Key), period), group);

        // The flat fees of the mandate (E13): a recharge for each stay and kind.
        foreach (var group in entries
                     .Where(e => e.EntryType == OwnerLedgerEntryType.OwnerCharge && e.SourceType == OwnerLedgerSourceType.MandateFlatFee)
                     .GroupBy(e => (e.SourceId, e.SourceReference, e.PropertyId)))
        {
            var kind = group.Key.SourceReference == OwnerLedgerFlatFees.Linen ? Text("OwnerInvoiceFlatFeeLinen") : Text("OwnerInvoiceFlatFeeCleaning");
            Add(FiscalLineType.Recharge, 1, Text("OwnerInvoiceLineFlatFee", kind, PropertyName(group.Key.PropertyId), StayLabel(group.Key.SourceId)), group);
        }

        // The approved charges, and the charges of any other source: a recharge for each, with its own description and day.
        foreach (var group in entries
                     .Where(e => e.EntryType == OwnerLedgerEntryType.OwnerCharge && e.SourceType != OwnerLedgerSourceType.MandateFlatFee)
                     .GroupBy(e => (e.SourceType, e.SourceId, e.PropertyId)))
        {
            Add(FiscalLineType.Recharge, 2, Text("OwnerInvoiceLineCharge", DescriptionOf(group), PropertyName(group.Key.PropertyId), DayOf(group)), group);
        }

        // The card fees the mandate passes to the owner: a cost the agency bears in its own name, recharged with the VAT of a recharge, one line per property.
        foreach (var group in entries
                     .Where(e => e.EntryType == OwnerLedgerEntryType.OwnerExpense && e.SourceType == OwnerLedgerSourceType.Payment)
                     .GroupBy(e => e.PropertyId))
        {
            Add(FiscalLineType.Recharge, 3, Text("OwnerInvoiceLineCardFees", PropertyName(group.Key), period), group);
        }

        // The commissions of the portals whose account the agency holds (V2), when the organization recharges them (⚖ I10, I11): one line per portal. A listing
        // whose account the owner holds (V1, the default) has none: the portal invoices the owner itself.
        foreach (var group in entries
                     .Where(e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PropertyId is not null && e.SourceId is { } stay && facts.Stays.ContainsKey(stay))
                     .Select(e => (Entry: e, Stay: facts.Stays[e.SourceId!.Value]))
                     .Where(x => holderMap.RechargesCommissionOf(x.Entry.PropertyId!.Value, x.Stay.Source))
                     .GroupBy(x => x.Stay.Source))
        {
            Add(FiscalLineType.Recharge, 4, Text("OwnerInvoiceLinePortalCommissions", ChannelLabel(group.Key), period), group.Select(x => x.Entry));
        }

        // The expenses the agency advanced in the name and on behalf of the owner (art. 15, outside the VAT): one line for each.
        foreach (var group in entries
                     .Where(e => e.EntryType == OwnerLedgerEntryType.OwnerExpense && e.SourceType != OwnerLedgerSourceType.Payment)
                     .GroupBy(e => (e.SourceType, e.SourceId, e.PropertyId)))
        {
            Add(FiscalLineType.AdvancedExpense, 5, Text("OwnerInvoiceLineExpense", DescriptionOf(group), PropertyName(group.Key.PropertyId), DayOf(group)), group);
        }

        return lines;

        void Add(FiscalLineType type, int order, string description, IEnumerable<OwnerLedgerEntry> group)
        {
            // A debit of the owner is negative in the ledger; what nets to nothing (a reversed line) or to a credit is not something the agency charges.
            var amount = -group.Sum(e => e.Amount);
            if (amount > 0m)
                lines.Add(new InvoiceLine(type, order, Truncate(description, FiscalDocumentLine.DescriptionMaxLength), amount));
        }

        string PropertyName(Guid? propertyId) =>
            propertyId is { } id && facts.Properties.TryGetValue(id, out var name) ? name : string.Empty;

        string StayLabel(Guid? stayId) =>
            stayId is { } id && facts.Stays.TryGetValue(id, out var stay)
                ? Text("OwnerInvoiceStay", BookingCodes.Format(stay.BookingCode), DocumentLanguages.Day(DateOnly.FromDateTime(stay.CheckOut)))
                : string.Empty;

        string ChannelLabel(BookingSource source) => source switch
        {
            BookingSource.BookingCom => "Booking.com",
            BookingSource.Airbnb => "Airbnb",
            BookingSource.Expedia => "Expedia",
            BookingSource.Vrbo => "Vrbo",
            BookingSource.TripAdvisor => "Tripadvisor",
            BookingSource.Agoda => "Agoda",
            _ => Text("OwnerInvoiceOtherPortals"),
        };
    }

    private async Task<LineFacts> LineFactsAsync(Guid orgId, IReadOnlyList<OwnerLedgerEntry> entries, CancellationToken ct)
    {
        var propertyIds = entries.Where(e => e.PropertyId is not null).Select(e => e.PropertyId!.Value).Distinct().ToList();
        var properties = propertyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Properties.AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var stayIds = entries
            .Where(e => e.SourceId is not null && (e.EntryType == OwnerLedgerEntryType.ChannelFee || e.SourceType == OwnerLedgerSourceType.MandateFlatFee))
            .Select(e => e.SourceId!.Value)
            .Distinct()
            .ToList();
        var stays = stayIds.Count == 0
            ? new Dictionary<Guid, StayFacts>()
            : await db.Bookings.AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .Where(b => b.OrgId == orgId && stayIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => new StayFacts(b.Source, b.BookingCode, b.CheckOutDate, b.ChannelLabel), ct);

        return new LineFacts(properties, stays);
    }

    private async Task<Recipient> RecipientAsync(Guid orgId, Guid ownerId, DateOnly day, CancellationToken ct)
    {
        var owner = await db.PropertyOwners.AsNoTracking().FirstOrDefaultAsync(o => o.Id == ownerId && o.OrgId == orgId, ct)
            ?? throw new NotFoundException($"Owner {ownerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };
        var profile = await db.OwnerFiscalProfiles.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.ValidFrom <= day && (p.ValidTo == null || p.ValidTo >= day))
            .OrderByDescending(p => p.ValidFrom)
            .FirstOrDefaultAsync(ct);

        var entity = profile?.EntityKind ?? owner.EntityKind ?? OwnerEntityKind.NaturalPerson;
        var name = Clean(profile?.LegalName) ?? Clean(owner.LegalName) ?? Clean($"{owner.FirstName} {owner.LastName}") ?? string.Empty;
        var country = Clean(owner.BillingCountry)?.ToUpperInvariant() ?? Clean(profile?.TaxResidenceCountry)?.ToUpperInvariant() ?? Clean(owner.TaxResidenceCountry)?.ToUpperInvariant();
        var province = Clean(owner.BillingProvince)?.ToUpperInvariant();
        return new Recipient(
            entity == OwnerEntityKind.LegalEntity ? InvoiceRecipientKind.Company : InvoiceRecipientKind.Guest,
            Truncate(name, FiscalDocument.RecipientNameMaxLength),
            Cut(Clean(profile?.FiscalCode) ?? Clean(owner.FiscalCode), FiscalDocument.RecipientFiscalCodeMaxLength),
            Cut(Clean(profile?.VatNumber) ?? Clean(owner.VatNumber), FiscalDocument.RecipientVatNumberMaxLength),
            Cut(Clean(owner.BillingStreet), FiscalDocument.RecipientAddressStreetMaxLength),
            Cut(Clean(owner.BillingCivic), FiscalDocument.RecipientAddressCivicMaxLength),
            Cut(Clean(owner.BillingPostalCode), FiscalDocument.RecipientPostalCodeMaxLength),
            Cut(Clean(owner.BillingCity), FiscalDocument.RecipientCityMaxLength),
            province is { Length: FiscalDocument.RecipientProvinceMaxLength } ? province : null,
            country is { Length: FiscalDocument.RecipientCountryCodeMaxLength } ? country : null);
    }

    /// <summary>
    /// What the invoice still needs of its recipient: the name always; for an Italian electronic invoice also the identification and the address, as for a
    /// guest (<see cref="FiscalDocumentService.MissingInvoiceData"/>).
    /// </summary>
    private static List<string> MissingRecipientData(Recipient recipient, bool nameOnly)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(recipient.Name))
            missing.Add(InvoiceDataFields.Name);
        if (nameOnly)
            return missing;

        if (InvoiceDataFormats.MissingIdentification(recipient.Kind, recipient.Country, recipient.FiscalCode, recipient.VatNumber) is { } identification)
            missing.Add(identification);
        if (string.IsNullOrWhiteSpace(recipient.Street))
            missing.Add(InvoiceDataFields.AddressStreet);
        if (string.IsNullOrWhiteSpace(recipient.PostalCode))
            missing.Add(InvoiceDataFields.PostalCode);
        if (string.IsNullOrWhiteSpace(recipient.City))
            missing.Add(InvoiceDataFields.City);
        return missing;
    }

    // ---------------------------------------------------------------- the draft

    /// <summary>
    /// The draft written: the live invoice of the owner and period made again, or a new one. A new one that another request made first (the unique index of the
    /// live owner invoices) is read and made again instead. 409 <c>document_already_issued</c> when the live one is issued.
    /// </summary>
    private async Task<(FiscalDocument Draft, bool Created)> SaveDraftAsync(OwnerStatementVersion version, Composition composition, string? userId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await LiveInvoiceAsync(version.OrgId, version.PropertyOwnerId, version.PeriodYear, version.PeriodMonth, tracked: true, ct);
            if (existing is { Status: FiscalDocumentStatus.Issued })
                throw new DomainConflictException(FiscalDocumentErrors.AlreadyIssued, "FiscalDocumentAlreadyIssued");

            var draft = existing ?? new FiscalDocument
            {
                Source = FiscalDocumentSource.CasaZen,
                Status = FiscalDocumentStatus.Draft,
                CreatedAt = UtcNow,
                CreatedByUserId = userId,
            };
            Apply(draft, version, composition);
            if (existing is null)
            {
                db.FiscalDocuments.Add(draft);
            }
            else
            {
                // The lines of a draft that exists are new rows of a tracked document: added through their set, never left to the collection.
                foreach (var line in draft.Lines)
                    db.FiscalDocumentLines.Add(line);
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return (draft, existing is null);
            }
            catch (DbUpdateException ex) when (attempt == 0 && existing is null && OrgTeamAccess.IsUniqueViolation(ex, FiscalDocument.OwnerPeriodIndexName))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private void Apply(FiscalDocument draft, OwnerStatementVersion version, Composition composition)
    {
        var first = new DateOnly(version.PeriodYear, version.PeriodMonth, 1);
        draft.OrgId = version.OrgId;
        draft.BookingId = null;
        draft.PropertyId = null;
        draft.Purpose = FiscalDocumentPurpose.OwnerInvoice;
        draft.RecipientOwnerId = version.PropertyOwnerId;
        draft.OwnerStatementVersionId = version.Id;
        draft.PeriodYear = version.PeriodYear;
        draft.PeriodMonth = version.PeriodMonth;
        draft.ServicePeriodFrom = first;
        draft.ServicePeriodTo = first.AddMonths(1).AddDays(-1);
        draft.RuleVersion = composition.Rule.Version;
        draft.Kind = composition.Kind;
        draft.Currency = StayCurrency.Normalize(version.Currency);
        draft.PaymentChannel = FiscalPaymentChannel.Unknown;
        draft.PaymentState = FiscalDocumentPaymentState.Unknown;

        var recipient = composition.Recipient;
        draft.RecipientKind = recipient.Kind;
        draft.RecipientName = recipient.Name;
        draft.RecipientFiscalCode = recipient.FiscalCode;
        draft.RecipientVatNumber = recipient.VatNumber;
        draft.RecipientAddressStreet = recipient.Street;
        draft.RecipientAddressCivic = recipient.Civic;
        draft.RecipientPostalCode = recipient.PostalCode;
        draft.RecipientCity = recipient.City;
        draft.RecipientProvince = recipient.Province;
        draft.RecipientCountryCode = recipient.Country;
        draft.RecipientSdiCode = null;
        draft.RecipientPec = null;

        foreach (var line in draft.Lines.ToList())
            db.FiscalDocumentLines.Remove(line);
        draft.Lines.Clear();
        FiscalDocumentService.ApplyAmounts(draft, composition.Amounts);
        draft.UpdatedAt = UtcNow;
    }

    /// <summary>
    /// The draft still is what the version gives today: the kind, the rule version, the currency, the totals, the recipient and every line (type, net, rate,
    /// nature, VAT). The descriptions are not compared: a renamed property does not change what the owner owes.
    /// </summary>
    private static bool IsTheSameDraft(FiscalDocument draft, Composition fresh)
    {
        var amounts = fresh.Amounts;
        var recipient = fresh.Recipient;
        if (draft.Kind != fresh.Kind
            || !string.Equals(draft.RuleVersion, fresh.Rule.Version, StringComparison.Ordinal)
            || draft.TaxableBase != amounts.TaxableBase || draft.VatAmount != amounts.VatAmount || draft.StampDuty != amounts.StampDuty || draft.Total != amounts.Total
            || draft.RecipientKind != recipient.Kind
            || !Same(draft.RecipientName, recipient.Name) || !Same(draft.RecipientFiscalCode, recipient.FiscalCode) || !Same(draft.RecipientVatNumber, recipient.VatNumber)
            || !Same(draft.RecipientAddressStreet, recipient.Street) || !Same(draft.RecipientAddressCivic, recipient.Civic)
            || !Same(draft.RecipientPostalCode, recipient.PostalCode) || !Same(draft.RecipientCity, recipient.City)
            || !Same(draft.RecipientProvince, recipient.Province) || !Same(draft.RecipientCountryCode, recipient.Country))
        {
            return false;
        }

        var stored = draft.Lines.OrderBy(l => l.LineNumber).ToList();
        return stored.Count == amounts.Lines.Count
            && stored.Zip(amounts.Lines.OrderBy(l => l.LineNumber)).All(pair =>
                pair.First.LineType == pair.Second.LineType
                && pair.First.TotalPrice == pair.Second.TotalPrice
                && pair.First.VatRatePercent == pair.Second.VatRatePercent
                && Same(pair.First.VatNatureCode, pair.Second.NatureCode)
                && pair.First.VatAmount == pair.Second.VatAmount);

        static bool Same(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The live owner invoice of an owner and period (neither discarded, nor a credit note, nor credited in full), with its lines.</summary>
    private async Task<FiscalDocument?> LiveInvoiceAsync(Guid orgId, Guid ownerId, int year, int month, bool tracked, CancellationToken ct)
    {
        var query = db.FiscalDocuments.Include(d => d.Lines)
            .Where(d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.RecipientOwnerId == ownerId && d.PeriodYear == year
                && d.PeriodMonth == month && d.Status != FiscalDocumentStatus.Discarded && d.Kind != DocumentKind.CreditNote && !d.CreditedInFull);
        return await (tracked ? query : query.AsNoTracking()).OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync(ct);
    }

    // ---------------------------------------------------------------- rules and helpers

    private FiscalTaxRule RuleOf(OrgFiscalIssuerProfile profile, DateOnly lastDay) =>
        taxRules.Resolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, lastDay);

    /// <summary>The profile of the issuer, complete: 422 <c>issuer_profile_incomplete</c> (with the <c>fields</c>) otherwise, as for the documents of the guests.</summary>
    private async Task<OrgFiscalIssuerProfile> RequireIssuerAsync(Guid orgId, CancellationToken ct)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgId, ct);
        var settings = await issuers.GetSettingsAsync(orgId, ct);
        if (profile is null || settings.MissingFields.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentErrors.ProfileIncomplete, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ProfileIncomplete))
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = settings.MissingFields },
            };
        }

        return profile;
    }

    /// <summary>The document is an owner invoice of the org, or its credit note: 404 <c>fiscal_document_not_found</c> otherwise (another org, a guest document), whatever its state.</summary>
    private async Task RequireOwnerInvoiceOfAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        var found = await db.FiscalDocuments.AsNoTracking()
            .AnyAsync(d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice, ct);
        if (!found)
            throw DocumentNotFound(documentId);
    }

    private string Text(string key, params object[] args) => texts.Get(key, LineLanguage, args);

    private static string DescriptionOf(IEnumerable<OwnerLedgerEntry> group) =>
        group.Select(e => e.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))?.Trim() ?? string.Empty;

    private static string DayOf(IEnumerable<OwnerLedgerEntry> group) => DocumentLanguages.Day(group.Min(e => e.ServiceDate));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Cut(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static NotFoundException VersionNotFound(Guid versionId) =>
        new($"Owner statement version {versionId} was not found") { Code = OwnerAccountingErrors.VersionNotFound, MessageKey = "OwnerStatementVersionNotFound" };

    private static NotFoundException DocumentNotFound(Guid documentId) =>
        new($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };
}
