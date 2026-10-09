using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IShowcaseBookingManager"/>
/// <remarks>
/// <para><b>Identification.</b> The booking is looked for by the supplier's slug and the code, with the explicit supplier org in the
/// statement (<see cref="RowsOf"/>), and only then is the customer's address compared: the stored one is decrypted by the column,
/// and compared in constant time with the one typed (<see cref="ServiceCustomerEmails.SameAddress"/>). The three statements — the
/// supplier by slug, the booking by code, the customer's contact — and the comparison <b>run for every attempt</b>, with values that
/// match nothing when the credentials are not valid or the supplier or the booking is not there, so a wrong slug, a wrong code, a
/// code of another supplier and a wrong address cost the same and fail the same way
/// (<see cref="ShowcaseBookingManagementErrors.BookingNotFound"/>, one body). The supplier is found whatever its status: a customer
/// can see and cancel its booking of a supplier that was suspended meanwhile.</para>
/// <para><b>What the view is made of.</b> <see cref="RowsOf"/> selects the columns the customer may read and nothing else (no
/// completion notes, no photos, no member who took the request, no encrypted column); the exact address comes from its own
/// statement (<see cref="LocationOf"/>) and only for a request the supplier took. <c>ShowcaseBookingManagerSqlTests</c> reads the
/// SQL of both.</para>
/// <para><b>The actions</b> are those of <see cref="IShowcaseRequestCustomerActions"/> (the lock, the planner, the <c>xmin</c> check, the
/// notifications); this class proves who is asking and answers with the booking as it is afterwards, read again from the database.
/// It writes nothing about the customer to a log.</para>
/// </remarks>
public sealed class ShowcaseBookingManager(
    AppDbContext db,
    IServiceCustomerReader customers,
    ISupplierServiceCatalogService catalog,
    IShowcaseRequestCustomerActions actions,
    IOptions<ShowcaseBookingOptions> options,
    TimeProvider? timeProvider = null) : IShowcaseBookingManager
{
    /// <summary>
    /// What a slug and a code that cannot be valid are looked up as: values that match nothing (a slug never starts with a hyphen, a code
    /// is ten letters and digits), so the statements still run and find no row.
    /// </summary>
    private const string NothingToMatch = "-";

    /// <summary>The address compared when there is none to compare with, so the comparison runs for every attempt.</summary>
    private const string NoAddress = "nobody@invalid.invalid";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseBookingView> LookupAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken = default)
    {
        var found = await IdentifyAsync(credentials, cancellationToken);
        return await BuildViewAsync(found, cancellationToken);
    }

    public async Task<ShowcaseBookingView> CancelAsync(
        ShowcaseBookingCredentials credentials,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var found = await IdentifyAsync(credentials, cancellationToken);
        await actions.CancelAsCustomerAsync(found.Row.Id, found.Supplier.OrgId, reason, cancellationToken);
        return await AfterAsync(found, cancellationToken);
    }

    public async Task<ShowcaseBookingView> RescheduleAsync(
        ShowcaseBookingCredentials credentials,
        DateTime startUtc,
        CancellationToken cancellationToken = default)
    {
        var found = await IdentifyAsync(credentials, cancellationToken);
        await actions.RescheduleAsCustomerAsync(found.Row.Id, found.Supplier.OrgId, startUtc, cancellationToken);
        return await AfterAsync(found, cancellationToken);
    }

    public async Task<ShowcaseBookingView> AcceptProposalAsync(
        ShowcaseBookingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var found = await IdentifyAsync(credentials, cancellationToken);
        await actions.AcceptProposalAsCustomerAsync(found.Row.Id, found.Supplier.OrgId, cancellationToken);
        return await AfterAsync(found, cancellationToken);
    }

    public async Task<ShowcaseBookingView> RejectProposalAsync(
        ShowcaseBookingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var found = await IdentifyAsync(credentials, cancellationToken);
        await actions.RejectProposalAsCustomerAsync(found.Row.Id, found.Supplier.OrgId, cancellationToken);
        return await AfterAsync(found, cancellationToken);
    }

    // ─── Who is asking ───────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Found(SupplierRow Supplier, ShowcaseBookingRow Row);

    /// <summary>
    /// The booking of the credentials, or <see cref="ShowcaseBookingManagementErrors.BookingNotFound"/>. The statements and the
    /// comparison run whatever the credentials are, and the failure is decided at the end, once: see the remarks of the class.
    /// </summary>
    private async Task<Found> IdentifyAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken)
    {
        var valid = ShowcaseBookingManagementRules.TryNormalize(credentials, out var key);

        var supplier = await SupplierBySlugOf(db, valid ? key!.Slug : NothingToMatch).FirstOrDefaultAsync(cancellationToken);
        var supplierOrgId = supplier?.OrgId ?? Guid.Empty;

        var row = await RowsOf(db, supplierOrgId)
            .Where(r => r.PublicCode == (valid ? key!.Code : NothingToMatch))
            .FirstOrDefaultAsync(cancellationToken);

        // The customer of the booking, read for this supplier only: the stored address, decrypted by the column. A request that has no
        // customer (it cannot happen: the database checks it) and a customer the retention anonymized have no address to match.
        var contact = await customers.FindContactAsync(supplierOrgId, row?.CustomerId ?? Guid.Empty, cancellationToken);
        var stored = contact is { Anonymized: false } ? contact.Email : null;
        var sameAddress = ServiceCustomerEmails.SameAddress(stored, valid ? key!.Email : NoAddress);

        if (!valid || supplier is null || row is null || !sameAddress)
            throw ShowcaseBookingManagementErrors.BookingNotFound();

        return new Found(supplier, row);
    }

    // ─── What the customer is shown ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The booking as it is after an action: read again, so the answer is what the database holds.</summary>
    private async Task<ShowcaseBookingView> AfterAsync(Found found, CancellationToken cancellationToken)
    {
        var row = await RowsOf(db, found.Supplier.OrgId)
            .Where(r => r.Id == found.Row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ShowcaseBookingManagementErrors.BookingNotFound();

        // The supplier can have changed meanwhile as well (suspended, the service paused): the view is built from what it is now.
        var supplier = await SupplierBySlugOf(db, found.Supplier.Slug).FirstOrDefaultAsync(cancellationToken) ?? found.Supplier;
        return await BuildViewAsync(new Found(supplier, row), cancellationToken);
    }

    private async Task<ShowcaseBookingView> BuildViewAsync(Found found, CancellationToken cancellationToken)
    {
        var (supplier, row) = (found.Supplier, found.Row);
        var now = UtcNow();
        var hours = options.Value.FreeCancellationHours;

        // The exact address, the floor and the notes for the access: only once the supplier took the request, and from their own statement.
        var location = SupplierJobDisclosure.IsDisclosed(row.Status)
            ? await LocationOf(db, supplier.OrgId, row.Id).FirstOrDefaultAsync(cancellationToken)
            : null;

        var service = row.ServiceListingId is { } listingId
            ? await catalog.FindForRequestAsync(supplier.OrgId, listingId, cancellationToken)
            : null;
        var published = service is { IsRequestable: true };
        var supplierActive = supplier.Status == SupplierStatus.Active;

        var start = row.ScheduledStartUtc ?? row.CreatedAt;
        var end = row.ScheduledEndUtc ?? start;
        var proposalPending = row is { ProposedStartUtc: not null, ProposedEndUtc: not null, ProposedAt: not null };
        var answerBy = proposalPending ? row.ResponseDueAt : null;

        var proposal = proposalPending
            ? new ShowcaseBookingProposal(row.ProposedStartUtc!.Value, row.ProposedEndUtc!.Value, row.ProposedAt!.Value, answerBy, row.ProposalMessage)
            : null;

        var cancellation = row.Status == ServiceRequestStatus.Annullato
            ? new ShowcaseBookingCancellation(
                row.CancelledAt ?? row.CreatedAt,
                row.CancelledBy ?? ServiceRequestActorParty.System,
                string.IsNullOrWhiteSpace(row.CancellationReason) || ServiceRequestCancellationReasons.IsCode(row.CancellationReason)
                    ? null
                    : row.CancellationReason)
            : null;

        return new ShowcaseBookingView(
            row.PublicCode ?? string.Empty,
            row.Status,
            new ShowcaseBookingServiceInfo(row.ServiceNameSnapshot ?? string.Empty, published ? service!.Slug : null),
            new ShowcaseBookingSupplierInfo(supplier.LegalName, supplier.Slug),
            start,
            end,
            new ShowcaseBookingPlace(
                row.LocationCity ?? string.Empty,
                row.LocationPostalCode,
                location?.Address,
                location?.Floor,
                location?.AccessNotes),
            PriceOf(row),
            row.Status == ServiceRequestStatus.Richiesto && !proposalPending ? row.ResponseDueAt : null,
            proposal,
            cancellation,
            row.Status == ServiceRequestStatus.Rifiutato && !string.IsNullOrWhiteSpace(row.RejectionReason) ? row.RejectionReason : null,
            new ShowcaseBookingCancellationTerms(
                ShowcaseBookingManagementRules.FreeCancellationUntil(start, hours),
                ShowcaseBookingManagementRules.IsFreeCancellation(start, now, hours)),
            new ShowcaseBookingActions(
                ShowcaseBookingManagementRules.CanCancel(row.Status, row.ScheduledStartUtc, now),
                ShowcaseBookingManagementRules.CanReschedule(row.Status, row.ScheduledStartUtc is not null, supplierActive, published),
                ShowcaseBookingManagementRules.CanRespondToProposal(row.Status, proposalPending, answerBy, now, supplierActive)));
    }

    /// <summary>
    /// The price of the booking: the amounts, and the lines — the choices of the customer when it booked, or, once the supplier
    /// completed the work, the lines of the final amount.
    /// </summary>
    private static ShowcaseBookingPrice PriceOf(ShowcaseBookingRow row)
    {
        var final = ServiceRequestJson.ReadPriceLines(row.PriceLinesJson);
        var lines = row.FinalAmountCents is not null && final.Count > 0
            ? final
                .Select(line => new ShowcaseBookingPriceLine(
                    line.Kind == ServiceRequestPriceLineKinds.Extra ? "extra" : "service",
                    line.Label,
                    1,
                    line.AmountCents,
                    line.AmountCents))
                .ToList()
            : ServiceRequestJson.ReadOptions(row.OptionsJson)
                .Select(option => new ShowcaseBookingPriceLine(
                    option.Code == ServiceRequestOptionCodes.Quantity ? "service" : "option",
                    option.Label,
                    option.Quantity,
                    option.AmountCents,
                    (int)Math.Min(int.MaxValue, (long)option.AmountCents * option.Quantity)))
                .ToList();

        return new ShowcaseBookingPrice(row.EstimatedAmountCents, row.QuotedAmountCents, row.FinalAmountCents, lines);
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    // ─── The statements ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The supplier of a showcase slug (unique), whatever its status. SupplierProfile is keyed by the supplier org and not tenant-filtered.</summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<SupplierRow> SupplierBySlugOf(AppDbContext db, string normalizedSlug) =>
        db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.ShowcaseSlug == normalizedSlug)
            .Select(sp => new SupplierRow(sp.OrgId, sp.Status, sp.LegalName, sp.ShowcaseSlug!));

    /// <summary>
    /// The showcase requests of <paramref name="supplierOrgId"/>, with the columns the customer may read and no others. ServiceRequest
    /// has two parties and is read with explicit predicates (the supplier org and the rental context), never through the filters of a host.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<ShowcaseBookingRow> RowsOf(AppDbContext db, Guid supplierOrgId) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && r.RentalContext == ServiceRequestRentalContext.Showcase)
            .Select(r => new ShowcaseBookingRow
            {
                Id = r.Id,
                CustomerId = r.CustomerId,
                Status = r.Status,
                PublicCode = r.PublicCode,
                ServiceListingId = r.ServiceListingId,
                ServiceNameSnapshot = r.ServiceNameSnapshot,
                ScheduledStartUtc = r.ScheduledStartUtc,
                ScheduledEndUtc = r.ScheduledEndUtc,
                EstimatedAmountCents = r.EstimatedAmountCents,
                QuotedAmountCents = r.QuotedAmountCents,
                FinalAmountCents = r.FinalAmountCents,
                PriceLinesJson = r.PriceLinesJson,
                OptionsJson = r.OptionsJson,
                ResponseDueAt = r.ResponseDueAt,
                ProposedStartUtc = r.ProposedStartUtc,
                ProposedEndUtc = r.ProposedEndUtc,
                ProposedAt = r.ProposedAt,
                ProposalMessage = r.ProposalMessage,
                CancelledAt = r.CancelledAt,
                CancelledBy = r.CancelledBy,
                CancellationReason = r.CancellationReason,
                RejectionReason = r.RejectionReason,
                LocationCity = r.LocationCity,
                LocationPostalCode = r.LocationPostalCode,
                CreatedAt = r.CreatedAt,
            });

    /// <summary>
    /// The street address, the floor and the notes for the access of one showcase request of <paramref name="supplierOrgId"/>: the
    /// three encrypted columns, read from their own statement and only for a request the supplier took.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<ShowcaseBookingLocation> LocationOf(AppDbContext db, Guid supplierOrgId, Guid requestId) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == requestId && r.SupplierOrgId == supplierOrgId && r.RentalContext == ServiceRequestRentalContext.Showcase)
            .Select(r => new ShowcaseBookingLocation(r.LocationAddress, r.LocationFloor, r.LocationAccessNotes));

    /// <summary>The supplier of a booking: what the customer's view and the rules need of its profile.</summary>
    internal sealed record SupplierRow(Guid OrgId, SupplierStatus Status, string LegalName, string Slug);

    /// <summary>The place a taken request is at, as the customer wrote it.</summary>
    internal sealed record ShowcaseBookingLocation(string? Address, string? Floor, string? AccessNotes);

    /// <summary>The columns of a showcase request that the customer's view is made of.</summary>
    internal sealed class ShowcaseBookingRow
    {
        public Guid Id { get; init; }

        public Guid? CustomerId { get; init; }

        public ServiceRequestStatus Status { get; init; }

        public string? PublicCode { get; init; }

        public Guid? ServiceListingId { get; init; }

        public string? ServiceNameSnapshot { get; init; }

        public DateTime? ScheduledStartUtc { get; init; }

        public DateTime? ScheduledEndUtc { get; init; }

        public int? EstimatedAmountCents { get; init; }

        public int? QuotedAmountCents { get; init; }

        public int? FinalAmountCents { get; init; }

        public string PriceLinesJson { get; init; } = "[]";

        public string OptionsJson { get; init; } = "[]";

        public DateTime? ResponseDueAt { get; init; }

        public DateTime? ProposedStartUtc { get; init; }

        public DateTime? ProposedEndUtc { get; init; }

        public DateTime? ProposedAt { get; init; }

        public string? ProposalMessage { get; init; }

        public DateTime? CancelledAt { get; init; }

        public ServiceRequestActorParty? CancelledBy { get; init; }

        public string? CancellationReason { get; init; }

        public string? RejectionReason { get; init; }

        public string? LocationCity { get; init; }

        public string? LocationPostalCode { get; init; }

        public DateTime CreatedAt { get; init; }
    }
}
