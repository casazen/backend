using System.Data;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// <item><b>Payment state.</b> A document is <c>Paid</c> when the completed payments of its booking, net of the refunds, reach its total; else <c>ToCollect</c>. For a stay of a portal
/// the host collects from, the amount the channel manager reports paid stands for the payments (<see cref="CollectedByBookingAsync"/>).</item>
/// <item><b>Owner invoices</b> (E14/F01/US01/T01) are made by <see cref="IOwnerInvoiceService"/> and issued here (<see cref="IssueOwnerInvoiceAsync"/>): the same
/// numbering (their own series), snapshot, rule version and XML, the rule of the last day of their period, and the VAT added on top posted to the owner ledger
/// in the transaction of the issue; their credit note posts its reversal. The endpoints of the guest documents neither make them again, issue, credit nor
/// discard them (422 <c>document_owner_invoice</c>): the owner invoices do, behind <c>agency.statement.manage</c> and their flag
/// (<see cref="CreateOwnerInvoiceCreditNoteAsync"/>, <see cref="DiscardOwnerInvoiceDraftAsync"/>).</item>
/// <item><b>The recipient.</b> The invoice data of the booking when it holds a name: it wins, as it always did. A booking with none (the portals and the site of the agency send
/// none) is made out to its guest for a document that needs only a name (a Swiss document of any kind, an Italian receipt): the name of the guest, nothing else of the guest
/// (<see cref="RecipientNameOfGuest"/>, FIX/DOC-RECIPIENT). An Italian invoice, a guest who asked for an invoice without giving its data and a guest who was erased never fall
/// back: 422 <c>document_invoice_data_missing</c>, as before. The issue reads the recipient again for a document that needs only a name: a draft that is no longer what the booking says
/// (invoice data with a name since, a guest erased since) is 409 <c>document_draft_outdated</c> (<see cref="RequireDraftOfTheRecipientAsync"/>).</item>
/// <item><b>Logs</b> carry ids, codes and counts: never a value of the recipient or an amount.</item>
/// </list>
/// </remarks>
public sealed partial class FiscalDocumentService(
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
    ILogger<FiscalDocumentService> logger,
    IGuestDocumentLineBuilder? lineBuilder = null,
    IInternalActivityService? activities = null,
    IPushNotificationService? pushNotifications = null,
    IFeatureFlags? featureFlags = null,
    IBillingSetupService? billingSetup = null,
    IOwnerAccountingService? ownerAccounting = null,
    IDocumentPlanService? documentPlans = null) : IFiscalDocumentService
{
    /// <summary>The description of the lines of a credit note: fixed words of the document, never personal data.</summary>
    private const string CreditKey = "FiscalDocLineCredit";

    /// <summary>
    /// The lines of the draft of a booking (E12/F01/US01/T01). The container gives the registered builder; a service built by hand without one (a test) gets a builder
    /// with no Swiss rule and no default portal, which makes nothing up.
    /// </summary>
    private readonly IGuestDocumentLineBuilder _lines = lineBuilder
        ?? new GuestDocumentLineBuilder(db, texts, Options.Create(new FiscalDocumentRulesOptions()), Options.Create(new SwissTouristTaxOptions()));

    /// <summary>The documents a booking needs and who issues each (E17/F01/US01/T02); a service built by hand without one (a test) reads the same plan.</summary>
    private readonly IDocumentPlanService _plans = documentPlans ?? new DocumentPlanService(db, taxRules);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The drafts of every document of the plan of the booking (E17/F01/US01/T02): the answer is the document of the stay (or the only one the call made). Nothing
    /// to make (every document of the plan is issued) is 409 <c>document_already_issued</c>, as before.
    /// </summary>
    public async Task<FiscalDocument> CreateDraftAsync(Guid bookingId, string? userId, CancellationToken ct = default)
    {
        var drafts = await MakeDraftsOfThePlanAsync(bookingId, userId, onlyMissing: false, ct);
        if (drafts.Count == 0)
            throw new DomainConflictException(FiscalDocumentErrors.AlreadyIssued, "FiscalDocumentAlreadyIssued");

        return drafts.FirstOrDefault(d => !d.ForAgencyServices) ?? drafts[0];
    }

    /// <summary>
    /// The drafts of the documents the booking needs (<see cref="IDocumentPlanService"/>), in the order of the plan: a draft of the same part (the stay, or the
    /// services of the agency) is made again from the booking (left as it is with <paramref name="onlyMissing"/>: the check-out event, the daily run); a part that a
    /// live document already bills, whoever issued it (<see cref="RequireNoOtherDocumentAsync"/>), is left alone; otherwise the draft is made. A draft the plan has no place for
    /// any more (the services of the agency once the cleaning follows the stay again) is discarded: it has no number. An owner with no issuer profile that can
    /// issue: nothing at all, its «Da fare», 422 <c>owner_issuer_missing</c>.
    /// </summary>
    private async Task<IReadOnlyList<FiscalDocument>> MakeDraftsOfThePlanAsync(Guid bookingId, string? userId, bool onlyMissing, CancellationToken ct)
    {
        var booking = await BookingOfAsync(bookingId, ct);
        await RequireCasazenIssuesAsync(booking, ct);
        var (organization, _) = await RequireIssuerAsync(booking.OrgId, ct);
        var plan = await RequirePlanAsync(booking, ct);

        var drafts = await db.FiscalDocuments
            .Include(d => d.Lines)
            .Where(d => d.BookingId == bookingId && d.Source == FiscalDocumentSource.CasaZen && d.Purpose == FiscalDocumentPurpose.GuestStay
                        && d.Status == FiscalDocumentStatus.Draft && d.Kind != DocumentKind.CreditNote)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        var made = new List<FiscalDocument>();
        foreach (var planned in plan.Documents)
        {
            var existing = drafts.FirstOrDefault(d => d.ForAgencyServices == planned.ForAgencyServices);
            if (existing is not null)
            {
                drafts.Remove(existing);
                if (!onlyMissing)
                    made.Add(await RebuildAsync(existing, ct, planned: planned));
                continue;
            }

            if (await HasBlockingDocumentAsync(booking.Id, Guid.Empty, PartOf(planned.ForAgencyServices, planned.IssuerOwnerId is not null), countPlannedCredit: true, ct))
                continue;

            var draft = new FiscalDocument
            {
                Source = FiscalDocumentSource.CasaZen,
                Status = FiscalDocumentStatus.Draft,
                CreatedAt = UtcNow,
                CreatedByUserId = userId,
                BookingId = bookingId,
            };
            made.Add(await RebuildAsync(draft, ct, isNew: true, planned: planned));
        }

        // The drafts of a part the plan no longer has: discarded (no number was given), never issued in the name of an issuer that no longer bills it. The replacement of an invoice the SDI
        // rejected holds the number of that invoice (E17/F01/US02/T03): it is never discarded (the number would be lost), the host is told instead.
        foreach (var stale in drafts.Where(d => plan.Of(d.ForAgencyServices) is null))
        {
            if (HoldsAReplacedNumber(stale))
            {
                logger.LogWarning("Draft {DocumentId} of booking {BookingId} holds the number of a rejected invoice: kept, the host is told the plan changed", stale.Id, booking.Id);
                await TellTheHostAsync(booking, stale.Id, FiscalDocumentTodos.BookingChangedReason, ct);
                continue;
            }

            stale.Status = FiscalDocumentStatus.Discarded;
            stale.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Draft {DocumentId} of booking {BookingId} discarded: the plan of the booking has no such document any more", stale.Id, booking.Id);
        }

        return made;
    }

    /// <summary>
    /// The plan of the booking; an owner without an issuer profile that can issue gets its «Da fare» (once while it is open) and the call is 422
    /// <c>owner_issuer_missing</c>: no document in the name of anybody else.
    /// </summary>
    private async Task<DocumentPlan> RequirePlanAsync(BookingFacts booking, CancellationToken ct)
    {
        var plan = await _plans.PlanAsync(booking.Id, ct);
        if (plan.OwnersWithoutIssuer.Count == 0)
            return plan;

        foreach (var ownerId in plan.OwnersWithoutIssuer)
            await TellOwnerIssuerMissingAsync(booking, ownerId, ct);

        logger.LogInformation("Booking {BookingId}: no document, {Count} owners without an issuer profile that can issue", booking.Id, plan.OwnersWithoutIssuer.Count);
        throw new DomainRuleException(FiscalDocumentErrors.OwnerIssuerMissing, "FiscalDocumentOwnerIssuerMissing")
        {
            Extensions = new Dictionary<string, object?> { ["ownerId"] = plan.OwnersWithoutIssuer[0] },
        };
    }

    /// <summary>The «Da fare» of an owner whose issuer profile is missing (no personal data: a code, the owner and the property); one open item per owner.</summary>
    private async Task TellOwnerIssuerMissingAsync(BookingFacts booking, Guid ownerId, CancellationToken ct)
    {
        if (activities is null)
            return;

        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.FiscalDocument,
                    Action = ActivityAction.ReviewFiscalDocument,
                    EntityType = FiscalDocumentTodos.OwnerEntityType,
                    EntityId = ownerId,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = booking.PropertyId,
                    OrgId = booking.OrgId,
                    Reason = FiscalDocumentTodos.OwnerIssuerMissingReason,
                },
                ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nobody to tell, and a retry would find the same.
            logger.LogInformation("Owner {OwnerId} of booking {BookingId}: property not found, no «To do» raised", ownerId, booking.Id);
        }
    }

    public async Task<FiscalDocument> RefreshDraftAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        RequireGuestDocument(document);

        // The draft of a credit note (a correction, E12/F01/US01/T03) is the credited document's amounts: there is nothing in the booking to make it again from.
        if (document.Kind == DocumentKind.CreditNote)
            return document;

        return await RebuildAsync(document, ct);
    }

    public Task DiscardDraftAsync(Guid documentId, CancellationToken ct = default) => DiscardAsync(documentId, ownerInvoice: false, ct);

    public Task DiscardOwnerInvoiceDraftAsync(Guid documentId, CancellationToken ct = default) => DiscardAsync(documentId, ownerInvoice: true, ct);

    private async Task DiscardAsync(Guid documentId, bool ownerInvoice, CancellationToken ct)
    {
        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        if (ownerInvoice)
            RequireOwnerInvoice(document);
        else
            RequireGuestDocument(document);
        RequireNotAHeldNumber(document);
        document.Status = FiscalDocumentStatus.Discarded;
        document.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Fiscal document draft {DocumentId} discarded", document.Id);
    }

    public Task<FiscalDocument> IssueAsync(Guid documentId, bool confirm, CancellationToken ct = default) =>
        IssueCoreAsync(documentId, confirm, notify: true, ct);

    /// <summary>
    /// The issue of a draft, the one path of the API, of the bulk issue and of the automatic issue. <paramref name="notify"/> tells the hosts of the booking that a document was issued
    /// (a push, the bell and the live message of the open pages, E12/F01/US01/T02): the issue of one document asked by a person and the corrections made by an event do, the bulk
    /// issue and the daily run do not (a notification for each of hundreds of documents is no help to anybody; their answer and the list tell).
    /// </summary>
    private async Task<FiscalDocument> IssueCoreAsync(Guid documentId, bool confirm, bool notify, CancellationToken ct)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");

        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        RequireGuestDocument(document);
        if (document.Kind == DocumentKind.CreditNote)
            return await IssueCreditNoteDraftAsync(document, notify, ct);

        var booking = await BookingOfAsync(document.BookingId, ct);
        await RequireCasazenIssuesAsync(booking, ct);
        var (organization, org) = await RequireIssuerAsync(booking.OrgId, ct);

        // The draft is still the document the plan wants (E17/F01/US01/T02): a draft made before the owner, the ownership or the mandate of today (or one of before
        // the owners issued, moved to the organization by the migration) is never issued in the name of an issuer that no longer bills it. An owner whose profile
        // cannot issue gets its «Da fare» (422 owner_issuer_missing); any other change is 409 document_draft_outdated, and the draft made again follows the plan.
        RequireDraftOfThePlan(document, (await RequirePlanAsync(booking, ct)).Of(document.ForAgencyServices), organization);

        // The issuer of the draft (E17/F01/US01/T02): an owner's draft is issued on the owner's profile, in the owner's series, with the owner's rule.
        var profile = await IssuerProfileAsync(booking, organization, document.IssuerProfileId, requireComplete: true, ct);
        var rule = RuleOf(profile, booking);
        RequireDraftOfTheRule(document, rule);
        await RequireDraftOfTheRecipientAsync(document, booking.Id, rule, ct);
        await RequireNoOtherDocumentAsync(booking.Id, document.Id, PartOf(document.ForAgencyServices, profile.IsOwnerProfile), ct);

        var today = timeProvider.TodayInRomeAsDateOnly();
        var series = SeriesOf(profile, today);
        var parties = await IssuerPartiesAsync(booking, profile, organization, org, ct);
        await IssueInTransactionAsync(document, booking.OrgId, booking.Id, rule, profile, org, series, today, credited: null, ct, parties: parties);
        logger.LogInformation("Fiscal document {DocumentId} of booking {BookingId} issued", document.Id, booking.Id);
        if (notify)
            TellTheHostsOfTheIssue(document, org);
        return document;
    }

    /// <summary>
    /// The profile a document of the booking is (or will be) issued on: the organization's, or an owner's of the same org (E17/F01/US01/T02). An owner's profile
    /// that can no longer issue (gone, or missing a field: <paramref name="requireComplete"/>) is 422 <c>owner_issuer_missing</c>, never the organization's in its
    /// place; a credit note only needs it to exist (it repeats the issuer of its original).
    /// </summary>
    private async Task<OrgFiscalIssuerProfile> IssuerProfileAsync(
        BookingFacts booking,
        OrgFiscalIssuerProfile organization,
        Guid? issuerProfileId,
        bool requireComplete,
        CancellationToken ct)
    {
        if (issuerProfileId is null || issuerProfileId == organization.Id)
            return organization;

        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == issuerProfileId && p.OrgId == booking.OrgId, ct);
        if (profile is not null && (!requireComplete || OwnerIssuerProfileRules.MissingFields(profile, RegimeCodeOfTheRule(profile, booking.StayDate)).Count == 0))
            return profile;

        throw new DomainRuleException(FiscalDocumentErrors.OwnerIssuerMissing, "FiscalDocumentOwnerIssuerMissing")
        {
            Extensions = new Dictionary<string, object?> { ["ownerId"] = profile?.PropertyOwnerId },
        };
    }

    /// <summary>The regime code of FatturaPA of the rule in force on <paramref name="day"/> for <paramref name="profile"/>, used when the profile has none of its own.</summary>
    private string? RegimeCodeOfTheRule(OrgFiscalIssuerProfile profile, DateOnly day) =>
        taxRules.TryResolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, day)?.FatturaPaRegimeCode;

    /// <summary>
    /// What the snapshot of an owner's document adds to the owner (E17/F01/US01/T02): the agency that issues through CasaZen on its behalf and the co-owners of the
    /// plan with their shares (⚖ I6). <c>null</c> for a document the organization issues in its own name.
    /// </summary>
    private async Task<IssuerParties?> IssuerPartiesAsync(
        BookingFacts booking,
        OrgFiscalIssuerProfile profile,
        OrgFiscalIssuerProfile organization,
        Org org,
        CancellationToken ct)
    {
        if (!profile.IsOwnerProfile)
            return null;

        var plan = await _plans.PlanAsync(booking.Id, ct);
        var coOwners = plan.Documents.FirstOrDefault(d => d.IssuerProfileId == profile.Id)?.CoOwners ?? [];
        return new IssuerParties(
            FiscalIssuerIntermediary.Of(organization, org),
            coOwners.Count > 1 ? coOwners.Select(c => new FiscalIssuerCoOwner(c.Name, c.SharePercent)).ToList() : null);
    }

    /// <summary>The parties an owner's document names beside the owner: the agency and the co-owners.</summary>
    private sealed record IssuerParties(FiscalIssuerIntermediary? Intermediary, IReadOnlyList<FiscalIssuerCoOwner>? CoOwners)
    {
        /// <summary>The parties of an issued document, for its credit note (the same issuer, as it was).</summary>
        public static IssuerParties? Of(FiscalDocument original)
        {
            var snapshot = FiscalIssuerSnapshot.FromJson(original.IssuerSnapshot);
            return snapshot?.Intermediary is null ? null : new IssuerParties(snapshot.Intermediary, snapshot.CoOwners);
        }
    }

    public async Task<FiscalDocument> IssueOwnerInvoiceAsync(Guid documentId, decimal vatToPost, string? userId, CancellationToken ct = default)
    {
        var document = await RequireAsync(documentId, ct);
        RequireCasazenDraft(document);
        if (document.Purpose != FiscalDocumentPurpose.OwnerInvoice || document.RecipientOwnerId is null || document.PeriodYear is null || document.PeriodMonth is null)
            throw NotFound(documentId);
        if (vatToPost > 0m && ownerAccounting is null)
            throw new InvalidOperationException("The owner accounting is needed to post the VAT of an owner invoice.");

        var (profile, org) = await RequireIssuerAsync(document.OrgId, ct);
        var rule = OwnerInvoiceRuleOf(profile, document);
        RequireDraftOfTheRule(document, rule);

        // The series of the owner invoices (E14), its own counter: never the one of the documents of the guests.
        var today = timeProvider.TodayInRomeAsDateOnly();
        var series = FiscalNumberFormat.SeriesFor(profile.OwnerInvoiceSeries ?? OrgFiscalIssuerProfile.DefaultOwnerInvoiceSeries, today);

        // The VAT added on top goes to the owner ledger in the transaction of the number: posted with the issue, or not at all (a second issue is refused
        // before, as the document is no longer a draft, and the line has a key of its own).
        OwnerLedgerPosting? posting = null;
        Func<CancellationToken, Task>? postVat = vatToPost > 0m
            ? async token => posting = await ownerAccounting!.PostOwnerInvoiceVatAsync(document, vatToPost, userId, token)
            : null;
        await IssueInTransactionAsync(document, document.OrgId, bookingId: null, rule, profile, org, series, today, credited: null, ct, postVat);
        logger.LogInformation("Owner invoice {DocumentId} issued", document.Id);
        if (posting is not null)
            await RegenerateOpenVersionAsync(document.OrgId, posting, userId, ct);
        return document;
    }

    /// <summary>
    /// The issue of the draft of a credit note (a correction, E12/F01/US01/T03): the draft is the credit note of an issued document of CasaZen, issued in the series of that document under the
    /// same lock as a credit note issued at once, so the cap (never more than what is left to credit) is checked again right after the number is reserved. A credit note is not refused for
    /// the state of its booking (it is what is done after a cancellation).
    /// </summary>
    private async Task<FiscalDocument> IssueCreditNoteDraftAsync(FiscalDocument note, bool notify, CancellationToken ct)
    {
        var original = note.CreditedDocumentId is { } creditedId ? await RequireAsync(creditedId, ct) : null;
        if (original is null
            || original.Source != FiscalDocumentSource.CasaZen
            || original.Status != FiscalDocumentStatus.Issued
            || original.Kind is DocumentKind.CreditNote or DocumentKind.Proforma)
        {
            throw new DomainRuleException(FiscalDocumentErrors.NotCreditable, "FiscalDocumentNotCreditable");
        }

        RequireNotRejected(original);
        var booking = await BookingOfAsync(note.BookingId, ct);
        var (organization, org) = await RequireIssuerAsync(booking.OrgId, ct);

        // The issuer of the credited document (E17/F01/US01/T02): an owner's reversal is the owner's, in the owner's series.
        var profile = await IssuerProfileAsync(booking, organization, original.IssuerProfileId, requireComplete: false, ct);
        var rule = RuleOf(profile, booking);
        var today = timeProvider.TodayInRomeAsDateOnly();
        await IssueInTransactionAsync(note, booking.OrgId, booking.Id, rule, profile, org, original.Series, today, original, ct, parties: IssuerParties.Of(original));
        logger.LogInformation("Credit note {CreditNoteId} of fiscal document {DocumentId} issued", note.Id, original.Id);
        if (notify)
            TellTheHostsOfTheIssue(note, org);
        return note;
    }

    public Task<FiscalDocument> CreateCreditNoteAsync(
        Guid documentId,
        decimal? amount,
        string reason,
        bool confirm,
        string? userId,
        CancellationToken ct = default) =>
        CreditNoteAsync(documentId, amount, reason, confirm, userId, ownerInvoice: false, ct);

    public Task<FiscalDocument> CreateOwnerInvoiceCreditNoteAsync(
        Guid documentId,
        decimal? amount,
        string reason,
        bool confirm,
        string? userId,
        CancellationToken ct = default) =>
        CreditNoteAsync(documentId, amount, reason, confirm, userId, ownerInvoice: true, ct);

    /// <summary>
    /// The credit note of a guest document (<paramref name="ownerInvoice"/> false: 422 <c>document_owner_invoice</c> for an owner invoice) or of an owner invoice
    /// (true: 404 for any other document): the one path of both, so the permission and the flag of the owner invoices cannot be skipped by the other endpoints.
    /// </summary>
    private async Task<FiscalDocument> CreditNoteAsync(
        Guid documentId,
        decimal? amount,
        string reason,
        bool confirm,
        string? userId,
        bool ownerInvoice,
        CancellationToken ct)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");
        if (ownerInvoice && ownerAccounting is null)
            throw new InvalidOperationException("The owner accounting is needed to give back the VAT of an owner invoice.");

        var original = await RequireAsync(documentId, ct);
        if (original.Source != FiscalDocumentSource.CasaZen)
            throw ReadOnly();
        if (ownerInvoice)
            RequireOwnerInvoice(original);
        else
            RequireGuestDocument(original);
        if (original.Status != FiscalDocumentStatus.Issued || original.Kind == DocumentKind.CreditNote || original.Kind == DocumentKind.Proforma)
            throw new DomainRuleException(FiscalDocumentErrors.NotCreditable, "FiscalDocumentNotCreditable");
        if (!ownerInvoice)
            RequireNotRejected(original);

        var credited = await CreditedSoFarAsync(original.Id, ct);
        var remaining = original.Total - credited;
        var value = amount ?? remaining;
        if (value <= 0m || value > remaining)
            throw new DomainRuleException(FiscalDocumentErrors.CreditAmountExceeds, "FiscalDocumentCreditAmountExceeds", remaining);

        // A document of a stay takes the rule of its check-out; an owner invoice the rule of the last day of its period (E14/F01/US01/T01). A document of a stay is
        // credited by its own issuer (E17/F01/US01/T02): an owner's credit note is the owner's.
        Guid? bookingId = null;
        FiscalTaxRule rule;
        var (organization, org) = await RequireIssuerAsync(original.OrgId, ct);
        var profile = organization;
        if (ownerInvoice)
        {
            if (original.PeriodYear is null || original.PeriodMonth is null || original.RecipientOwnerId is null)
                throw new DomainRuleException(FiscalDocumentErrors.NotCreditable, "FiscalDocumentNotCreditable");
            rule = OwnerInvoiceRuleOf(profile, original);
        }
        else
        {
            var booking = await BookingOfAsync(original.BookingId, ct);
            bookingId = booking.Id;
            profile = await IssuerProfileAsync(booking, organization, original.IssuerProfileId, requireComplete: false, ct);
            rule = RuleOf(profile, booking);
        }

        var note = BuildCreditNote(original, credited, value, reason, userId, rule);
        db.FiscalDocuments.Add(note);
        var today = timeProvider.TodayInRomeAsDateOnly();

        // The credit note of an owner invoice gives back, in the same transaction, the VAT the invoice posted to the owner ledger (E14/F01/US01/T01).
        OwnerLedgerPosting? posting = null;
        Func<CancellationToken, Task>? reverseVat = ownerInvoice
            ? async token => posting = await ownerAccounting!.ReverseOwnerInvoiceVatAsync(note, original, userId, token)
            : null;
        await IssueInTransactionAsync(note, original.OrgId, bookingId, rule, profile, org, original.Series, today, original, ct, reverseVat, IssuerParties.Of(original));
        logger.LogInformation("Credit note {CreditNoteId} of fiscal document {DocumentId} issued", note.Id, original.Id);
        if (posting is not null)
            await RegenerateOpenVersionAsync(original.OrgId, posting, userId, ct);
        TellTheHostsOfTheIssue(note, org);
        return note;
    }

    /// <summary>
    /// The draft credit note of <paramref name="value"/> (VAT included) of an issued document that <paramref name="credited"/> was credited of already: the whole document is its lines
    /// and amounts as stored, never recomputed; a part is spread over the lines of the credited document in proportion (the VAT split follows the original lines). The reason goes
    /// with every line («Storno del documento …: reason - line»): a credit note has no line of its own without an amount. Not added to the context: the caller adds it (and issues it,
    /// or leaves it as a draft for the host).
    /// </summary>
    private FiscalDocument BuildCreditNote(FiscalDocument original, decimal credited, decimal value, string reason, string? userId, FiscalTaxRule rule)
    {
        var note = new FiscalDocument
        {
            OrgId = original.OrgId,
            BookingId = original.BookingId,
            PropertyId = original.PropertyId,
            Purpose = original.Purpose,

            // A credit note is the issuer's own: the same profile, the same series and counter as the document it credits, and the same part of the booking.
            IssuerProfileId = original.IssuerProfileId,
            ForAgencyServices = original.ForAgencyServices,
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
            UpdatedAt = UtcNow,
            CreatedByUserId = userId,
        };
        CopyRecipient(original, note);

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

        foreach (var line in note.Lines)
            line.Description = Truncate($"{description} - {line.Description}", FiscalDocumentLine.DescriptionMaxLength);

        return note;
    }

    /// <summary>
    /// The statement version of the month an owner invoice posted a line in is generated again when it is a draft or flagged (an approved or published one is told by
    /// the readiness). The document is issued already: a refusal of the generation is logged, the month stays dirty and the agency generates it.
    /// </summary>
    internal async Task RegenerateOpenVersionAsync(Guid orgId, OwnerLedgerPosting posting, string? userId, CancellationToken ct)
    {
        if (ownerAccounting is null)
            return;

        try
        {
            await ownerAccounting.RegenerateOpenVersionAsync(orgId, posting.OwnerId, posting.Year, posting.Month, userId, ct);
        }
        catch (DomainException ex)
        {
            logger.LogWarning(
                "The statement version of owner {OwnerId} for {Year}-{Month} was not generated again after an owner invoice: {Code}",
                posting.OwnerId, posting.Year, posting.Month, ex.Code);
        }
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
                d.Id, d.BookingId, d.Kind, d.Number, d.IssueDate, d.RecipientName, d.TaxableBase, d.VatAmount, d.StampDuty, d.Total, d.Currency, d.PaymentState, d.SdiStatus, d.Source,
                d.PaymentChannel))
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
            documents.Where(d => IsPaymentStateReadFromPayments(d.Source, d.Kind, d.BookingId, d.PaymentChannel)).Select(d => d.BookingId!.Value).Distinct().ToList(),
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
                    IsPaymentStateReadFromPayments(d.Source, d.Kind, d.BookingId, d.PaymentChannel) ? PaymentStateOf(collected.GetValueOrDefault(d.BookingId!.Value), d.Total) : d.PaymentState,
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
        FiscalDocumentSource Source,
        FiscalPaymentChannel PaymentChannel);

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
        var credited = document.CreditedDocumentId is { } creditedId
            ? await db.FiscalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == creditedId, ct)
            : null;
        var issuer = document.Status == FiscalDocumentStatus.Issued ? FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot) : null;
        if (issuer is null)
        {
            // A draft shows the issuer it will be issued on today: its own profile (an owner who issues, E17), else the profile of the organization; an owner's
            // draft names the agency and the co-owners as the issue will.
            var profiles = db.OrgFiscalIssuerProfiles.AsNoTracking();
            var organization = await profiles.OfOrganization(document.OrgId).FirstOrDefaultAsync(ct);
            var profile = document.IssuerProfileId is { } issuerProfileId
                ? await profiles.FirstOrDefaultAsync(p => p.Id == issuerProfileId && p.OrgId == document.OrgId, ct)
                : organization;
            var org = await db.Orgs.AsNoTracking().FirstAsync(o => o.Id == document.OrgId, ct);
            issuer = FiscalIssuerSnapshot.From(profile, org);
            if (profile is { IsOwnerProfile: true } && document.BookingId is { } bookingId && organization is not null)
            {
                var parties = document.Kind == DocumentKind.CreditNote && credited is not null
                    ? IssuerParties.Of(credited)
                    : await IssuerPartiesAsync(await BookingOfAsync(bookingId, ct), profile, organization, org, ct);
                issuer = issuer with { Intermediary = parties?.Intermediary, CoOwners = parties?.CoOwners };
            }
        }

        var propertyName = document.PropertyId is { } propertyId
            ? await db.Properties.AsNoTracking().IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]).Where(p => p.Id == propertyId).Select(p => p.Name).FirstOrDefaultAsync(ct)
            : null;

        // An issued owner invoice says how it is paid, read now from the owner ledger: offset in the statement, or to pay (with the QR-bill in Switzerland).
        var settlement = document.Purpose == FiscalDocumentPurpose.OwnerInvoice && document.Status == FiscalDocumentStatus.Issued && document.Kind != DocumentKind.CreditNote
            ? (await OwnerInvoiceSettlementReader.ReadAsync(db, [document], ct)).GetValueOrDefault(document.Id)
            : null;
        return FiscalDocumentPdfs.Pdf(document, issuer, texts, language, credited, propertyName, settlement);
    }

    // ---------------------------------------------------------------- draft

    /// <summary>
    /// The draft made from the booking: the checks of the issuer and of its profile, the document of the plan it is (<paramref name="planned"/>, else the one of
    /// its part: the stay or the services of the agency, E17/F01/US01/T02) and its issuer, the tax rule of that issuer in force at the check-out of the stay, the
    /// invoice data the kind of document needs (the name of the guest of the booking when the document needs only a name and the booking has no invoice data,
    /// FIX/DOC-RECIPIENT), then the lines the document bills (one VAT per line type) and the amounts. A draft whose part the plan no longer
    /// has is discarded (no number was given) and returned as it is.
    /// </summary>
    private async Task<FiscalDocument> RebuildAsync(FiscalDocument draft, CancellationToken ct, bool isNew = false, PlannedDocument? planned = null)
    {
        var booking = await BookingOfAsync(draft.BookingId, ct);
        await RequireCasazenIssuesAsync(booking, ct);
        var (organization, _) = await RequireIssuerAsync(booking.OrgId, ct);
        planned ??= (await RequirePlanAsync(booking, ct)).Of(draft.ForAgencyServices);
        if (planned is null)
        {
            // The replacement of an invoice the SDI rejected holds its number (E17/F01/US02/T03): kept as it is, the host is told the plan of the booking changed and decides.
            if (HoldsAReplacedNumber(draft))
            {
                logger.LogWarning("Draft {DocumentId} of booking {BookingId} holds the number of a rejected invoice: kept, the host is told the plan changed", draft.Id, booking.Id);
                await TellTheHostAsync(booking, draft.Id, FiscalDocumentTodos.BookingChangedReason, ct);
                return draft;
            }

            draft.Status = FiscalDocumentStatus.Discarded;
            draft.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Draft {DocumentId} of booking {BookingId} discarded: the plan of the booking has no such document any more", draft.Id, booking.Id);
            return draft;
        }

        var profile = await IssuerProfileAsync(booking, organization, planned.IssuerProfileId, requireComplete: true, ct);
        var rule = RuleOf(profile, booking);
        if (isNew)
            await RequireNoOtherDocumentAsync(booking.Id, draft.Id, PartOf(planned.ForAgencyServices, profile.IsOwnerProfile), ct, countPlannedCredit: true);

        var kind = rule.DocumentKind == RuleDocumentKind.Receipt ? DocumentKind.Receipt : DocumentKind.Invoice;

        // A document that needs only a name, for a booking with no invoice data of its own (a portal sends none): the guest of the booking is the recipient, by name only
        // (FIX/DOC-RECIPIENT). No name to use (the guest was erased or has none): the refusal below is the one of a booking with no data, as before.
        var (recipient, guestName, missing) = await RecipientOfAsync(booking.Id, NeedsOnlyTheName(rule, kind), ct);
        if (missing.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentErrors.InvoiceDataMissing, "FiscalDocumentInvoiceDataMissing")
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = missing },
            };
        }

        // The lines of the stay whatever its source (E12/F01/US01/T01): the amounts of the portal, the tourist tax only when the host collects it (the agency's choice of
        // the portals that collect it), who collected the money; then only the lines this document bills (E17/F01/US01/T02).
        var built = await _lines.BuildAsync(booking.Id, rule, _lines.PlatformCollectedChannelsOf(organization), ct);
        var amounts = FiscalDocumentTotals.FromGross(GuestDocumentLineBuilder.LinesOf(built.Lines, planned), rule.StampDuty);

        draft.OrgId = booking.OrgId;
        draft.PropertyId = booking.PropertyId;
        draft.Purpose = planned.Purpose;
        draft.IssuerProfileId = profile.Id;
        draft.ForAgencyServices = planned.ForAgencyServices;
        draft.ServicePeriodFrom = DateOnly.FromDateTime(booking.CheckIn);
        draft.ServicePeriodTo = DateOnly.FromDateTime(booking.CheckOut);
        draft.RuleVersion = rule.Version;
        draft.Kind = kind;
        draft.Currency = StayCurrency.Normalize(booking.Currency);
        if (guestName is not null)
            CopyRecipientName(guestName, draft);
        else
            CopyRecipient(recipient!, draft);
        foreach (var line in draft.Lines.ToList())
            db.FiscalDocumentLines.Remove(line);
        draft.Lines.Clear();
        ApplyAmounts(draft, amounts);

        // The money of a booking of a portal is the portal's: the document is paid through it. Any other document is paid or to collect by what CasaZen collected.
        draft.PaymentChannel = built.PaymentChannel;
        draft.PaymentState = built.PaymentChannel == FiscalPaymentChannel.Portal
            ? FiscalDocumentPaymentState.Paid
            : await PaymentStateAsync(booking.Id, draft.Total, ct);
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

    /// <summary>The lines and the totals of <paramref name="amounts"/> written on the document (its lines added to its collection).</summary>
    internal static void ApplyAmounts(FiscalDocument document, FiscalDocumentAmounts amounts)
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

    /// <summary>The tax rule of an owner invoice: the rule set in force on the last day of its period (E14/F01/US01/T01).</summary>
    private FiscalTaxRule OwnerInvoiceRuleOf(OrgFiscalIssuerProfile profile, FiscalDocument document) =>
        taxRules.Resolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, PeriodEndOf(document));

    /// <summary>The last day of the period of an owner invoice.</summary>
    internal static DateOnly PeriodEndOf(FiscalDocument document) =>
        new DateOnly(document.PeriodYear!.Value, document.PeriodMonth!.Value, 1).AddMonths(1).AddDays(-1);

    // ---------------------------------------------------------------- issue

    /// <summary>
    /// The issue in one READ COMMITTED transaction: the number, the checks again under the lock of the series, the snapshot of the issuer (with the agency and the
    /// co-owners of an owner's document, <paramref name="parties"/>), the XML, the issuer row of the booking, the event and <paramref name="beforeSave"/> (the VAT
    /// of an owner invoice in the owner ledger), saved and committed together.
    /// </summary>
    private async Task IssueInTransactionAsync(
        FiscalDocument document,
        Guid orgId,
        Guid? bookingId,
        FiscalTaxRule rule,
        OrgFiscalIssuerProfile profile,
        Org org,
        string series,
        DateOnly today,
        FiscalDocument? credited,
        CancellationToken ct,
        Func<CancellationToken, Task>? beforeSave = null,
        IssuerParties? parties = null)
    {
        // READ COMMITTED: the advisory lock of the numbering joins it; a failure rolls the number back with the document (no gap).
        await using var transaction = PostgresAdvisoryLocks.IsSupported(db)
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;

        // The number of the issuer the document is issued on (E17/F01/US01/T01): its counter, its series. A document made before the issuers existed is the
        // organization's.
        document.IssuerProfileId ??= profile.Id;

        // The replacement of a document the SDI rejected (E17/F01/US02/T03) holds the number the correction took from it, and keeps the day of the document it replaces: that number is
        // used and no other is reserved, so the sequence has neither a duplicate nor a hole. Anything else reserves the next number of the series.
        var issueDay = today;
        FiscalNumber number;
        if (document.ReplacesDocumentId is { } replacedId)
        {
            issueDay = await RequireReplacedAsync(document, replacedId, ct) ?? today;
            number = new FiscalNumber(document.Series, document.SequenceNumber!.Value, document.Number!);
        }
        else
        {
            number = await numbering.ReserveNextAsync(orgId, document.IssuerProfileId.Value, series, ct);
        }

        // What the document may be is decided again here, under the lock of the series (a credit note takes the series of its original, so the same lock):
        // the checks before the transaction are quick refusals, but two parallel requests both pass them, and neither the original nor the booking changes to
        // tell them apart. A refusal rolls the transaction back with the number (no gap). An owner invoice has no booking: its draft already holds its owner
        // and period (the unique index of the live owner invoices), and the concurrency token of the row refuses a second issue of the same draft.
        if (credited is null)
        {
            if (bookingId is { } booking)
                await RequireNoOtherDocumentAsync(booking, document.Id, PartOf(document.ForAgencyServices, profile.IsOwnerProfile), ct);
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
        document.IssueDate = issueDay;

        // The issuer as it is now, kept with the document: its PDF and its XML read it from here from now on. The version of the rule is the one the draft
        // was made with (a document made before the versions existed takes the one in force).
        // A Swiss owner invoice keeps the account its QR-bill is paid on (E14/F01/US01/T02): the payment part prints it, as it was at the issue.
        var issuer = FiscalIssuerSnapshot.From(
            profile, org, rule.FatturaPaRegimeCode, withQrBillAccount: document.Purpose == FiscalDocumentPurpose.OwnerInvoice && credited is null && rule.IsSwiss);
        if (parties is not null)
            issuer = issuer with { Intermediary = parties.Intermediary, CoOwners = parties.CoOwners };
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

        if (credited is null && bookingId is { } lockedBooking)
            await issuers.LockIssuerAsync(lockedBooking, BookingDocumentIssuerKind.CasaZen, document.Id, save: false, ct);

        document.Status = FiscalDocumentStatus.Issued;
        document.SdiStatusUpdatedAt = UtcNow;
        document.UpdatedAt = UtcNow;
        if (beforeSave is not null)
            await beforeSave(ct);
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
    /// The document a replacement draft replaces (E17/F01/US02/T03) is a document of the same organization and issuer that the SDI rejected and that gave its number up (discarded, no sequence
    /// number, the same series and display number as the draft holds), and no other replacement of it was issued: the number goes to the replacement of that document and to nobody else.
    /// Answers the day of the document it replaces (the replacement keeps it); 422 <c>document_replacement_invalid</c> otherwise.
    /// </summary>
    private async Task<DateOnly?> RequireReplacedAsync(FiscalDocument replacement, Guid replacedId, CancellationToken ct)
    {
        var replaced = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.Id == replacedId && d.OrgId == replacement.OrgId)
            .Select(d => new { d.Status, d.SdiStatus, d.IssuerProfileId, d.Series, d.Number, d.SequenceNumber, d.IssueDate, d.Kind })
            .FirstOrDefaultAsync(ct);
        var holdsItsNumber = replacement.SequenceNumber is > 0 && !string.IsNullOrEmpty(replacement.Number);
        var valid = replaced is not null
                    && holdsItsNumber
                    && replaced.Status == FiscalDocumentStatus.Discarded
                    && replaced.SdiStatus == FiscalDocumentSdiStatuses.Rejected
                    && replaced.SequenceNumber is null
                    && replaced.IssuerProfileId == replacement.IssuerProfileId
                    && replaced.Kind == replacement.Kind
                    && string.Equals(replaced.Series, replacement.Series, StringComparison.Ordinal)
                    && string.Equals(replaced.Number, replacement.Number, StringComparison.Ordinal)
                    && !await db.FiscalDocuments.AnyAsync(
                        d => d.ReplacesDocumentId == replacedId && d.Id != replacement.Id && d.Status == FiscalDocumentStatus.Issued, ct);
        if (!valid)
            throw new DomainRuleException(FiscalDocumentSdiErrors.ReplacementInvalid, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.ReplacementInvalid));

        return replaced!.IssueDate;
    }

    /// <summary>
    /// The input of the XML of an issued document: the issuer from its snapshot, the recipient from the document, the stored lines and the VAT summary of
    /// their stored VAT per rate (<see cref="FiscalDocumentTotals.SummaryOf"/>), never <c>taxable × rate</c> again. The document of an owner company issued
    /// through the agency names the agency as the third-party issuer (<c>TZ</c>, E17/F01/US01/T03), from the snapshot as at the issue; the transmitter is
    /// <see cref="TransmitterOf"/>.
    /// </summary>
    internal static FatturaPaInput FatturaPaInputOf(FiscalDocument document, FiscalIssuerSnapshot issuerSnapshot, FiscalTaxRule rule, FiscalDocument? credited)
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
                string.Equals(recipientCountry, "IT", StringComparison.OrdinalIgnoreCase) ? document.RecipientPostalCode ?? string.Empty : FatturaPaParties.ForeignPostalCode,
                document.RecipientCity ?? string.Empty,
                string.Equals(recipientCountry, "IT", StringComparison.OrdinalIgnoreCase) ? document.RecipientProvince : null,
                recipientCountry));

        var thirdParty = issuerSnapshot.Intermediary is { } agency ? AgencyParty(agency) : null;
        return new FatturaPaInput(
            TransmitterOf(issuerSnapshot),
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
            credited is null ? null : new FatturaPaRelatedDocument(credited.Number ?? string.Empty, credited.IssueDate))
        {
            IssuedBy = thirdParty is null ? FatturaPaIssuedBy.Issuer : FatturaPaIssuedBy.ThirdParty,
            ThirdPartyIssuer = thirdParty,
        };
    }

    /// <summary>
    /// Who transmits the file to the SDI (<c>IdTrasmittente</c>, E17/F01/US01/T03): the subject whose channel sends it, by its fiscal code (else its VAT number).
    /// It is the agency, for a document it issues in its own name as for one it issues on behalf of an owner (the agency, not the owner, sends the file). The XML is immutable
    /// once issued: the SDI channel of E17/F01/US02 sends the file as it was issued, so whether a provider needs its own code here is checked on its sandbox
    /// (<c>docs/runbooks/fiscal-documents.md</c>, *Verifying the channel*) and, if it does, changes here for the files issued afterwards.
    /// </summary>
    internal static FatturaPaId TransmitterOf(FiscalIssuerSnapshot issuerSnapshot)
    {
        ArgumentNullException.ThrowIfNull(issuerSnapshot);

        if (issuerSnapshot.Intermediary is { } agency)
            return new FatturaPaId(CountryOf(agency.CountryCode), agency.FiscalCode ?? DigitsOf(agency.VatNumber) ?? string.Empty);

        return new FatturaPaId(CountryOf(issuerSnapshot.CountryCode), issuerSnapshot.FiscalCode ?? DigitsOf(issuerSnapshot.VatNumber) ?? string.Empty);
    }

    /// <summary>
    /// The agency as the third-party issuer of a document of its owner (<c>TerzoIntermediarioOSoggettoEmittente</c>): its Italian VAT number and fiscal code and its
    /// name. The block has no address: the one of the party is not written.
    /// </summary>
    private static FatturaPaParty AgencyParty(FiscalIssuerIntermediary agency)
    {
        var country = CountryOf(agency.CountryCode);
        var vatNumber = DigitsOf(agency.VatNumber);
        return new FatturaPaParty(
            vatNumber is null ? null : new FatturaPaId(country, vatNumber),
            agency.FiscalCode,
            agency.Name,
            null,
            null,
            new FatturaPaAddress(string.Empty, null, string.Empty, string.Empty, null, country));
    }

    private static string CountryOf(string? countryCode) =>
        string.IsNullOrWhiteSpace(countryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : countryCode.Trim().ToUpperInvariant();

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
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
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
    /// What a document of a stay bills (E17/F01/US01/T02), the ground of «never two documents for one stay»: the document of an owner bills the stay, the document of
    /// the services of the agency bills its services, and the one document of the organization (or a document issued elsewhere, or without an issuer) bills both.
    /// </summary>
    [Flags]
    private enum BilledPart
    {
        Stay = 1,
        Services = 2,
        Everything = Stay | Services,
    }

    /// <summary>The part a document of a stay bills: the services of the agency; else the stay when an owner issues it; else everything.</summary>
    private static BilledPart PartOf(bool forAgencyServices, bool ownerIssues) =>
        forAgencyServices ? BilledPart.Services : ownerIssues ? BilledPart.Stay : BilledPart.Everything;

    /// <summary>
    /// Never two documents that bill the same part of one booking (E17/F01/US01/T02), unless the first was credited in full: 409 <c>document_already_issued</c>. The
    /// owner's document of the stay and the agency's document of its services live side by side; a document that billed the whole stay (the organization's: every
    /// one of before the owners issued, or one made while the property had no owner in force) holds both parts, and a document of the stay holds every new document
    /// of the stay, whoever issues it, so the guest is never billed twice. A document imported from Octorate (issued elsewhere) and a document without an issuer hold
    /// the whole booking; a proforma is not a document.
    /// </summary>
    private async Task RequireNoOtherDocumentAsync(Guid bookingId, Guid exceptId, BilledPart part, CancellationToken ct, bool countPlannedCredit = false)
    {
        if (await HasBlockingDocumentAsync(bookingId, exceptId, part, countPlannedCredit, ct))
            throw new DomainConflictException(FiscalDocumentErrors.AlreadyIssued, "FiscalDocumentAlreadyIssued");
    }

    /// <summary>The booking has an issued document that bills some of <paramref name="part"/> and is not credited in full (see <see cref="RequireNoOtherDocumentAsync"/>).</summary>
    private async Task<bool> HasBlockingDocumentAsync(Guid bookingId, Guid exceptId, BilledPart part, bool countPlannedCredit, CancellationToken ct)
    {
        var issued = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId && d.Id != exceptId && d.Status == FiscalDocumentStatus.Issued
                        && d.Purpose == FiscalDocumentPurpose.GuestStay
                        && d.Kind != DocumentKind.CreditNote && d.Kind != DocumentKind.Proforma)
            .Select(d => new { d.Id, d.Total, d.Source, d.IssuerProfileId, d.ForAgencyServices })
            .ToListAsync(ct);
        if (issued.Count == 0)
            return false;

        // The documents an owner issued (on a profile with an owner) bill the stay only.
        var profileIds = issued.Where(d => d.IssuerProfileId is not null).Select(d => d.IssuerProfileId!.Value).Distinct().ToList();
        var ownerProfiles = profileIds.Count == 0
            ? []
            : (await db.OrgFiscalIssuerProfiles
                .AsNoTracking()
                .Where(p => profileIds.Contains(p.Id) && p.PropertyOwnerId != null)
                .Select(p => p.Id)
                .ToListAsync(ct)).ToHashSet();

        foreach (var document in issued)
        {
            var billed = document.Source != FiscalDocumentSource.CasaZen || document.IssuerProfileId is not { } issuer
                ? BilledPart.Everything
                : PartOf(document.ForAgencyServices, ownerProfiles.Contains(issuer));
            if ((billed & part) == 0)
                continue;

            // A draft is allowed beside a document whose credit note is only a draft (the correction of a date change makes the new draft with the credit note), but never issued: the
            // issue asks again with only the credit notes issued (E12/F01/US01/T03).
            var credited = await CreditedSoFarAsync(document.Id, ct) + (countPlannedCredit ? await PlannedCreditAsync(document.Id, ct) : 0m);
            if (credited < document.Total)
                return true;
        }

        return false;
    }

    private Task<decimal> CreditedSoFarAsync(Guid documentId, CancellationToken ct) =>
        db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.CreditedDocumentId == documentId && d.Status == FiscalDocumentStatus.Issued && d.Kind == DocumentKind.CreditNote)
            .SumAsync(d => d.Total, ct);

    /// <summary>What the credit notes still drafts of a document credit (E12/F01/US01/T03): the corrections waiting for the host to check and issue them.</summary>
    private Task<decimal> PlannedCreditAsync(Guid documentId, CancellationToken ct) =>
        db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.CreditedDocumentId == documentId && d.Status == FiscalDocumentStatus.Draft && d.Kind == DocumentKind.CreditNote)
            .SumAsync(d => d.Total, ct);

    /// <summary>
    /// A guest document the SDI rejected never was issued, so it is not credited: its correction replaces it, with the number it gave up (E17/F01/US02/T03); a credit note of it would bill
    /// an operation twice and take its number away from the replacement. 422 <c>document_rejected_use_correction</c>. An owner invoice is credited as it always was.
    /// </summary>
    private static void RequireNotRejected(FiscalDocument original)
    {
        if (original.SdiStatus == FiscalDocumentSdiStatuses.Rejected && original.Purpose != FiscalDocumentPurpose.OwnerInvoice)
            throw new DomainRuleException(FiscalDocumentSdiErrors.UseCorrection, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.UseCorrection));
    }

    /// <summary>
    /// The replacement of an invoice the SDI rejected (E17/F01/US02/T03) holds the number that invoice gave up: discarded, the number would be lost (a hole in the numbering) and the rejected
    /// invoice could not be corrected again. It is issued, never discarded: 422 <c>document_replacement_holds_number</c>.
    /// </summary>
    private static void RequireNotAHeldNumber(FiscalDocument draft)
    {
        if (HoldsAReplacedNumber(draft))
            throw new DomainRuleException(FiscalDocumentSdiErrors.ReplacementHoldsNumber, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.ReplacementHoldsNumber));
    }

    /// <summary>The draft replaces a rejected invoice and holds its number: neither the host nor an automatic run discards it (the run tells the host instead).</summary>
    internal static bool HoldsAReplacedNumber(FiscalDocument draft) => draft.ReplacesDocumentId is not null && draft.SequenceNumber is not null;

    private static void RequireCasazenDraft(FiscalDocument document)
    {
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw ReadOnly();
        if (document.Status != FiscalDocumentStatus.Draft)
            throw new DomainRuleException(FiscalDocumentErrors.NotDraft, "FiscalDocumentNotDraft");
    }

    /// <summary>
    /// An owner invoice is made again, issued, credited and discarded from the owner invoices, behind their permission (<c>agency.statement.manage</c>) and their
    /// flag (E14/F01/US01/T01): never by the endpoints of the documents of the guests, which hold only <c>payment.write</c> and would skip both and the owner
    /// ledger the VAT is posted to and given back from.
    /// </summary>
    private static void RequireGuestDocument(FiscalDocument document)
    {
        if (document.Purpose == FiscalDocumentPurpose.OwnerInvoice)
            throw new DomainRuleException(OwnerInvoiceErrors.UseOwnerInvoices, "FiscalDocumentOwnerInvoice");
    }

    /// <summary>The owner path takes only an owner invoice: any other document is not found there.</summary>
    private static void RequireOwnerInvoice(FiscalDocument document)
    {
        if (document.Purpose != FiscalDocumentPurpose.OwnerInvoice)
            throw NotFound(document.Id);
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
    /// The invoice data of a booking names no recipient and the guest did not ask for an invoice (FIX/DOC-RECIPIENT): there is no row at all (the portals and the site of the agency
    /// send none), or the row holds no name and no «yes» to «Ti serve la fattura?». A row with a name wins, as it always did; a guest who asked for an invoice and gave no data is
    /// waited for (the host asks for it), never made out as somebody else.
    /// </summary>
    internal static bool NeedsTheNameOfTheGuest(InvoiceDataView? data) => data is null || (!data.InvoiceRequested && string.IsNullOrWhiteSpace(data.Name));

    /// <summary>
    /// The name of the guest of the booking, the recipient of a document that needs only a name (FIX/DOC-RECIPIENT). Only the name is read: no address, e-mail, phone, fiscal code or
    /// nationality of the guest is read or copied. <c>null</c> when the guest has no name to use (see <see cref="RecipientNameOfGuest"/>).
    /// </summary>
    private async Task<string?> GuestNameOfRecipientAsync(Guid bookingId, CancellationToken ct)
    {
        var guest = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new GuestNameFacts(b.Guest.FirstName, b.Guest.LastName, b.Guest.DataAnonymizedDate != null || b.Guest.IsDeleted || b.Guest.ErasureRequested))
            .FirstOrDefaultAsync(ct);
        return guest is null ? null : RecipientNameOfGuest(guest.FirstName, guest.LastName, guest.Erased);
    }

    /// <summary>
    /// The name a document is made out to for a guest: the first and the last name, trimmed and joined, at most <see cref="FiscalDocument.RecipientNameMaxLength"/> characters.
    /// <c>null</c> for a guest who was erased, anonymized, deleted or who asked to be erased (<paramref name="erased"/>: the same test the CRM and the e-mails make), for one with no
    /// name, and for one whose name is the placeholder of the erasure (<see cref="GuestDataEraser.AnonymizedName"/>): a document is never made out to a name that is not the guest's.
    /// </summary>
    internal static string? RecipientNameOfGuest(string? firstName, string? lastName, bool erased)
    {
        if (erased)
            return null;

        var parts = new[] { firstName, lastName }.Select(part => part?.Trim() ?? string.Empty).Where(part => part.Length > 0).ToList();
        if (parts.Count == 0 || parts.Any(part => string.Equals(part, GuestDataEraser.AnonymizedName, StringComparison.OrdinalIgnoreCase)))
            return null;

        return Truncate(string.Join(' ', parts), FiscalDocument.RecipientNameMaxLength);
    }

    /// <summary>What <see cref="GuestNameOfRecipientAsync"/> reads of the guest of a booking: the two parts of the name, and whether the guest is gone.</summary>
    private sealed record GuestNameFacts(string? FirstName, string? LastName, bool Erased);

    /// <summary>
    /// Who a document of the booking is made out to now (FIX/DOC-RECIPIENT): the invoice data of the booking, else for a document that needs only a name the name of its guest, else what is
    /// missing (<see cref="MissingInvoiceData"/>). The one reading of the draft (<see cref="RebuildAsync"/>) and of its issue (<see cref="RequireDraftOfTheRecipientAsync"/>).
    /// </summary>
    private async Task<(InvoiceDataView? Data, string? GuestName, IReadOnlyList<string> Missing)> RecipientOfAsync(Guid bookingId, bool nameOnly, CancellationToken ct)
    {
        var data = await invoiceData.GetAsync(bookingId, ct);
        var guestName = nameOnly && NeedsTheNameOfTheGuest(data) ? await GuestNameOfRecipientAsync(bookingId, ct) : null;
        IReadOnlyList<string> missing = guestName is null ? MissingInvoiceData(data, nameOnly) : [];
        return (data, guestName, missing);
    }

    /// <summary>
    /// The draft of a document that needs only a name is still made out to who the booking says now (FIX/DOC-RECIPIENT): the draft made with the name of the guest is no longer the document once the
    /// booking has invoice data with a name (it wins), once the guest was erased or asked to be erased (a document is never made out to a name that is no longer theirs) or when the name changed;
    /// a draft made from invoice data is no longer it when the data changed or is gone. 409 <c>document_draft_outdated</c> and nothing is numbered: the host makes the draft again, which is then
    /// the 422 of the draft when there is nobody to make it out to. The draft of an Italian invoice is not read again: it never falls back and is issued as it always was.
    /// </summary>
    private async Task RequireDraftOfTheRecipientAsync(FiscalDocument draft, Guid bookingId, FiscalTaxRule rule, CancellationToken ct)
    {
        var kind = rule.DocumentKind == RuleDocumentKind.Receipt ? DocumentKind.Receipt : DocumentKind.Invoice;
        if (!NeedsOnlyTheName(rule, kind))
            return;

        var (data, guestName, missing) = await RecipientOfAsync(bookingId, nameOnly: true, ct);
        if (missing.Count == 0)
        {
            var current = new FiscalDocument();
            if (guestName is not null)
                CopyRecipientName(guestName, current);
            else
                CopyRecipient(data!, current);

            if (SameRecipient(draft, current))
                return;
        }

        throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");
    }

    /// <summary>Two documents have the same recipient: the same kind and the same text in every field of the snapshot, a text of white space being none.</summary>
    private static bool SameRecipient(FiscalDocument first, FiscalDocument second) =>
        first.RecipientKind == second.RecipientKind && RecipientTextsOf(first).Zip(RecipientTextsOf(second)).All(pair => SameText(pair.First, pair.Second));

    /// <summary>The texts of the recipient snapshot of a document, in a fixed order.</summary>
    private static string?[] RecipientTextsOf(FiscalDocument document) =>
    [
        document.RecipientName,
        document.RecipientFiscalCode,
        document.RecipientVatNumber,
        document.RecipientAddressStreet,
        document.RecipientAddressCivic,
        document.RecipientPostalCode,
        document.RecipientCity,
        document.RecipientProvince,
        document.RecipientCountryCode,
        document.RecipientSdiCode,
        document.RecipientPec,
    ];

    private static bool SameText(string? first, string? second) =>
        string.Equals(string.IsNullOrWhiteSpace(first) ? null : first.Trim(), string.IsNullOrWhiteSpace(second) ? null : second.Trim(), StringComparison.Ordinal);

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

    /// <summary>
    /// The draft is the document <paramref name="planned"/> of the plan of its booking (E17/F01/US01/T02): the same issuer (a draft without one is the
    /// organization's) and no line of a type that document does not bill; 409 <c>document_draft_outdated</c> otherwise.
    /// </summary>
    private static void RequireDraftOfThePlan(FiscalDocument draft, PlannedDocument? planned, OrgFiscalIssuerProfile organization)
    {
        if (planned is not null
            && planned.IssuerProfileId == (draft.IssuerProfileId ?? organization.Id)
            && draft.Lines.All(line => planned.Bills(line.LineType)))
        {
            return;
        }

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

    /// <summary>
    /// The money collected for each booking: its completed and partially refunded payments, net of the refunds. A stay of a portal the host collects from has no payment of CasaZen (the
    /// host records none on it and no link is made for it): what the guest paid is what the channel manager reports (<c>OtaPaidAmount</c>, the figure the payment ledger shows too),
    /// and nothing is collected while it says nothing. A stay the portal collected is the portal's money, never counted here.
    /// </summary>
    /// <param name="db">The context.</param>
    /// <param name="bookingIds">The bookings.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="ofOrg">The org of the bookings, for a caller that has no org of its own (the owner portal: its tenant filter matches nothing); <c>null</c> for a caller of the org, whose tenant filter applies.</param>
    internal static async Task<Dictionary<Guid, decimal>> CollectedByBookingAsync(AppDbContext db, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct, Guid? ofOrg = null)
    {
        if (bookingIds.Count == 0)
            return [];

        // A caller with no org of its own (the owner portal) has a tenant filter that matches nothing: it names the org of the bookings instead (ofOrg).
        var payments = ofOrg is { } payOrg
            ? db.Payments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).Where(p => p.OrgId == payOrg)
            : db.Payments.AsQueryable();
        var collected = await payments
            .AsNoTracking()
            .Where(p => bookingIds.Contains(p.BookingId) && (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded))
            .GroupBy(p => p.BookingId)
            .Select(g => new { BookingId = g.Key, Collected = g.Sum(p => p.Amount - p.RefundedAmount) })
            .ToDictionaryAsync(x => x.BookingId, x => x.Collected, ct);

        var stays = ofOrg is { } stayOrg
            ? db.Bookings.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter, AppDbContext.TenantQueryFilter]).Where(b => b.OrgId == stayOrg)
            : db.Bookings.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]);
        var paidToTheHost = await stays
            .AsNoTracking()
            .Where(b => bookingIds.Contains(b.Id) && b.OtaPaidAmount != null && b.OtaCompanyCollect != true)
            .Select(b => new { b.Id, Paid = b.OtaPaidAmount ?? 0m })
            .ToListAsync(ct);
        foreach (var stay in paidToTheHost)
            collected[stay.Id] = collected.GetValueOrDefault(stay.Id) + stay.Paid;

        return collected;
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
        var own = documents.Where(d => IsPaymentStateReadFromPayments(d.Source, d.Kind, d.BookingId, d.PaymentChannel)).ToList();
        if (own.Count == 0)
            return;

        var collected = await CollectedByBookingAsync(db, own.Select(d => d.BookingId!.Value).Distinct().ToList(), ct);
        foreach (var document in own)
            document.PaymentState = PaymentStateOf(collected.GetValueOrDefault(document.BookingId!.Value), document.Total);
    }

    /// <summary>
    /// Whether the payment state of a document is read from the payments of its booking: the documents CasaZen issued for a booking, except a credit note
    /// (which credits money and is not collected) and the document of a booking whose portal collected the money (paid through the portal: CasaZen sees none of
    /// its payments). Any other document keeps the state it was given. The one rule of the list, the detail and the export.
    /// </summary>
    internal static bool IsPaymentStateReadFromPayments(FiscalDocumentSource source, DocumentKind kind, Guid? bookingId, FiscalPaymentChannel channel) =>
        source == FiscalDocumentSource.CasaZen && kind != DocumentKind.CreditNote && bookingId is not null && channel != FiscalPaymentChannel.Portal;

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

    /// <summary>
    /// The recipient of a document made out to the guest of the booking by name (FIX/DOC-RECIPIENT): a private person with that name and nothing else. Every other field is set to
    /// nothing, so a draft made again never keeps the address or the codes of an invoice data it no longer has.
    /// </summary>
    private static void CopyRecipientName(string name, FiscalDocument document)
    {
        document.RecipientKind = InvoiceRecipientKind.Guest;
        document.RecipientName = name;
        document.RecipientFiscalCode = null;
        document.RecipientVatNumber = null;
        document.RecipientAddressStreet = null;
        document.RecipientAddressCivic = null;
        document.RecipientPostalCode = null;
        document.RecipientCity = null;
        document.RecipientProvince = null;
        document.RecipientCountryCode = null;
        document.RecipientSdiCode = null;
        document.RecipientPec = null;
    }

    /// <summary>The recipient snapshot of <paramref name="from"/> copied to <paramref name="to"/> (a credit note, the replacement of a rejected document).</summary>
    internal static void CopyRecipient(FiscalDocument from, FiscalDocument to)
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

    /// <summary>A line of another document as a line of <paramref name="document"/>: the same text, amounts and VAT, a new id.</summary>
    internal static FiscalDocumentLine CopyLine(FiscalDocument document, FiscalDocumentLine line) => new()
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
            ? new FatturaPaId(country.ToUpperInvariant(), FatturaPaParties.ForeignPrivateCode)
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
