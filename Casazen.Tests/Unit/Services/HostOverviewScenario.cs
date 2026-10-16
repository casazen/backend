using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The data the reads of SR-03b (the Home overview, the host's day, the booking list with its chips and counters, the pages of
/// payments) are proved on, shared by the PostgreSQL tests and their InMemory reference: <see cref="HostScopeScenario"/> and, for
/// each property of it, the stays around 9 October 2026 (today in Rome) with their payments, online check-in links, Alloggiati
/// reports and interventions. A property of the other org has them too: nothing of it may ever be seen.
/// <code>
///   guest (first name)  status      source      stay                  total  paid          link          report   chip
///   arrives             Confirmed   Airbnb      9-12 Oct (11-14)      500    200 (partial) sent          -        in corso (in arrivo from the 11th)
///   inhouse             CheckedIn   Manual      7-10 Oct              400    400 (paid)    completed     sent     in corso
///   leaves              CheckedIn   Direct      6-9 Oct               350    refunded      email failed  error    in corso
///   later               Confirmed   BookingCom  12-15 Oct             300    -             in progress   -        in arrivo
///   over                CheckedOut  Local       20-23 Sep             200    200 (paid)    expired       -        concluse
///   cancelled           Cancelled   Expedia     20-22 Oct             250    -             -             -        cancellate
/// </code>
/// and, of the interventions: one scheduled for today, a cleaning of the stay that leaves today, a time proposed by the supplier, a
/// final amount to confirm and a finished job to pay. The arrival of today is the first property's alone (the second one's is two
/// days later), so the soonest arrivals of the org are never tied at the edge of the list of three.
/// </summary>
internal static class HostOverviewScenario
{
    public static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static DateTime Day(int month, int day) => HostScopeScenario.Day(2026, month, day);

    private static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>The stays of every property of the world: the two of the org and the one of the other org.</summary>
    public static async Task AddAsync(AppDbContext db, HostScopeWorld world)
    {
        AddTo(db, world.Granted, world.SupplierOrgId, arrivalDay: 9);
        AddTo(db, world.Hidden, world.SupplierOrgId, arrivalDay: 11);
        AddTo(db, world.OtherOrgProperty, world.SupplierOrgId, arrivalDay: 9);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>The stays, payments, links, reports and interventions of one property (not saved).</summary>
    public static void AddTo(AppDbContext db, Property property, Guid supplierOrgId, int arrivalDay = 9)
    {
        var future = At(10, 15, 8);
        var past = At(10, 8, 8);

        var arrives = Stay(db, property, "arrives", BookingStatus.Confirmed, BookingSource.Airbnb, Day(10, arrivalDay), Day(10, arrivalDay + 3), 500m, 1);
        var inHouse = Stay(db, property, "inhouse", BookingStatus.CheckedIn, BookingSource.Manual, Day(10, 7), Day(10, 10), 400m, 2);
        var leaves = Stay(db, property, "leaves", BookingStatus.CheckedIn, BookingSource.Direct, Day(10, 6), Day(10, 9), 350m, 3);
        var later = Stay(db, property, "later", BookingStatus.Confirmed, BookingSource.BookingCom, Day(10, 12), Day(10, 15), 300m, 4);
        var over = Stay(db, property, "over", BookingStatus.CheckedOut, BookingSource.Local, Day(9, 20), Day(9, 23), 200m, 5);
        Stay(db, property, "cancelled", BookingStatus.Cancelled, BookingSource.Expedia, Day(10, 20), Day(10, 22), 250m, 6);

        db.Payments.AddRange(
            Pay(arrives, PaymentStatus.Completed, 200m, At(10, 5, 10)),
            Pay(inHouse, PaymentStatus.Completed, 400m, At(10, 6, 10)),
            Pay(leaves, PaymentStatus.Refunded, 350m, At(10, 4, 10), refunded: 350m),
            Pay(over, PaymentStatus.Completed, 200m, At(9, 18, 10)));

        db.GuestCheckInSessions.AddRange(
            Session(arrives, GuestCheckInSessionStatus.Inviato, future, At(10, 6, 8)),
            Session(later, GuestCheckInSessionStatus.InCompilazione, future, At(10, 6, 8)),
            Session(inHouse, GuestCheckInSessionStatus.Completo, future, At(10, 5, 8)),
            Session(over, GuestCheckInSessionStatus.Scaduto, past, At(9, 15, 8)),
            Session(leaves, GuestCheckInSessionStatus.Inviato, future, At(10, 6, 8), GuestCheckInLinkEmailStatus.Failed));

        db.AlloggiatiWebReports.AddRange(
            new AlloggiatiWebReport
            {
                BookingId = inHouse.Id,
                OrgId = property.OrgId,
                GuestId = inHouse.GuestId,
                Status = AlloggiatiWebStatus.Inviato,
                ConfirmationNumber = "RICEVUTA-1",
                ReportedAt = At(10, 8, 7),
            },
            new AlloggiatiWebReport
            {
                BookingId = leaves.Id,
                OrgId = property.OrgId,
                GuestId = leaves.GuestId,
                Status = AlloggiatiWebStatus.Errore,
                ErrorMessage = "Questura: dati non validi",
            });

        db.ServiceRequests.AddRange(
            Request(property, supplierOrgId, ServiceRequestStatus.PresoInCarico, start: At(10, 9, 8)),
            Request(property, supplierOrgId, ServiceRequestStatus.Richiesto, bookingId: leaves.Id),
            Request(property, supplierOrgId, ServiceRequestStatus.Richiesto, proposal: true),
            Request(property, supplierOrgId, ServiceRequestStatus.Completato, amountToConfirm: true),
            Request(property, supplierOrgId, ServiceRequestStatus.Completato));
    }

    private static Booking Stay(
        AppDbContext db,
        Property property,
        string key,
        BookingStatus status,
        BookingSource source,
        DateTime checkIn,
        DateTime checkOut,
        decimal total,
        int createdMinute)
    {
        // The guest is named after the stay and its last name is the property's: a row is told apart by who it is, in both worlds.
        var guest = new Guest { OrgId = property.OrgId, FirstName = key, LastName = property.Name, Email = $"{key}-{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            Source = source,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            BasePrice = total,
            TotalPrice = total,
            CreatedAt = At(10, 1, 8, createdMinute),
            UpdatedAt = At(10, 1, 8, createdMinute),
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        return booking;
    }

    private static Payment Pay(Booking booking, PaymentStatus status, decimal amount, DateTime processedAt, decimal refunded = 0m) => new()
    {
        OrgId = booking.OrgId,
        BookingId = booking.Id,
        Status = status,
        Amount = amount,
        RefundedAmount = refunded,
        Method = PaymentMethod.CreditCard,
        TransactionId = $"tx-{Guid.NewGuid():N}",
        ProcessedAt = processedAt,
        CreatedAt = processedAt,
        UpdatedAt = processedAt,
    };

    private static GuestCheckInSession Session(
        Booking booking,
        GuestCheckInSessionStatus status,
        DateTime expiresAt,
        DateTime createdAt,
        GuestCheckInLinkEmailStatus? email = GuestCheckInLinkEmailStatus.Sent)
    {
        return new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = status,
            ExpiresAt = expiresAt,
            LinkEmailStatus = email,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    private static ServiceRequest Request(
        Property property,
        Guid supplierOrgId,
        ServiceRequestStatus status,
        DateTime? start = null,
        Guid? bookingId = null,
        bool proposal = false,
        bool amountToConfirm = false)
    {
        var request = new ServiceRequest
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            BookingId = bookingId,
            SupplierOrgId = supplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = ServiceCategories.Cleaning,
            Status = status,
            ScheduledStartUtc = start,
            ScheduledEndUtc = start?.AddHours(2),
            FinalAmountNeedsConfirmation = amountToConfirm,
            CreatedAt = At(10, 2, 8),
            UpdatedAt = At(10, 2, 8),
        };
        if (proposal)
        {
            // A proposal has its start, end and instant together (CK_ServiceRequests_ProposedInterval).
            request.ProposedStartUtc = At(10, 12, 8);
            request.ProposedEndUtc = At(10, 12, 10);
            request.ProposedAt = At(10, 8, 9);
        }

        return request;
    }
}
