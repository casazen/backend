using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Supplier console reads of the service requests (SU-08, A4-14; SP-04). The rows are projected column by column: the guest of
/// the stay is never joined nor loaded, so no guest data can reach the supplier; the name of the property, the host's notes,
/// the street address and the host contact are filled only for a request the supplier took
/// (<see cref="SupplierJobDisclosure"/>, decision D9).
/// </summary>
public class SupplierServiceRequestReader(AppDbContext db, TimeProvider? timeProvider = null) : ISupplierServiceRequestReader
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
        return (page.Select(r => ToView(r, ownerPhones, history: null)).ToList(), total);
    }

    /// <summary>
    /// The statuses, period and filters of the console applied to the rows of one supplier. The period is made of Europe/Rome
    /// calendar days, <c>[00:00 Rome of From, 00:00 Rome of the day after To)</c>, and applies to the activity date of each
    /// request; <c>When</c> applies to the day of the job.
    /// </summary>
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
            // A service of the catalog by its id, or a category by its code: the console offers both in one list.
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

        if (query.When is { } when)
        {
            // The day of the job: the scheduled time, else the check-out day of the stay (stored as midnight UTC of its date,
            // which falls inside the Europe/Rome day it names). A request with neither has no day, so it never matches.
            var (firstDay, lastDay) = SupplierInboxWhens.RangeOf(when, _clock.TodayInRomeAsDateOnly());
            var startUtc = RomeCalendar.StartOfDayUtc(firstDay);
            var endUtc = RomeCalendar.StartOfDayUtc(lastDay.AddDays(1));
            rows = rows.Where(r => r.WorkAt >= startUtc && r.WorkAt < endUtc);
        }

        return rows;
    }

    internal static IOrderedQueryable<SupplierRequestRow> Sort(IQueryable<SupplierRequestRow> rows, SupplierInboxSort sort) => sort switch
    {
        // The request to answer first: the earliest deadline; the ones with none (older than SP-04) after it.
        SupplierInboxSort.Urgency => rows
            .OrderBy(r => r.ResponseDueAt == null)
            .ThenBy(r => r.ResponseDueAt)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id),

        // The next job: the earliest day and time; the ones with no day (to agree) after it.
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
                row.PaidBy),
            takenByName);

        return ToView(row, ownerPhones, history);
    }

    public async Task<IReadOnlyList<SupplierAgendaRequest>> ListForAgendaAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        // The day of a request with a time is the Europe/Rome day of its start; the day of one without is the check-out day of
        // the stay (stored as midnight UTC of its date, but RomeCalendar.DateInRome is what reads it back). Ask the database for
        // a day more on each side and keep the exact Europe/Rome days below.
        var lowerUtc = from.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var upperUtc = to.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var rows = await AgendaRowsOf(db, supplierOrgId, lowerUtc, upperUtc).ToListAsync(cancellationToken);

        return rows
            .Select(ToAgendaItem)
            .Where(item => item.Date >= from && item.Date <= to)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartUtc)
            .ThenBy(item => item.Id)
            .ToList();
    }

    private static SupplierAgendaRequest ToAgendaItem(AgendaRow row) =>
        row is { StartUtc: { } start, EndUtc: { } end }
            ? new SupplierAgendaRequest(row.Id, RomeCalendar.DateInRome(start), row.Status, row.Category, start, end)
            : new SupplierAgendaRequest(row.Id, RomeCalendar.DateInRome(row.CheckOutDate!.Value), row.Status, row.Category);

    /// <summary>
    /// The requests of the supplier that fall in <c>[lowerUtc, upperUtc)</c>, rejected and cancelled ones left out: a request
    /// with a time by that time, one without by the check-out date of its stay. Only the id, the status, the category, the
    /// time and the check-out date, never the guest. ServiceRequest has two parties and no tenant filter (TN-2 allow-list):
    /// the explicit SupplierOrgId predicate is the scope. IgnoreQueryFilters opens the stay of the host (another tenant)
    /// through the request, as <see cref="Rows"/> does.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<AgendaRow> AgendaRowsOf(AppDbContext db, Guid supplierOrgId, DateTime lowerUtc, DateTime upperUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && r.Status != ServiceRequestStatus.Rifiutato
                        && r.Status != ServiceRequestStatus.Annullato
                        && ((r.ScheduledStartUtc != null && r.ScheduledStartUtc >= lowerUtc && r.ScheduledStartUtc < upperUtc)
                            || (r.ScheduledStartUtc == null
                                && r.RentalContext == ServiceRequestRentalContext.ShortRent
                                && r.Booking != null
                                && r.Booking.CheckOutDate >= lowerUtc
                                && r.Booking.CheckOutDate < upperUtc)))
            .Select(r => new AgendaRow(
                r.Id,
                r.Status,
                r.Category,
                r.ScheduledStartUtc,
                r.ScheduledEndUtc,
                r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null));

    /// <summary>A request of the agenda as read from the database.</summary>
    internal sealed record AgendaRow(
        Guid Id,
        ServiceRequestStatus Status,
        string Category,
        DateTime? StartUtc,
        DateTime? EndUtc,
        DateTime? CheckOutDate);

    /// <summary>
    /// The requests sent to <paramref name="supplierOrgId"/>, with the columns the supplier may see. ServiceRequest has two
    /// parties and no tenant filter (TN-2 allow-list); IgnoreQueryFilters also opens the host's property, stay and org
    /// (another tenant) through the request: the explicit SupplierOrgId predicate is the scope.
    /// </summary>
    internal IQueryable<SupplierRequestRow> Rows(Guid supplierOrgId) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .Select(r => new SupplierRequestRow
            {
                Id = r.Id,
                RentalContext = r.RentalContext,
                Status = r.Status,
                Category = r.Category,
                Urgency = r.Urgency,
                Notes = r.Notes,
                RejectionReason = r.RejectionReason,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                TakenAt = r.TakenAt,
                TakenByUserId = r.TakenByUserId,
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
                // The day of the job: its scheduled time, else the check-out of its stay.
                WorkAt = r.ScheduledStartUtc ?? (r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null),
                ClientId = r.OrgId,
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
                PropertyId = r.PropertyId,
                PropertyName = r.Property.Name,
                City = r.Property.City,
                ComuneIstatCode = r.Property.ComuneIstatCode,
                PostalCode = r.Property.PostalCode,
                Address = r.Property.Address,
                OwnerId = r.Property.OwnerId,
                BookingId = r.BookingId,
                CheckInDate = r.Booking != null ? r.Booking.CheckInDate : (DateTime?)null,
                CheckOutDate = r.Booking != null ? r.Booking.CheckOutDate : (DateTime?)null,
                HostDisplayName = r.Org.DisplayName,
                HostName = r.Org.Name,
                HostEmail = r.Org.ContactEmail,
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

        // Users has no tenant filter: the ids come from properties of requests sent to this supplier.
        var owners = await db.Users
            .AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id) && u.PhoneNumber != "")
            .Select(u => new { u.Id, u.PhoneNumber })
            .ToListAsync(cancellationToken);

        return owners.ToDictionary(u => u.Id, u => u.PhoneNumber);
    }

    /// <summary>
    /// The name of the supplier member who took the request: only a user of the same supplier org is named, so the
    /// supplier never reads the name of someone outside its own team.
    /// </summary>
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

    private static SupplierServiceRequestView ToView(
        SupplierRequestRow row,
        IReadOnlyDictionary<string, string> ownerPhones,
        IReadOnlyList<ServiceRequestHistoryEntry>? history)
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
            // Short-rent without a time of its own: the job is the turnover of the stay, on its check-out day.
            scheduledFor = stay.CheckOut;
        }

        // A request with a time has a day of its own, which is the day of that time (Europe/Rome).
        if (row.ScheduledStartUtc is { } scheduledStart)
            scheduledFor = RomeCalendar.DateInRome(scheduledStart);

        SupplierJobHostContact? hostContact = null;
        if (disclosed)
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
            // Decision D9: the host's notes and the name of the property come with the take, like the address and the contact.
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
                disclosed ? NullIfBlank(row.Address) : null),
            scheduledFor,
            stay,
            disclosed,
            hostContact,
            history,
            SupplierRequestSources.CasaZen,
            row.ServiceListingId,
            NullIfBlank(row.ServiceNameSnapshot),
            new SupplierJobSchedule(
                row.ScheduledStartUtc,
                row.ScheduledEndUtc,
                // The deadline matters while the supplier has not answered.
                row.Status == ServiceRequestStatus.Richiesto ? row.ResponseDueAt : null,
                row.StartedAt),
            new SupplierJobPrice(
                row.EstimatedAmountCents,
                row.QuotedAmountCents,
                row.FinalAmountCents,
                row.FinalAmountNeedsConfirmation,
                ServiceRequestJson.ReadPriceLines(row.PriceLinesJson)),
            // Decision D9: for a host's request the customer is the host org, whose name is shown before the take.
            new SupplierJobClient(row.ClientId, HostName(row)),
            proposal,
            cancellation,
            NullIfBlank(row.CompletionNotes),
            ServiceRequestJson.ReadPhotos(row.WorkPhotosJson),
            row.PaymentMode);
    }

    private static string HostName(SupplierRequestRow row) =>
        NullIfBlank(row.HostDisplayName) ?? NullIfBlank(row.HostName) ?? string.Empty;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The columns of a request the supplier side reads (never the guest of the stay).</summary>
    internal sealed class SupplierRequestRow
    {
        public Guid Id { get; init; }
        public ServiceRequestRentalContext RentalContext { get; init; }
        public ServiceRequestStatus Status { get; init; }
        public string Category { get; init; } = string.Empty;
        public ServiceRequestUrgency Urgency { get; init; }
        public string? Notes { get; init; }
        public string? RejectionReason { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime? TakenAt { get; init; }
        public string? TakenByUserId { get; init; }
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
        public Guid PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string? ComuneIstatCode { get; init; }
        public string? PostalCode { get; init; }
        public string? Address { get; init; }
        public string OwnerId { get; init; } = string.Empty;
        public Guid? BookingId { get; init; }
        public DateTime? CheckInDate { get; init; }
        public DateTime? CheckOutDate { get; init; }
        public string? HostDisplayName { get; init; }
        public string? HostName { get; init; }
        public string? HostEmail { get; init; }
    }
}
