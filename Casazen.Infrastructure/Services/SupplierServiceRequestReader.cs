using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Supplier console reads of the service requests (SU-08, A4-14; SP-04). The rows are projected column by column: the guest of
/// the stay is never joined nor loaded, so no guest data can reach the supplier; the name of the property, the host's notes,
/// the street address and the host contact are filled only for a request the supplier took
/// (<see cref="SupplierJobDisclosure"/>, decision D9). Since SP-04e the views also carry the payment summary of a completed job, read
/// through <see cref="ISupplierPaymentReadModels"/> (without it a view has no summary), and the counters of the tabs
/// (<c>SupplierServiceRequestReader.Counts.cs</c>).
/// </summary>
public partial class SupplierServiceRequestReader(
    AppDbContext db,
    TimeProvider? timeProvider = null,
    ISupplierPaymentReadModels? payments = null) : ISupplierServiceRequestReader
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<(IReadOnlyList<SupplierServiceRequestView> Items, int Total)> ListAsync(
        Guid supplierOrgId,
        SupplierInboxQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = Filter(Rows(supplierOrgId), query);

        var total = await rows.CountAsync(cancellationToken);
        var page = await Sort(rows, query.Sort)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var ownerPhones = await OwnerPhonesAsync(page, cancellationToken);
        var summaries = await PaymentSummariesAsync(supplierOrgId, page, cancellationToken);
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        return (page.Select(r => ToView(r, ownerPhones, summaries.GetValueOrDefault(r.Id), history: null, nowUtc)).ToList(), total);
    }

    internal IQueryable<SupplierRequestRow> Filter(IQueryable<SupplierRequestRow> rows, SupplierInboxQuery query)
    {
        if (query.Statuses.Count > 0)
        {
            var statuses = query.Statuses.ToArray();
            rows = rows.Where(r => statuses.Contains(r.Status));
        }

        if (query.From is { } from)
        {
            var startUtc = RomeCalendar.StartOfDayUtc(from.ToDateTime(TimeOnly.MinValue));
            rows = rows.Where(r => r.ActivityAt >= startUtc);
        }

        if (query.To is { } to)
        {
            var endUtc = RomeCalendar.StartOfDayUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue));
            rows = rows.Where(r => r.ActivityAt < endUtc);
        }

        if (query.ClientId is { } clientId)
            rows = rows.Where(r => r.ClientId == clientId);

        if (!string.IsNullOrWhiteSpace(query.Service))
        {
            var service = query.Service.Trim();
            if (Guid.TryParse(service, out var listingId))
            {
                rows = rows.Where(r => r.ServiceListingId == listingId);
            }
            else
            {
                var category = service.ToLowerInvariant();
                rows = rows.Where(r => r.Category == category);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Comune))
        {
            var comune = query.Comune.Trim();
            var lowered = comune.ToLowerInvariant();
            rows = rows.Where(r => r.ComuneIstatCode == comune || r.City.ToLower() == lowered);
        }

        (DateOnly From, DateOnly To)? workDays = query.WorkDays is { } days
            ? (days.From, days.To)
            : query.When is { } when
                ? SupplierInboxWhens.RangeOf(when, _clock.TodayInRomeAsDateOnly())
                : null;
        if (workDays is { } range)
        {
            var startUtc = RomeCalendar.StartOfDayUtc(range.From);
            var endUtc = RomeCalendar.StartOfDayUtc(range.To.AddDays(1));
            rows = rows.Where(r => r.WorkAt >= startUtc && r.WorkAt < endUtc);
        }

        if (query.Source is { } source)
            rows = rows.Where(r => r.Source == source);

        if (query.AwaitingCustomer is { } awaiting)
        {
            rows = awaiting
                ? rows.Where(r => r.ProposedStartUtc != null || r.QuoteSentAt != null)
                : rows.Where(r => r.ProposedStartUtc == null && r.QuoteSentAt == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
            rows = FilterByText(rows, query.Text.Trim());

        return rows;
    }

    /// <summary>
    /// The free text of the inbox (<c>q</c>): the requests whose service name, category or comune contain it (any case), or whose
    /// booking code it <b>is</b>. These are the words the supplier already sees before the take (decision D9); nothing the take
    /// discloses (the property, the notes, the address) and no customer is searched (SP-D23: the customer's name is encrypted for a
    /// private customer, and the search by name is the console's, on the rows it loaded).
    /// </summary>
    /// <remarks>
    /// The booking code is a credential of the customer (with its e-mail it manages the booking, SP-11) and the supplier never sees it, so
    /// only a <b>whole</b> code matches here, never a part of it. (The global search of the console already finds a request by the start
    /// of its code; that is older than this filter and is not changed by it.)
    /// </remarks>
    private static IQueryable<SupplierRequestRow> FilterByText(IQueryable<SupplierRequestRow> rows, string text)
    {
        var term = text.ToLowerInvariant();
        var categories = CategorySearchWords.Value
            .Where(category => category.Words.Any(word => word.Contains(term, StringComparison.Ordinal)))
            .Select(category => category.Code)
            .ToArray();

        if (BookingCodes.TryNormalize(text, out var bookingCode))
        {
            return rows.Where(r =>
                (r.ServiceNameSnapshot != null && r.ServiceNameSnapshot.ToLower().Contains(term))
                || categories.Contains(r.Category)
                || r.City.ToLower().Contains(term)
                || r.PublicCode == bookingCode);
        }

        return rows.Where(r =>
            (r.ServiceNameSnapshot != null && r.ServiceNameSnapshot.ToLower().Contains(term))
            || categories.Contains(r.Category)
            || r.City.ToLower().Contains(term));
    }

    /// <summary>
    /// The words a category is found by: its code and its label in Italian and in English, lower case (the consoles show the label, not
    /// the code, so a supplier types "pulizie"). Built on the first search, not with the type (it reads the e-mail texts), and
    /// <see cref="LazyThreadSafetyMode.PublicationOnly"/> so that a failure is tried again instead of being kept until the next restart.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(string Code, string[] Words)>> CategorySearchWords = new(
        () => ServiceCategories.All
            .Select(code => (
                code,
                new[]
                {
                    code,
                    EmailTemplates.ServiceCategoryLabel(CultureInfo.GetCultureInfo("it-IT"), code).ToLowerInvariant(),
                    EmailTemplates.ServiceCategoryLabel(CultureInfo.GetCultureInfo("en"), code).ToLowerInvariant(),
                }))
            .ToArray(),
        LazyThreadSafetyMode.PublicationOnly);

    internal static IOrderedQueryable<SupplierRequestRow> Sort(IQueryable<SupplierRequestRow> rows, SupplierInboxSort sort) => sort switch
    {
        SupplierInboxSort.Urgency => rows
            .OrderBy(r => r.ResponseDueAt == null)
            .ThenBy(r => r.ResponseDueAt)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id),

        SupplierInboxSort.WorkTime => rows
            .OrderBy(r => r.WorkAt == null)
            .ThenBy(r => r.WorkAt)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id),

        _ => rows
            .OrderByDescending(r => r.ActivityAt)
            .ThenByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id),
    };

    public async Task<SupplierServiceRequestView?> GetAsync(
        Guid id,
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var row = await Rows(supplierOrgId).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null) return null;

        var ownerPhones = await OwnerPhonesAsync([row], cancellationToken);
        var summaries = await PaymentSummariesAsync(supplierOrgId, [row], cancellationToken);
        var takenByName = await TakenByNameAsync(row.TakenByUserId, supplierOrgId, cancellationToken);
        var history = ServiceRequestHistory.Build(
            new ServiceRequestMilestones(
                row.Status,
                row.CreatedAt,
                row.UpdatedAt,
                row.TakenAt,
                row.CompletedAt,
                row.PaidAt,
                row.RejectionReason,
                row.StartedAt,
                row.CancelledAt,
                row.CancellationReason,
                row.CancelledBy,
                PaidBy: row.PaidBy,
                Requester: row.RentalContext == ServiceRequestRentalContext.Showcase ? ServiceRequestActorParty.Customer : ServiceRequestActorParty.Host,
                AutoAccepted: row.AutoAccepted,
                QuoteEvents: ServiceRequestJson.ReadQuoteEvents(row.QuoteEventsJson)),
            takenByName);

        return ToView(row, ownerPhones, summaries.GetValueOrDefault(row.Id), history, _clock.GetUtcNow().UtcDateTime);
    }

    public Task<IReadOnlyList<SupplierAgendaRequest>> ListForAgendaAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        ListAsync(
            (lowerUtc, upperUtc) => AgendaRowsOf(db, supplierOrgId, lowerUtc, upperUtc),
            ToAgendaItem,
            from,
            to,
            cancellationToken);

    public Task<IReadOnlyList<SupplierAgendaRequest>> ListForCalendarAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        ListAsync(
            (lowerUtc, upperUtc) => CalendarRowsOf(db, supplierOrgId, lowerUtc, upperUtc),
            ToCalendarItem,
            from,
            to,
            cancellationToken);

    private static async Task<IReadOnlyList<SupplierAgendaRequest>> ListAsync<TRow>(
        Func<DateTime, DateTime, IQueryable<TRow>> rowsOf,
        Func<TRow, SupplierAgendaRequest> toItem,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var lowerUtc = from.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var upperUtc = to.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var rows = await rowsOf(lowerUtc, upperUtc).ToListAsync(cancellationToken);

        return rows
            .Select(toItem)
            .Where(item => item.Date >= from && item.Date <= to)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartUtc)
            .ThenBy(item => item.Id)
            .ToList();
    }

    private static SupplierAgendaRequest ToAgendaItem(AgendaRow row)
    {
        var source = SupplierRequestSources.Of(row.Source);
        return row is { StartUtc: { } start, EndUtc: { } end }
            ? new SupplierAgendaRequest(
                row.Id, RomeCalendar.DateInRome(start), row.Status, row.Category, start, end, row.ServiceListingId,
                Source: source, HasPendingProposal: row.HasPendingProposal, HasPendingQuote: row.HasPendingQuote)
            : new SupplierAgendaRequest(
                row.Id, RomeCalendar.DateInRome(row.CheckOutDate!.Value), row.Status, row.Category,
                ServiceListingId: row.ServiceListingId, Source: source, HasPendingProposal: row.HasPendingProposal,
                HasPendingQuote: row.HasPendingQuote);
    }

    private static SupplierAgendaRequest ToCalendarItem(CalendarRow row) =>
        ToAgendaItem(row.Core) with
        {
            ServiceName = NullIfBlank(row.ServiceNameSnapshot),
            ClientLabel = NullIfBlank(ClientName(
                row.RentalContext == ServiceRequestRentalContext.Showcase, row.Status, row.CustomerFullName, row.HostDisplayName, row.HostName)),
            Comune = NullIfBlank(row.City),
        };

    /// <summary>
    /// The requests of the supplier that fall in <c>[lowerUtc, upperUtc)</c>, rejected and cancelled ones left out: a request
    /// with a time by that time, one without by the check-out date of its stay. ServiceRequest has two parties and no tenant
    /// filter (TN-2 allow-list): the explicit SupplierOrgId predicate is the scope. IgnoreQueryFilters opens the stay of the host
    /// (another tenant) through the request, as <see cref="Rows"/> does. The base of <see cref="AgendaRowsOf"/> and
    /// <see cref="CalendarRowsOf"/>: what they select is the only difference.
    /// </summary>
    internal static IQueryable<ServiceRequest> AgendaRequestsOf(AppDbContext db, Guid supplierOrgId, DateTime lowerUtc, DateTime upperUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            // SP-BKP: a backup that only waits for its primary holds nothing of the agenda; the backup that accepted does, from then on (the
            // first backup that accepts closes the race and is the only one that can have the job), and frees the time if the primary takes it.
            .OnlyHoldingSupplierTime()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && r.Status != ServiceRequestStatus.Rifiutato
                        && r.Status != ServiceRequestStatus.Annullato
                        && ((r.ScheduledStartUtc != null && r.ScheduledStartUtc >= lowerUtc && r.ScheduledStartUtc < upperUtc)
                            || (r.ScheduledStartUtc == null
                                && r.RentalContext == ServiceRequestRentalContext.ShortRent
                                && r.Booking != null
                                && r.Booking.CheckOutDate >= lowerUtc
                                && r.Booking.CheckOutDate < upperUtc)));

    /// <summary>
    /// The requests of the agenda as the slot planner needs them: only the id, the status, the category, the time, the check-out
    /// date, the catalog service, where the request comes from and whether a proposed time waits, never the guest. The light read (no
    /// join on the host, the property or the customer).
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<AgendaRow> AgendaRowsOf(AppDbContext db, Guid supplierOrgId, DateTime lowerUtc, DateTime upperUtc) =>
        AgendaRequestsOf(db, supplierOrgId, lowerUtc, upperUtc)
            .Select(r => new AgendaRow(
                r.Id,
                r.Status,
                r.Category,
                r.ScheduledStartUtc,
                r.ScheduledEndUtc,
                r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null,
                r.ServiceListingId,
                r.Source,
                r.ProposedStartUtc != null && r.ProposedEndUtc != null && r.ProposedAt != null,
                r.QuoteSentAt != null));

    /// <summary>
    /// The same requests with the titles of the calendar (SP-03b): the name of the service, the org of the host, the name of the
    /// private customer (shortened to "Nome C." before the take by <see cref="ToCalendarItem"/>), the comune, the source and whether
    /// a proposed time waits. Never the street address, the property's name, a contact, a note or the guest of the stay.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<CalendarRow> CalendarRowsOf(AppDbContext db, Guid supplierOrgId, DateTime lowerUtc, DateTime upperUtc) =>
        AgendaRequestsOf(db, supplierOrgId, lowerUtc, upperUtc)
            .Select(r => new CalendarRow(
                r.Id,
                r.Status,
                r.Category,
                r.ScheduledStartUtc,
                r.ScheduledEndUtc,
                r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null,
                r.ServiceListingId,
                r.ServiceNameSnapshot,
                r.Source,
                r.RentalContext,
                r.Org.DisplayName,
                r.Org.Name,
                r.Customer != null ? r.Customer.FullName : null,
                r.Property != null ? r.Property.City : r.LocationCity ?? string.Empty,
                r.ProposedStartUtc != null && r.ProposedEndUtc != null && r.ProposedAt != null,
                r.QuoteSentAt != null));

    internal sealed record AgendaRow(
        Guid Id,
        ServiceRequestStatus Status,
        string Category,
        DateTime? StartUtc,
        DateTime? EndUtc,
        DateTime? CheckOutDate,
        Guid? ServiceListingId,
        ServiceRequestSource Source,
        bool HasPendingProposal,
        bool HasPendingQuote);

    internal sealed record CalendarRow(
        Guid Id,
        ServiceRequestStatus Status,
        string Category,
        DateTime? StartUtc,
        DateTime? EndUtc,
        DateTime? CheckOutDate,
        Guid? ServiceListingId,
        string? ServiceNameSnapshot,
        ServiceRequestSource Source,
        ServiceRequestRentalContext RentalContext,
        string? HostDisplayName,
        string? HostName,
        string? CustomerFullName,
        string City,
        bool HasPendingProposal,
        bool HasPendingQuote)
    {
        public AgendaRow Core => new(Id, Status, Category, StartUtc, EndUtc, CheckOutDate, ServiceListingId, Source, HasPendingProposal, HasPendingQuote);
    }

    private static readonly ServiceRequestStatus[] DisclosedStatuses =
    [
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
        ServiceRequestStatus.Completato,
        ServiceRequestStatus.Pagato,
    ];

    /// <summary>
    /// The requests sent to <paramref name="supplierOrgId"/>, with the columns the supplier may see. ServiceRequest has two
    /// parties and no tenant filter (TN-2 allow-list); IgnoreQueryFilters also opens the host's property, stay and org
    /// (another tenant) through the request: the explicit SupplierOrgId predicate is the scope.
    /// </summary>
    /// <remarks>
    /// A request from the public showcase (SP-10) has no property: the place is read from the request itself, the customer from
    /// the request's <c>Customer</c>. What the supplier sees only after the take (the street address, the floor and the access
    /// notes of the place, the e-mail and the phone of the customer) is selected only for the requests in a disclosed status, so
    /// the database does not even return it for the others (it is encrypted: it is not decrypted either); the name of the
    /// customer is read always and shortened to "Nome C." in <see cref="ToView"/> until the take (decision D9).
    /// </remarks>
    internal IQueryable<SupplierRequestRow> Rows(Guid supplierOrgId) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .Select(r => new SupplierRequestRow
            {
                Id = r.Id,
                RentalContext = r.RentalContext,
                Source = r.Source,
                Status = r.Status,
                Category = r.Category,
                Urgency = r.Urgency,
                Notes = r.Notes,
                RejectionReason = r.RejectionReason,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                TakenAt = r.TakenAt,
                TakenByUserId = r.TakenByUserId,
                AutoAccepted = r.AutoAccepted,
                Kind = r.Kind,
                GroupRole = r.GroupRole,
                BackupAcceptedAt = r.BackupAcceptedAt,
                GroupPhase = r.Group != null ? (ServiceRequestGroupPhase?)r.Group.Phase : null,
                GroupPrimaryDueAt = r.Group != null ? (DateTime?)r.Group.PrimaryDueAt : null,
                GroupBackupDueAt = r.Group != null ? r.Group.BackupDueAt : null,
                CompletedAt = r.CompletedAt,
                PaidAt = r.PaidAt,
                // Activity date of the inbox period and order (SupplierInboxStatusFilter): completion for completed and
                // paid requests (as the SU-11 KPIs), rejection for rejected ones and cancellation for cancelled ones (final
                // statuses), reception otherwise.
                ActivityAt = r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato
                    ? r.CompletedAt ?? r.CreatedAt
                    : r.Status == ServiceRequestStatus.Rifiutato
                        ? r.UpdatedAt
                        : r.Status == ServiceRequestStatus.Annullato
                            ? r.CancelledAt ?? r.UpdatedAt
                            : r.CreatedAt,
                WorkAt = r.ScheduledStartUtc ?? (r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null),
                ClientId = r.CustomerId ?? r.OrgId,
                ScheduledStartUtc = r.ScheduledStartUtc,
                ScheduledEndUtc = r.ScheduledEndUtc,
                ServiceListingId = r.ServiceListingId,
                ServiceNameSnapshot = r.ServiceNameSnapshot,
                EstimatedAmountCents = r.EstimatedAmountCents,
                QuotedAmountCents = r.QuotedAmountCents,
                FinalAmountCents = r.FinalAmountCents,
                FinalAmountNeedsConfirmation = r.FinalAmountNeedsConfirmation,
                PaymentMode = r.PaymentMode,
                PaidBy = r.PaidBy,
                PriceLinesJson = r.PriceLinesJson,
                ResponseDueAt = r.ResponseDueAt,
                StartedAt = r.StartedAt,
                CancelledAt = r.CancelledAt,
                CancelledBy = r.CancelledBy,
                CancellationReason = r.CancellationReason,
                CompletionNotes = r.CompletionNotes,
                WorkPhotosJson = r.WorkPhotosJson,
                ProposedStartUtc = r.ProposedStartUtc,
                ProposedEndUtc = r.ProposedEndUtc,
                ProposedAt = r.ProposedAt,
                ProposalMessage = r.ProposalMessage,
                QuoteSentAt = r.QuoteSentAt,
                QuoteLinesJson = r.QuoteLinesJson,
                QuoteTotalCents = r.QuoteTotalCents,
                QuoteNote = r.QuoteNote,
                QuoteScheduledStartUtc = r.QuoteScheduledStartUtc,
                QuoteScheduledEndUtc = r.QuoteScheduledEndUtc,
                QuoteEventsJson = r.QuoteEventsJson,
                // Only to search by it (q, SP-04e): never part of a view, the customer's credential stays out of the supplier's sight.
                PublicCode = r.PublicCode,
                PropertyId = r.PropertyId,
                PropertyName = r.Property != null ? r.Property.Name : null,
                City = r.Property != null ? r.Property.City : r.LocationCity ?? string.Empty,
                ComuneIstatCode = r.Property != null ? r.Property.ComuneIstatCode : r.LocationComuneIstat,
                PostalCode = r.Property != null ? r.Property.PostalCode : r.LocationPostalCode,
                // The street address of a showcase request is an encrypted column and the property's is not: they are two columns of
                // the projection, never the two branches of one CASE (EF would give the CASE the mapping of the first branch, and
                // the other would be read without, or with the wrong, decryption).
                Address = r.Property != null ? r.Property.Address : null,
                LocationAddress = DisclosedStatuses.Contains(r.Status) ? r.LocationAddress : null,
                Floor = DisclosedStatuses.Contains(r.Status) ? r.LocationFloor : null,
                AccessNotes = DisclosedStatuses.Contains(r.Status) ? r.LocationAccessNotes : null,
                OwnerId = r.Property != null ? r.Property.OwnerId : string.Empty,
                BookingId = r.BookingId,
                CheckInDate = r.Booking != null ? r.Booking.CheckInDate : (DateTime?)null,
                CheckOutDate = r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null,
                HostDisplayName = r.Org.DisplayName,
                HostName = r.Org.Name,
                HostEmail = r.Org.ContactEmail,
                CustomerFullName = r.Customer != null ? r.Customer.FullName : null,
                CustomerEmail = r.Customer != null && DisclosedStatuses.Contains(r.Status) ? r.Customer.Email : null,
                CustomerPhone = r.Customer != null && DisclosedStatuses.Contains(r.Status) ? r.Customer.Phone : null,
            });

    /// <summary>Phones of the owners of the properties of the requests the supplier took (none is read otherwise).</summary>
    private async Task<IReadOnlyDictionary<string, string>> OwnerPhonesAsync(
        IReadOnlyCollection<SupplierRequestRow> rows,
        CancellationToken cancellationToken)
    {
        var ownerIds = rows
            .Where(r => SupplierJobDisclosure.IsDisclosed(r.Status) && !string.IsNullOrEmpty(r.OwnerId))
            .Select(r => r.OwnerId)
            .Distinct()
            .ToArray();
        if (ownerIds.Length == 0)
            return new Dictionary<string, string>();

        var owners = await db.Users
            .AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id) && u.PhoneNumber != "")
            .Select(u => new { u.Id, u.PhoneNumber })
            .ToListAsync(cancellationToken);

        return owners.ToDictionary(u => u.Id, u => u.PhoneNumber);
    }

    private async Task<string?> TakenByNameAsync(string? userId, Guid supplierOrgId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        var member = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.SupplierOrgId == supplierOrgId)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        return member is null ? null : $"{member.FirstName} {member.LastName}".Trim();
    }

    private static SupplierJobBackup? BackupOf(SupplierRequestRow row, DateTime nowUtc)
    {
        if (row is not { GroupPhase: { } phase, GroupPrimaryDueAt: { } primaryDueAt })
            return null;

        switch (row.GroupRole)
        {
            case ServiceRequestGroupRole.Backup:
            case ServiceRequestGroupRole.PromotedBackup:
                return new SupplierJobBackup(
                    SupplierBackupRoles.Backup,
                    SupplierBackupStates.Of(row.GroupRole, row.Status, phase, primaryDueAt, nowUtc, row.BackupAcceptedAt),
                    primaryDueAt,
                    row.GroupBackupDueAt,
                    row.BackupAcceptedAt);
            case ServiceRequestGroupRole.Primary when row.Status == ServiceRequestStatus.Richiesto:
                return new SupplierJobBackup(
                    SupplierBackupRoles.Primary,
                    SupplierBackupStates.Of(row.GroupRole, row.Status, phase, primaryDueAt, nowUtc, acceptedAt: null),
                    primaryDueAt,
                    row.GroupBackupDueAt,
                    AcceptedAt: null);
            default:
                return null;
        }
    }

    private static SupplierServiceRequestView ToView(
        SupplierRequestRow row,
        IReadOnlyDictionary<string, string> ownerPhones,
        SupplierJobPayment? payment,
        IReadOnlyList<ServiceRequestHistoryEntry>? history,
        DateTime nowUtc)
    {
        var disclosed = SupplierJobDisclosure.IsDisclosed(row.Status);

        SupplierJobStay? stay = null;
        DateOnly? scheduledFor = null;
        if (row.RentalContext == ServiceRequestRentalContext.ShortRent
            && row.BookingId is { } bookingId
            && row.CheckInDate is { } checkIn
            && row.CheckOutDate is { } checkOut)
        {
            stay = new SupplierJobStay(bookingId, RomeCalendar.DateInRome(checkIn), RomeCalendar.DateInRome(checkOut));
            scheduledFor = stay.CheckOut;
        }

        if (row.ScheduledStartUtc is { } scheduledStart)
            scheduledFor = RomeCalendar.DateInRome(scheduledStart);

        var showcase = row.RentalContext == ServiceRequestRentalContext.Showcase;

        SupplierJobHostContact? hostContact = null;
        if (disclosed && showcase)
        {
            hostContact = new SupplierJobHostContact(
                NullIfBlank(row.CustomerFullName) ?? string.Empty,
                NullIfBlank(row.CustomerEmail),
                NullIfBlank(row.CustomerPhone));
        }
        else if (disclosed)
        {
            var name = HostName(row);
            hostContact = new SupplierJobHostContact(
                name,
                NullIfBlank(row.HostEmail),
                ownerPhones.TryGetValue(row.OwnerId, out var phone) ? NullIfBlank(phone) : null);
        }

        var proposal = row is { ProposedStartUtc: { } proposedStart, ProposedEndUtc: { } proposedEnd, ProposedAt: { } proposedAt }
            ? new SupplierJobProposal(proposedStart, proposedEnd, proposedAt, NullIfBlank(row.ProposalMessage))
            : null;

        var cancellation = row.Status == ServiceRequestStatus.Annullato
            ? new SupplierJobCancellation(
                row.CancelledAt ?? row.UpdatedAt,
                row.CancelledBy ?? ServiceRequestActorParty.Host,
                NullIfBlank(row.CancellationReason))
            : null;

        return new SupplierServiceRequestView(
            row.Id,
            row.RentalContext,
            row.Status,
            row.Category,
            row.Urgency,
            disclosed ? NullIfBlank(row.Notes) : null,
            NullIfBlank(row.RejectionReason),
            row.CreatedAt,
            row.UpdatedAt,
            row.TakenAt,
            row.CompletedAt,
            row.PaidAt,
            new SupplierJobLocation(
                row.PropertyId,
                disclosed ? row.PropertyName : null,
                row.City,
                NullIfBlank(row.PostalCode),
                disclosed ? NullIfBlank(row.Address ?? row.LocationAddress) : null,
                disclosed ? NullIfBlank(row.Floor) : null,
                disclosed ? NullIfBlank(row.AccessNotes) : null),
            scheduledFor,
            stay,
            disclosed,
            hostContact,
            history,
            SupplierRequestSources.Of(row.Source),
            row.ServiceListingId,
            NullIfBlank(row.ServiceNameSnapshot),
            new SupplierJobSchedule(
                row.ScheduledStartUtc,
                row.ScheduledEndUtc,
                row.Status == ServiceRequestStatus.Richiesto ? row.ResponseDueAt : null,
                row.StartedAt),
            new SupplierJobPrice(
                row.EstimatedAmountCents,
                row.QuotedAmountCents,
                row.FinalAmountCents,
                row.FinalAmountNeedsConfirmation,
                ServiceRequestJson.ReadPriceLines(row.PriceLinesJson)),
            new SupplierJobClient(
                row.ClientId,
                ClientName(showcase, row.Status, row.CustomerFullName, row.HostDisplayName, row.HostName)),
            proposal,
            cancellation,
            NullIfBlank(row.CompletionNotes),
            ServiceRequestJson.ReadPhotos(row.WorkPhotosJson),
            row.PaymentMode,
            payment,
            row.AutoAccepted,
            row.Kind,
            QuoteOf(row),
            BackupOf(row, nowUtc));
    }

    private static ServiceRequestQuote? QuoteOf(SupplierRequestRow row) =>
        ServiceRequestQuoteRules.IsPending(row.Status, row.QuoteSentAt) && row is { QuoteSentAt: { } sentAt, QuoteTotalCents: { } total }
            ? new ServiceRequestQuote(
                sentAt,
                ServiceRequestJson.ReadQuoteLines(row.QuoteLinesJson),
                total,
                NullIfBlank(row.QuoteNote),
                row.QuoteScheduledStartUtc,
                row.QuoteScheduledEndUtc,
                row.RentalContext == ServiceRequestRentalContext.Showcase ? row.ResponseDueAt : null)
            : null;

    private static string ClientName(
        bool showcase,
        ServiceRequestStatus status,
        string? customerFullName,
        string? hostDisplayName,
        string? hostName) =>
        showcase ? SupplierJobDisclosure.CustomerName(status, customerFullName) : HostName(hostDisplayName, hostName);

    private static string HostName(SupplierRequestRow row) => HostName(row.HostDisplayName, row.HostName);

    private static string HostName(string? displayName, string? name) =>
        NullIfBlank(displayName) ?? NullIfBlank(name) ?? string.Empty;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed class SupplierRequestRow
    {
        public Guid Id { get; init; }
        public ServiceRequestRentalContext RentalContext { get; init; }
        public ServiceRequestSource Source { get; init; }
        public ServiceRequestStatus Status { get; init; }
        public string Category { get; init; } = string.Empty;
        public ServiceRequestUrgency Urgency { get; init; }
        public string? Notes { get; init; }
        public string? RejectionReason { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime? TakenAt { get; init; }
        public string? TakenByUserId { get; init; }
        public bool AutoAccepted { get; init; }
        public ServiceRequestKind Kind { get; init; }
        public ServiceRequestGroupRole GroupRole { get; init; }
        public DateTime? BackupAcceptedAt { get; init; }
        public ServiceRequestGroupPhase? GroupPhase { get; init; }
        public DateTime? GroupPrimaryDueAt { get; init; }
        public DateTime? GroupBackupDueAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public DateTime? PaidAt { get; init; }
        public DateTime ActivityAt { get; init; }
        public DateTime? WorkAt { get; init; }
        public Guid ClientId { get; init; }
        public DateTime? ScheduledStartUtc { get; init; }
        public DateTime? ScheduledEndUtc { get; init; }
        public Guid? ServiceListingId { get; init; }
        public string? ServiceNameSnapshot { get; init; }
        public int? EstimatedAmountCents { get; init; }
        public int? QuotedAmountCents { get; init; }
        public int? FinalAmountCents { get; init; }
        public bool FinalAmountNeedsConfirmation { get; init; }
        public ServiceRequestPaymentMode PaymentMode { get; init; }
        public ServiceRequestActorParty? PaidBy { get; init; }
        public string PriceLinesJson { get; init; } = "[]";
        public DateTime? ResponseDueAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CancelledAt { get; init; }
        public ServiceRequestActorParty? CancelledBy { get; init; }
        public string? CancellationReason { get; init; }
        public string? CompletionNotes { get; init; }
        public string WorkPhotosJson { get; init; } = "[]";
        public DateTime? ProposedStartUtc { get; init; }
        public DateTime? ProposedEndUtc { get; init; }
        public DateTime? ProposedAt { get; init; }
        public string? ProposalMessage { get; init; }
        public DateTime? QuoteSentAt { get; init; }
        public string QuoteLinesJson { get; init; } = "[]";
        public int? QuoteTotalCents { get; init; }
        public string? QuoteNote { get; init; }
        public DateTime? QuoteScheduledStartUtc { get; init; }
        public DateTime? QuoteScheduledEndUtc { get; init; }
        public string QuoteEventsJson { get; init; } = "[]";
        public string? PublicCode { get; init; }
        public Guid? PropertyId { get; init; }
        public string? PropertyName { get; init; }
        public string City { get; init; } = string.Empty;
        public string? ComuneIstatCode { get; init; }
        public string? PostalCode { get; init; }
        public string? Address { get; init; }
        public string? LocationAddress { get; init; }
        public string? Floor { get; init; }
        public string? AccessNotes { get; init; }
        public string OwnerId { get; init; } = string.Empty;
        public Guid? BookingId { get; init; }
        public DateTime? CheckInDate { get; init; }
        public DateTime? CheckOutDate { get; init; }
        public string? HostDisplayName { get; init; }
        public string? HostName { get; init; }
        public string? HostEmail { get; init; }

        public string? CustomerFullName { get; init; }
        public string? CustomerEmail { get; init; }
        public string? CustomerPhone { get; init; }
    }
}
