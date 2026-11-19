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

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISelfBillingService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The base is the ledger.</b> The commissions are the <see cref="OwnerLedgerEntryType.ChannelFee"/> lines of the owner ledger of the month (the ledger decides which
/// stays are active and what the portal kept; a reversal and the line it reverses cancel out), in the currency of the stay, never read from the bookings again. The portal of a
/// stay is its booking source, the holder of its account is <see cref="IPortalAccountHolderResolver"/>'s, and for an account held by the owner the owner is the one who signs for
/// the property on the check-out day (<see cref="DocumentPlanRules.SignatoryOf"/>): the same owner whose receipt or invoice the document plan makes for the stay.</item>
/// <item><b>Read in bulk.</b> The month is read with a handful of queries whatever the number of stays (the ledger lines, their bookings, the holders, the ownerships and the
/// mandates of the properties, the issuer profiles); the holder of a stay is decided in memory, once for each owner.</item>
/// <item><b>The draft is the ledger.</b> It is made again from the ledger until it is issued (the lines it had are replaced), and the issue refuses a draft that is no longer what
/// the ledger, the holder, the portal and the settings give (409 <c>document_draft_outdated</c>). A draft nothing in the ledger supports any more is discarded by the next
/// generation (no number was given).</item>
/// <item><b>Only active stays.</b> The ledger is posted by hand (the month-end posting), so the line of a stay cancelled or marked no-show since is still alive until the next posting:
/// the bookings are read for their channel, their check-out day and whether they are active now (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> and no no-show, the one definition),
/// and a stay that is not is reported as not due (<see cref="SelfBillingNotDueReasons.StayNotActive"/>), in the drafts and at the issue.</item>
/// <item><b>The «Da fare» of the drafts and the monthly run</b> (FIX/BE-TD17-ALERTS, <c>SelfBillingService.MonthlyDrafts.cs</c>): the generation, the issue and the discard keep the
/// item of the month in step with the drafts that are left (<paramref name="activities"/>, nothing without it); only the monthly run raises it.</item>
/// <item><b>Logs</b> carry ids and counts: never a name, a VAT number or an amount.</item>
/// </list>
/// </remarks>
public sealed partial class SelfBillingService(
    AppDbContext db,
    IFiscalDocumentService documents,
    IDocumentIssuerService issuers,
    IFiscalTaxRuleResolver taxRules,
    IPortalAccountHolderResolver holders,
    IDocumentTexts texts,
    IOptions<SelfBillingOptions> options,
    TimeProvider time,
    ILogger<SelfBillingService> logger,
    IInternalActivityService? activities = null) : ISelfBillingService
{
    /// <summary>The most TD17 one call of the list answers (the newest periods first).</summary>
    public const int MaxListRows = 1000;

    /// <summary>The language of the lines of a document (the descriptions are stored with it, as for the other documents CasaZen issues).</summary>
    private const string LineLanguage = DocumentLanguages.Italian;

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<SelfBillingGeneration> CreateDraftsForPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            throw new DomainRuleException(SelfBillingErrors.PeriodInvalid, "SelfBillingPeriodInvalid");
        RequirePeriodOver(year, month);
        RequireConfigured();

        // One generation of an organization at a time: two requests (a double click, a retry) never both find «no draft yet» for a holder, a portal and a month.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.SelfBilling, orgId.ToString("N")));

        var plan = await PlanAsync(orgId, year, month, ct);
        var live = await db.FiscalDocuments
            .Include(d => d.Lines)
            .Where(d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.PeriodYear == year && d.PeriodMonth == month
                        && d.Status != FiscalDocumentStatus.Discarded && d.Kind != DocumentKind.CreditNote)
            .ToListAsync(ct);

        var last = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        var names = await PropertyNamesAsync(orgId, plan.Groups.SelectMany(g => g.PropertyNets.Keys).Distinct().ToList(), ct);
        int created = 0, updated = 0, alreadyIssued = 0, discarded = 0;
        var outcomes = new List<SelfBillingOutcome>();
        var matched = new HashSet<Guid>();
        foreach (var group in plan.Groups)
        {
            var composition = Compose(group, names, year, month, last);
            var existing = live.FirstOrDefault(d => IsDocumentOf(d, group));
            if (existing is not null)
                matched.Add(existing.Id);

            if (existing is { Status: FiscalDocumentStatus.Issued })
            {
                alreadyIssued++;
                outcomes.Add(OutcomeOf(existing, group, SelfBillingResults.AlreadyIssued, changedSinceIssue: existing.TaxableBase != composition.Amounts.TaxableBase));
                continue;
            }

            var draft = existing ?? new FiscalDocument
            {
                Source = FiscalDocumentSource.CasaZen,
                Status = FiscalDocumentStatus.Draft,
                CreatedAt = UtcNow,
                CreatedByUserId = userId,
            };
            if (existing is not null)
            {
                // The lines of a draft that exists are replaced: the old ones are removed, the new ones added through their set, never left to the collection.
                foreach (var line in draft.Lines.ToList())
                    db.FiscalDocumentLines.Remove(line);
            }

            Apply(draft, orgId, year, month, group, composition);
            matched.Add(draft.Id);
            if (existing is null)
            {
                db.FiscalDocuments.Add(draft);
                live.Add(draft);
                created++;
            }
            else
            {
                foreach (var line in draft.Lines)
                    db.FiscalDocumentLines.Add(line);
                updated++;
            }

            await db.SaveChangesAsync(ct);
            outcomes.Add(OutcomeOf(draft, group, existing is null ? SelfBillingResults.Created : SelfBillingResults.Updated, changedSinceIssue: false));
        }

        // A draft the ledger no longer supports (its commissions are gone, its holder or its portal changed) is dropped: it has no number, and the month shows what is true.
        foreach (var stale in live.Where(d => d.Status == FiscalDocumentStatus.Draft && !matched.Contains(d.Id)))
        {
            stale.Status = FiscalDocumentStatus.Discarded;
            stale.UpdatedAt = UtcNow;
            discarded++;
        }

        if (discarded > 0)
            await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        // The «Da fare» of the month follows the drafts that are left: a generation that dropped the last one closes it (it never raises it: that is the monthly run's).
        await TrySyncDraftsTodoAsync(orgId, year, month, ct);

        logger.LogInformation(
            "Self-billed invoice drafts of org {OrgId} for {Year}-{Month} by {UserId}: {Created} made, {Updated} made again, {Issued} issued already, {Discarded} discarded, {Failed} holders refused, {NotDue} commissions not due, {Ignored} portal entries ignored",
            orgId, year, month, userId, created, updated, alreadyIssued, discarded, plan.Failed.Count, plan.NotDue.Count, options.Value.IgnoredPortalEntries());

        // The drafts only read the ledger, which is posted by hand (the month-end posting of the owner accounting): a month with no line in it has nothing to invoice, and the answer says so
        // instead of an empty list that reads as «nothing is due».
        var ledgerPosted = await db.OwnerLedgerEntries.AsNoTracking().AnyAsync(e => e.OrgId == orgId && e.PeriodYear == year && e.PeriodMonth == month, ct);
        return new SelfBillingGeneration(year, month, created, updated, alreadyIssued, discarded, outcomes, plan.Failed, plan.NotDue, ledgerPosted);
    }

    public async Task<IReadOnlyList<SelfBillingRow>> ListAsync(Guid orgId, SelfBillingCriteria criteria, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = db.FiscalDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.Kind != DocumentKind.CreditNote && d.Status != FiscalDocumentStatus.Discarded);
        if (criteria.Year is { } year && criteria.Month is { } month)
            query = query.Where(d => d.PeriodYear == year && d.PeriodMonth == month);

        var found = await query
            .OrderByDescending(d => d.PeriodYear)
            .ThenByDescending(d => d.PeriodMonth)
            .ThenByDescending(d => d.CreatedAt)
            .Take(MaxListRows)
            .ToListAsync(ct);
        var info = await HolderInfoAsync(orgId, found, ct);
        return found
            .Select(d => RowOf(d, info))
            .OrderByDescending(r => r.PeriodYear)
            .ThenByDescending(r => r.PeriodMonth)
            .ThenBy(r => r.HolderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.PortalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SelfBillingDetail> GetAsync(Guid orgId, Guid documentId, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.Kind != DocumentKind.CreditNote, ct)
            ?? throw DocumentNotFound(documentId);
        return await DetailOfAsync(orgId, document, await EstimatedAmountOfAsync(orgId, document, ct), ct);
    }

    public async Task<SelfBillingDetail> IssueAsync(Guid orgId, Guid documentId, bool confirm, string? userId, CancellationToken ct = default, bool acceptEstimate = false)
    {
        if (!confirm)
            throw new DomainRuleException(FiscalDocumentErrors.ConfirmationRequired, "FiscalDocumentConfirmationRequired");

        var draft = await db.FiscalDocuments.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.Kind != DocumentKind.CreditNote, ct)
            ?? throw DocumentNotFound(documentId);
        if (draft.Status != FiscalDocumentStatus.Draft)
            throw new DomainRuleException(FiscalDocumentErrors.NotDraft, "FiscalDocumentNotDraft");
        RequireConfigured();

        // The draft is issued only while it still is what the ledger, the holder, the portal and the settings give today: otherwise the host confirmed other amounts, and the
        // drafts of the month are made again first.
        if (draft.PeriodYear is not { } year || draft.PeriodMonth is not { } month)
            throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");
        RequirePeriodOver(year, month);

        var plan = await PlanAsync(orgId, year, month, ct);
        var group = plan.Groups.FirstOrDefault(g => IsDocumentOf(draft, g))
            ?? throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");
        var names = await PropertyNamesAsync(orgId, group.PropertyNets.Keys.ToList(), ct);
        var composition = Compose(group, names, year, month, new DateOnly(year, month, 1).AddMonths(1).AddDays(-1));
        var expected = new FiscalDocument();
        Apply(expected, orgId, year, month, group, composition);
        if (!IsTheSameDraft(draft, expected))
            throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");

        // Part of the amount may be an estimate (a rule of the agency or the default of the channel, never the figure of the portal): it is issued only when the host accepted it, because an
        // issued TD17 is not corrected from here. Nothing is numbered before the answer.
        var estimated = EstimateOf(group);
        if (estimated > 0m && !acceptEstimate)
        {
            throw new DomainRuleException(SelfBillingErrors.EstimateNotConfirmed, "SelfBillingEstimateNotConfirmed")
            {
                Extensions = new Dictionary<string, object?> { ["estimatedAmount"] = estimated },
            };
        }

        var issued = await documents.IssueSelfBillingAsync(draft.Id, composition.Rule, options.Value.EffectiveSeriesPattern, userId, ct);

        // The last draft of the month issued: its «Da fare» to check and issue is done.
        await TrySyncDraftsTodoAsync(orgId, year, month, ct);
        return await DetailOfAsync(orgId, issued, 0m, ct);
    }

    public async Task DiscardDraftAsync(Guid orgId, Guid documentId, CancellationToken ct = default)
    {
        var found = await db.FiscalDocuments.AsNoTracking()
            .Where(d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.Kind != DocumentKind.CreditNote)
            .Select(d => new { d.PeriodYear, d.PeriodMonth })
            .FirstOrDefaultAsync(ct)
            ?? throw DocumentNotFound(documentId);

        await documents.DiscardSelfBillingDraftAsync(documentId, ct);

        // The last draft of the month discarded: its «Da fare» to check and issue is done.
        if (found.PeriodYear is { } year && found.PeriodMonth is { } month)
            await TrySyncDraftsTodoAsync(orgId, year, month, ct);
    }

    // ---------------------------------------------------------------- the plan: who needs a TD17 for which commissions

    /// <summary>One group of commissions that makes one TD17: a holder, a portal entity, the net of each property and what is known of the amounts.</summary>
    private sealed class Group(HolderOfAccount holder, SelfBillingPortal portal)
    {
        public HolderOfAccount Holder { get; } = holder;

        public SelfBillingPortal Portal { get; } = portal;

        public Dictionary<Guid, decimal> PropertyNets { get; } = [];

        public HashSet<Guid> Stays { get; } = [];

        public decimal Estimated { get; set; }

        public decimal Taxable => PropertyNets.Values.Where(net => net > 0m).Sum();
    }

    /// <summary>The issuer a TD17 is made for: the agency (<see cref="OwnerId"/> <c>null</c>) or an owner, with the issuer profile it is issued on.</summary>
    private sealed record HolderOfAccount(SelfBillingHolderKind Kind, Guid? OwnerId, OrgFiscalIssuerProfile Profile);

    /// <summary>What a holder is for the TD17: ready, not subject to it (the reason), or subject and not able yet (the code and the fields it misses).</summary>
    private sealed record HolderCheck(HolderOfAccount? Holder, string? NotDue, string? FailureCode, IReadOnlyList<string> Fields, SelfBillingHolderKind Kind, Guid? OwnerId)
    {
        public static HolderCheck Ready(HolderOfAccount holder) => new(holder, null, null, [], holder.Kind, holder.OwnerId);

        public static HolderCheck NotSubject(string reason, SelfBillingHolderKind kind, Guid? ownerId) => new(null, reason, null, [], kind, ownerId);

        public static HolderCheck Cannot(string code, IReadOnlyList<string> fields, SelfBillingHolderKind kind, Guid? ownerId) => new(null, null, code, fields, kind, ownerId);
    }

    private sealed record Plan(List<Group> Groups, List<SelfBillingFailure> Failed, List<SelfBillingNotDue> NotDue);

    private sealed record FeeLine(Guid PropertyId, Guid StayId, Guid OwnerId, string Currency, decimal Amount, string? RuleVersion);

    private sealed record StayFacts(Guid Id, BookingSource Source, DateTime CheckOut, BookingStatus Status, DateTime? NoShowAt);

    private sealed record OwnerFacts(Guid Id, OwnerEntityKind? EntityKind, string? VatNumber);

    private sealed record OwnershipFacts(Guid Id, Guid PropertyId, Guid OwnerId, decimal SharePercent, DateTime Start, DateTime? End);

    private async Task<Plan> PlanAsync(Guid orgId, int year, int month, CancellationToken ct)
    {
        var plan = new Plan([], [], []);

        // The commissions of the month, as the ledger has them: the owner's share of the commission of each stay, a reversal and the line it reverses cancelling out. In the
        // currency of the stay (the share of the owner in it), so that nothing is converted.
        var fees = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PeriodYear == year && e.PeriodMonth == month
                        && e.PropertyId != null && e.SourceId != null)
            .Select(e => new FeeLine(e.PropertyId!.Value, e.SourceId!.Value, e.PropertyOwnerId, e.OriginalCurrency, e.OriginalAmount, e.RuleVersion))
            .ToListAsync(ct);
        if (fees.Count == 0)
            return plan;

        var stayIds = fees.Select(f => f.StayId).Distinct().ToList();
        var stays = await db.Bookings.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == orgId && stayIds.Contains(b.Id))
            .Select(b => new StayFacts(b.Id, b.Source, b.CheckOutDate, b.Status, b.NoShowAt))
            .ToDictionaryAsync(b => b.Id, ct);

        var holderMap = await holders.ForOrgAsync(orgId, ct);
        var portals = options.Value.ConfiguredPortals();
        var last = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        var owners = await OwnerContextAsync(orgId, fees.Select(f => f.PropertyId).Distinct().ToList(), ct);
        var ownerChecks = new Dictionary<Guid, HolderCheck>();
        HolderCheck? agencyCheck = null;

        var groups = new Dictionary<(Guid ProfileId, string PortalKey), Group>();
        var failures = new Dictionary<(Guid? OwnerId, string PortalKey), SelfBillingFailure>();
        var notDue = new Dictionary<(string Reason, string Channel, string Currency), (int Stays, decimal Amount)>();

        foreach (var stayFees in fees.GroupBy(f => f.StayId))
        {
            var currency = StayCurrency.Normalize(stayFees.First().Currency);
            var amount = -stayFees.Sum(f => f.Amount);
            if (!stays.TryGetValue(stayFees.Key, out var stay))
            {
                AddNotDue(SelfBillingNotDueReasons.StayNotFound, string.Empty);
                continue;
            }

            // The ledger is posted by hand: the line of a stay that was cancelled or marked no-show after the posting is alive until the next one. Only an active stay (the one definition
            // of the code, as the owner ledger plans it) is integrated, so the draft and the issue never invoice a commission nobody owes.
            if (!AlloggiatiStatusRules.ActiveBookingStatuses.Contains(stay.Status) || stay.NoShowAt is not null)
            {
                AddNotDue(SelfBillingNotDueReasons.StayNotActive, stay.Source.ToString());
                continue;
            }

            var channel = stay.Source;
            if (!FiscalCopy.IsOtaBookingSource(channel))
            {
                AddNotDue(SelfBillingNotDueReasons.NotAPortal, channel.ToString());
                continue;
            }

            if (currency != StayCurrency.Euro)
            {
                AddNotDue(SelfBillingNotDueReasons.CurrencyNotSupported, channel.ToString());
                continue;
            }

            // The holder of the account of this listing on this portal (⚖ I10): the agency, or the owner who signs for the property on the check-out day.
            var propertyId = stayFees.First().PropertyId;
            HolderCheck check;
            if (holderMap.Resolve(propertyId, channel) == PortalAccountHolder.Agency)
            {
                agencyCheck ??= await AgencyCheckAsync(orgId, last, ct);
                check = agencyCheck;
            }
            else
            {
                var ownerId = SignatoryOf(owners, propertyId, DateOnly.FromDateTime(stay.CheckOut), stayFees.Select(f => f.OwnerId));
                if (!ownerChecks.TryGetValue(ownerId, out check!))
                    ownerChecks[ownerId] = check = OwnerCheck(owners, ownerId, last);
            }

            if (check.NotDue is { } reason)
            {
                AddNotDue(reason, channel.ToString());
                continue;
            }

            if (!portals.TryGetValue(channel, out var portal))
            {
                AddNotDue(SelfBillingNotDueReasons.PortalNotConfigured, channel.ToString());
                continue;
            }

            if (check.FailureCode is { } code)
            {
                failures[(check.OwnerId, portal.Key)] = new SelfBillingFailure(check.Kind, check.OwnerId, portal.Name, code, check.Fields);
                continue;
            }

            var holder = check.Holder!;
            if (!groups.TryGetValue((holder.Profile.Id, portal.Key), out var group))
                groups[(holder.Profile.Id, portal.Key)] = group = new Group(holder, portal);

            group.PropertyNets[propertyId] = group.PropertyNets.GetValueOrDefault(propertyId) + amount;
            group.Stays.Add(stayFees.Key);

            // What did not come from the figure of the portal itself (a rule of the agency, the default of the channel) is an estimate: the host checks it before the issue.
            group.Estimated += -stayFees.Where(f => OwnerReconciliationRules.SourceOf(f.RuleVersion) != ChannelCommissionSource.Portal).Sum(f => f.Amount);

            void AddNotDue(string why, string channelName)
            {
                var key = (why, channelName, currency);
                var known = notDue.GetValueOrDefault(key);
                notDue[key] = (known.Stays + 1, known.Amount + amount);
            }
        }

        // A TD17 needs something to invoice: the groups whose commissions net to nothing (a stay fully reversed) are no document.
        plan.Groups.AddRange(groups.Values
            .Where(g => g.Taxable > 0m)
            .OrderBy(g => g.Holder.Kind)
            .ThenBy(g => g.Holder.OwnerId)
            .ThenBy(g => g.Portal.Name, StringComparer.Ordinal));
        plan.Failed.AddRange(failures.Values.OrderBy(f => f.HolderKind).ThenBy(f => f.HolderOwnerId).ThenBy(f => f.PortalName, StringComparer.Ordinal));
        plan.NotDue.AddRange(notDue
            .OrderBy(n => n.Key.Reason, StringComparer.Ordinal)
            .ThenBy(n => n.Key.Channel, StringComparer.Ordinal)
            .Select(n => new SelfBillingNotDue(n.Key.Reason, n.Key.Channel, n.Value.Stays, n.Value.Amount, n.Key.Currency)));
        return plan;
    }

    /// <summary>
    /// The agency as the holder of an account (V2): subject when it is an Italian issuer with a VAT number (a Swiss agency has no TD17, an issuer without a VAT number integrates
    /// nothing), able when its profile is complete (<see cref="IDocumentIssuerService.GetSettingsAsync"/>: the one definition of an issuer that can issue).
    /// </summary>
    private async Task<HolderCheck> AgencyCheckAsync(Guid orgId, DateOnly last, CancellationToken ct)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
        if (profile is null)
            return HolderCheck.Cannot(FiscalDocumentErrors.ProfileIncomplete, [], SelfBillingHolderKind.Agency, null);
        if (!IsItalian(profile))
            return HolderCheck.NotSubject(SelfBillingNotDueReasons.HolderNotItalian, SelfBillingHolderKind.Agency, null);
        if (profile.DocumentRegime == DocumentRegime.PrivatePerson)
            return HolderCheck.NotSubject(SelfBillingNotDueReasons.HolderNotVatRegistered, SelfBillingHolderKind.Agency, null);

        var settings = await issuers.GetSettingsAsync(orgId, ct);
        return profile.DocumentRegime is null || settings.MissingFields.Count > 0
            ? HolderCheck.Cannot(FiscalDocumentErrors.ProfileIncomplete, settings.MissingFields, SelfBillingHolderKind.Agency, null)
            : HolderCheck.Ready(new HolderOfAccount(SelfBillingHolderKind.Agency, null, profile));
    }

    /// <summary>
    /// An owner as the holder of an account (V1): subject when its profile is an Italian one with a VAT number (an owner company, a professional; a private person has none: the
    /// portal charged the Italian VAT, a foreign owner has no Italian TD17), able when that profile is complete (the rule of the document plan). An owner with no profile that is a
    /// company, or has a VAT number, is subject and cannot yet; any other is a private person.
    /// </summary>
    private HolderCheck OwnerCheck(OwnerContext context, Guid ownerId, DateOnly last)
    {
        context.Profiles.TryGetValue(ownerId, out var profile);
        context.Owners.TryGetValue(ownerId, out var owner);
        var company = owner is not null && (owner.EntityKind == OwnerEntityKind.LegalEntity || !string.IsNullOrWhiteSpace(owner.VatNumber));
        if (profile is null)
        {
            return company
                ? HolderCheck.Cannot(FiscalDocumentErrors.OwnerIssuerMissing, [], SelfBillingHolderKind.Owner, ownerId)
                : HolderCheck.NotSubject(SelfBillingNotDueReasons.PrivateHolder, SelfBillingHolderKind.Owner, ownerId);
        }

        if (!IsItalian(profile))
            return HolderCheck.NotSubject(SelfBillingNotDueReasons.HolderNotItalian, SelfBillingHolderKind.Owner, ownerId);
        if (profile.DocumentRegime == DocumentRegime.PrivatePerson)
            return HolderCheck.NotSubject(SelfBillingNotDueReasons.PrivateHolder, SelfBillingHolderKind.Owner, ownerId);

        var ruleRegimeCode = taxRules.TryResolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, last)?.FatturaPaRegimeCode;
        var missing = OwnerIssuerProfileRules.MissingFields(profile, ruleRegimeCode);
        if (profile.DocumentRegime is null && !company)
            return HolderCheck.NotSubject(SelfBillingNotDueReasons.PrivateHolder, SelfBillingHolderKind.Owner, ownerId);

        return missing.Count > 0
            ? HolderCheck.Cannot(FiscalDocumentErrors.OwnerIssuerMissing, missing, SelfBillingHolderKind.Owner, ownerId)
            : HolderCheck.Ready(new HolderOfAccount(SelfBillingHolderKind.Owner, ownerId, profile));
    }

    private static bool IsItalian(OrgFiscalIssuerProfile profile) =>
        string.Equals(profile.CountryCode?.Trim(), FiscalDocumentRules.Italy, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ownerships and the mandates of the properties of the month, the issuer profiles and the registry of their owners.</summary>
    private sealed record OwnerContext(
        IReadOnlyDictionary<Guid, List<OwnershipFacts>> OwnershipsByProperty,
        IReadOnlyList<OwnerMandate> Mandates,
        IReadOnlyDictionary<Guid, OrgFiscalIssuerProfile> Profiles,
        IReadOnlyDictionary<Guid, OwnerFacts> Owners);

    private async Task<OwnerContext> OwnerContextAsync(Guid orgId, IReadOnlyList<Guid> propertyIds, CancellationToken ct)
    {
        var ownerships = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OrgId == orgId && propertyIds.Contains(o.PropertyId))
            .Select(o => new OwnershipFacts(o.Id, o.PropertyId, o.PropertyOwnerId, o.SharePercent, o.OwnershipStart, o.OwnershipEnd))
            .ToListAsync(ct);
        var ownerIds = ownerships.Select(o => o.OwnerId).Distinct().ToList();
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == orgId && ownerIds.Contains(m.PropertyOwnerId))
            .ToListAsync(ct);
        var profiles = await db.OrgFiscalIssuerProfiles.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId != null && ownerIds.Contains(p.PropertyOwnerId.Value))
            .ToDictionaryAsync(p => p.PropertyOwnerId!.Value, ct);
        var owners = await db.PropertyOwners.AsNoTracking()
            .Where(o => o.OrgId == orgId && ownerIds.Contains(o.Id))
            .Select(o => new OwnerFacts(o.Id, o.EntityKind, o.VatNumber))
            .ToDictionaryAsync(o => o.Id, ct);
        return new OwnerContext(ownerships.GroupBy(o => o.PropertyId).ToDictionary(g => g.Key, g => g.ToList()), mandates, profiles, owners);
    }

    /// <summary>
    /// The owner who signs for the property on <paramref name="day"/> (the rule of the document plan); without an ownership in force the smallest owner id among the owners the
    /// ledger shared the commission with, so a stay is never left without a holder.
    /// </summary>
    private static Guid SignatoryOf(OwnerContext context, Guid propertyId, DateOnly day, IEnumerable<Guid> ledgerOwners)
    {
        var active = context.OwnershipsByProperty.GetValueOrDefault(propertyId, [])
            .Where(o => DocumentPlanRules.IsActiveOn(o.Start, o.End, day))
            .Select(o => new ActiveOwnership(o.Id, o.OwnerId, o.SharePercent, o.Start))
            .ToList();
        return DocumentPlanRules.SignatoryOf(active, context.Mandates, day)?.OwnerId ?? ledgerOwners.Min();
    }

    // ---------------------------------------------------------------- the document

    /// <summary>What a draft is made of: the rule of the TD17 and its amounts (the net of each property, the VAT integrated on top).</summary>
    private sealed record Composition(FiscalTaxRule Rule, FiscalDocumentAmounts Amounts);

    private Composition Compose(Group group, IReadOnlyDictionary<Guid, string> names, int year, int month, DateOnly last)
    {
        var profile = group.Holder.Profile;
        var regimeCode = Clean(profile.FatturaPaRegimeCode)
            ?? taxRules.TryResolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, last)?.FatturaPaRegimeCode;
        var rule = SelfBillingRules.RuleOf(options.Value, profile.DocumentRegime ?? DocumentRegime.VatOrdinary, regimeCode, last);
        var rate = rule.RateOf(FiscalLineType.Commission);
        var period = FiscalDocumentPdfs.PeriodLabel(year, month);

        // One line for each property, by name: the net commission of its stays of the month (what nets to nothing or less is not something to integrate).
        var lines = group.PropertyNets
            .Where(net => net.Value > 0m)
            .OrderBy(net => names.GetValueOrDefault(net.Key, string.Empty), StringComparer.Ordinal)
            .ThenBy(net => net.Key)
            .Select(net => new FiscalDocumentNetLine(
                Truncate(texts.Get("SelfBillingLineCommissions", LineLanguage, group.Portal.Name, names.GetValueOrDefault(net.Key, string.Empty), period), FiscalDocumentLine.DescriptionMaxLength),
                1m,
                net.Value,
                rate.Percent,
                rate.NatureCode,
                FiscalLineType.Commission))
            .ToList();
        return new Composition(rule, FiscalDocumentTotals.FromNet(lines, rule.StampDuty));
    }

    /// <summary>
    /// The fields of a TD17 for <paramref name="group"/>: the issuer profile it is issued on, the foreign supplier in the recipient columns (with the SDI code and the PEC of the
    /// holder, where the file is delivered), the month, the version of the settings and the amounts with their lines. Touches nothing of the database: the caller removes the
    /// lines a draft had and adds the new ones.
    /// </summary>
    private static void Apply(FiscalDocument draft, Guid orgId, int year, int month, Group group, Composition composition)
    {
        var first = new DateOnly(year, month, 1);
        var portal = group.Portal;
        draft.OrgId = orgId;
        draft.BookingId = null;
        draft.PropertyId = null;
        draft.Purpose = FiscalDocumentPurpose.SelfBilling;
        draft.IssuerProfileId = group.Holder.Profile.Id;
        draft.ForAgencyServices = false;
        draft.RecipientOwnerId = null;
        draft.OwnerStatementVersionId = null;
        draft.PeriodYear = year;
        draft.PeriodMonth = month;
        draft.ServicePeriodFrom = first;
        draft.ServicePeriodTo = first.AddMonths(1).AddDays(-1);
        draft.RuleVersion = composition.Rule.Version;
        draft.Kind = DocumentKind.Invoice;
        draft.Currency = StayCurrency.Euro;
        draft.PaymentChannel = FiscalPaymentChannel.Unknown;
        draft.PaymentState = FiscalDocumentPaymentState.Unknown;

        draft.RecipientKind = InvoiceRecipientKind.Company;
        draft.RecipientName = portal.Name;
        draft.RecipientFiscalCode = null;
        draft.RecipientVatNumber = portal.VatNumber;
        draft.RecipientAddressStreet = portal.Street;
        draft.RecipientAddressCivic = portal.Civic;
        draft.RecipientPostalCode = portal.PostalCode;
        draft.RecipientCity = portal.City;
        draft.RecipientProvince = null;
        draft.RecipientCountryCode = portal.CountryCode;
        draft.RecipientSdiCode = Clean(group.Holder.Profile.IssuerSdiRecipientCode)?.ToUpperInvariant();
        draft.RecipientPec = Clean(group.Holder.Profile.IssuerPec)?.ToLowerInvariant();

        draft.Lines.Clear();
        FiscalDocumentService.ApplyAmounts(draft, composition.Amounts);
    }

    /// <summary>The document is the live TD17 of <paramref name="group"/>: made on the profile of its holder, for the legal entity of its portal.</summary>
    private static bool IsDocumentOf(FiscalDocument document, Group group) =>
        document.IssuerProfileId == group.Holder.Profile.Id
        && string.Equals(SelfBillingRules.PortalKey(document.RecipientCountryCode, document.RecipientVatNumber), group.Portal.Key, StringComparison.Ordinal);

    /// <summary>
    /// The draft still is what the ledger gives today (<paramref name="expected"/> is the document made now): the issuer, the version of the settings, the supplier and where the
    /// file goes, the totals and every line (type, net, rate, nature, VAT). The descriptions are not compared: a renamed property does not change what the holder integrates.
    /// </summary>
    private static bool IsTheSameDraft(FiscalDocument draft, FiscalDocument expected)
    {
        if (draft.IssuerProfileId != expected.IssuerProfileId
            || draft.Kind != expected.Kind
            || !Same(draft.RuleVersion, expected.RuleVersion)
            || !Same(draft.Currency, expected.Currency)
            || draft.TaxableBase != expected.TaxableBase || draft.VatAmount != expected.VatAmount || draft.StampDuty != expected.StampDuty || draft.Total != expected.Total
            || draft.RecipientKind != expected.RecipientKind
            || !Same(draft.RecipientName, expected.RecipientName) || !Same(draft.RecipientVatNumber, expected.RecipientVatNumber)
            || !Same(draft.RecipientAddressStreet, expected.RecipientAddressStreet) || !Same(draft.RecipientAddressCivic, expected.RecipientAddressCivic)
            || !Same(draft.RecipientPostalCode, expected.RecipientPostalCode) || !Same(draft.RecipientCity, expected.RecipientCity)
            || !Same(draft.RecipientCountryCode, expected.RecipientCountryCode)
            || !Same(draft.RecipientSdiCode, expected.RecipientSdiCode) || !Same(draft.RecipientPec, expected.RecipientPec))
        {
            return false;
        }

        var stored = draft.Lines.OrderBy(l => l.LineNumber).ToList();
        var wanted = expected.Lines.OrderBy(l => l.LineNumber).ToList();
        return stored.Count == wanted.Count
            && stored.Zip(wanted).All(pair =>
                pair.First.LineType == pair.Second.LineType
                && pair.First.TotalPrice == pair.Second.TotalPrice
                && pair.First.VatRatePercent == pair.Second.VatRatePercent
                && Same(pair.First.VatNatureCode, pair.Second.VatNatureCode)
                && pair.First.VatAmount == pair.Second.VatAmount);

        static bool Same(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
    }

    private SelfBillingOutcome OutcomeOf(FiscalDocument document, Group group, string result, bool changedSinceIssue) => new(
        document.Id,
        group.Holder.Kind,
        group.Holder.OwnerId,
        group.Portal.Name,
        result,
        document.TaxableBase,
        EstimateOf(group),
        changedSinceIssue);

    // ---------------------------------------------------------------- rows

    /// <summary>The holder of a TD17 as its row shows it: agency or owner, and the legal name.</summary>
    private sealed record HolderInfo(SelfBillingHolderKind Kind, Guid? OwnerId, string Name);

    private async Task<SelfBillingDetail> DetailOfAsync(Guid orgId, FiscalDocument document, decimal estimated, CancellationToken ct)
    {
        var info = await HolderInfoAsync(orgId, [document], ct);
        return new SelfBillingDetail(document, RowOf(document, info), estimated);
    }

    /// <summary>
    /// The estimate of a draft as the plan gives it now (the figure the issue asks to accept), recomputed as the issue does; 0 for a document that is not a draft and for a draft the ledger no
    /// longer supports (the next generation drops it).
    /// </summary>
    private async Task<decimal> EstimatedAmountOfAsync(Guid orgId, FiscalDocument document, CancellationToken ct)
    {
        if (document.Status != FiscalDocumentStatus.Draft || document.PeriodYear is not { } year || document.PeriodMonth is not { } month)
            return 0m;

        var plan = await PlanAsync(orgId, year, month, ct);
        var group = plan.Groups.FirstOrDefault(g => IsDocumentOf(document, g));
        return group is null ? 0m : EstimateOf(group);
    }

    /// <summary>The part of the taxable amount of a group that is an estimate (a rule of the agency or a default, not the figure of the portal), never more than what is invoiced.</summary>
    private static decimal EstimateOf(Group group) => Math.Clamp(group.Estimated, 0m, group.Taxable);

    private async Task<IReadOnlyDictionary<Guid, HolderInfo>> HolderInfoAsync(Guid orgId, IReadOnlyCollection<FiscalDocument> found, CancellationToken ct)
    {
        var ids = found.Where(d => d.IssuerProfileId is not null).Select(d => d.IssuerProfileId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, HolderInfo>();

        var profiles = await db.OrgFiscalIssuerProfiles.AsNoTracking().Where(p => p.OrgId == orgId && ids.Contains(p.Id)).ToListAsync(ct);
        var orgName = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return profiles.ToDictionary(
            p => p.Id,
            p => new HolderInfo(
                p.PropertyOwnerId is null ? SelfBillingHolderKind.Agency : SelfBillingHolderKind.Owner,
                p.PropertyOwnerId,
                Clean(p.LegalName) ?? orgName));
    }

    private SelfBillingRow RowOf(FiscalDocument document, IReadOnlyDictionary<Guid, HolderInfo> info)
    {
        var holder = document.IssuerProfileId is { } id && info.TryGetValue(id, out var found) ? found : new HolderInfo(SelfBillingHolderKind.Agency, null, string.Empty);

        // An issued document names its holder as it was at the issue, never as the profile says today.
        var name = document.Status == FiscalDocumentStatus.Issued && FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot) is { } snapshot && snapshot.DisplayName.Length > 0
            ? snapshot.DisplayName
            : holder.Name;
        var year = document.PeriodYear ?? 0;
        var month = document.PeriodMonth ?? 0;
        return new SelfBillingRow(
            document.Id,
            holder.Kind,
            holder.OwnerId,
            name,
            document.RecipientName ?? string.Empty,
            document.RecipientCountryCode ?? string.Empty,
            year,
            month,
            year is > 0 && month is >= 1 and <= 12 ? SelfBillingRules.DueDate(year, month, options.Value.EffectiveIssueByDay) : default,
            document.Number,
            document.IssueDate,
            document.Status,
            document.TaxableBase,
            document.VatAmount,
            document.Total,
            document.Currency,
            document.SdiStatus);
    }

    private async Task<Dictionary<Guid, string>> PropertyNamesAsync(Guid orgId, IReadOnlyList<Guid> propertyIds, CancellationToken ct) =>
        propertyIds.Count == 0
            ? []
            : await db.Properties.AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The month is over (Europe/Rome, as the close of an owner's month asks): a TD17 integrates the commissions of a whole month and an issued one is not completed afterwards, so neither the
    /// drafts nor the issue are made while the month runs. 422 <c>self_billing_period_not_ended</c> otherwise.
    /// </summary>
    private void RequirePeriodOver(int year, int month)
    {
        var last = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        if (last >= time.TodayInRomeAsDateOnly())
            throw new DomainRuleException(SelfBillingErrors.PeriodNotEnded, "SelfBillingPeriodNotEnded");
    }

    /// <summary>The settings can make a TD17: a VAT rate above 0 and at most 100, and a portal that can be invoiced. 422 <c>self_billing_not_configured</c> otherwise.</summary>
    private void RequireConfigured()
    {
        var settings = options.Value;
        if (!settings.HasValidVatRate || settings.ConfiguredPortals().Count == 0)
            throw new DomainRuleException(SelfBillingErrors.NotConfigured, "SelfBillingNotConfigured");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static NotFoundException DocumentNotFound(Guid documentId) =>
        new($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };
}
