using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Supplier console reads of the service requests (SU-08, A4-14). The rows are projected column by column: the guest of
/// the stay is never joined nor loaded, so no guest data can reach the supplier; street address and host contact are
/// filled only for a request the supplier took (<see cref="SupplierJobDisclosure"/>).
/// </summary>
public class SupplierServiceRequestReader(AppDbContext db) : ISupplierServiceRequestReader
{
    public async Task<(IReadOnlyList<SupplierServiceRequestView> Items, int Total)> ListAsync(
        Guid supplierOrgId,
        SupplierInboxQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = Rows(supplierOrgId);

        if (query.Statuses.Count > 0)
        {
            var statuses = query.Statuses.ToArray();
            rows = rows.Where(r => statuses.Contains(r.Status));
        }

        // The period is made of Europe/Rome calendar days: [00:00 Rome of From, 00:00 Rome of the day after To).
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

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(r => r.ActivityAt)
            .ThenByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var ownerPhones = await OwnerPhonesAsync(page, cancellationToken);
        return (page.Select(r => ToView(r, ownerPhones, history: null)).ToList(), total);
    }

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
                row.Status, row.CreatedAt, row.UpdatedAt, row.TakenAt, row.CompletedAt, row.PaidAt, row.RejectionReason),
            takenByName);

        return ToView(row, ownerPhones, history);
    }

    /// <summary>
    /// The requests sent to <paramref name="supplierOrgId"/>, with the columns the supplier may see. ServiceRequest has two
    /// parties and no tenant filter (TN-2 allow-list); IgnoreQueryFilters also opens the host's property, stay and org
    /// (another tenant) through the request: the explicit SupplierOrgId predicate is the scope.
    /// </summary>
    private IQueryable<SupplierRequestRow> Rows(Guid supplierOrgId) =>
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
                // paid requests (as the SU-11 KPIs), rejection for rejected ones (final status), reception otherwise.
                ActivityAt = r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato
                    ? r.CompletedAt ?? r.CreatedAt
                    : r.Status == ServiceRequestStatus.Rifiutato
                        ? r.UpdatedAt
                        : r.CreatedAt,
                PropertyId = r.PropertyId,
                PropertyName = r.Property.Name,
                City = r.Property.City,
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
            // Short-rent: the job is the turnover of the stay, on its check-out day. The request has no date of its own.
            scheduledFor = stay.CheckOut;
        }

        SupplierJobHostContact? hostContact = null;
        if (disclosed)
        {
            var name = NullIfBlank(row.HostDisplayName) ?? NullIfBlank(row.HostName) ?? string.Empty;
            hostContact = new SupplierJobHostContact(
                name,
                NullIfBlank(row.HostEmail),
                ownerPhones.TryGetValue(row.OwnerId, out var phone) ? NullIfBlank(phone) : null);
        }

        return new SupplierServiceRequestView(
            row.Id,
            row.RentalContext,
            row.Status,
            row.Category,
            row.Urgency,
            NullIfBlank(row.Notes),
            NullIfBlank(row.RejectionReason),
            row.CreatedAt,
            row.UpdatedAt,
            row.TakenAt,
            row.CompletedAt,
            row.PaidAt,
            new SupplierJobLocation(
                row.PropertyId,
                row.PropertyName,
                row.City,
                NullIfBlank(row.PostalCode),
                disclosed ? NullIfBlank(row.Address) : null),
            scheduledFor,
            stay,
            disclosed,
            hostContact,
            history);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The columns of a request the supplier side reads (never the guest of the stay).</summary>
    private sealed class SupplierRequestRow
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
        public Guid PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
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
