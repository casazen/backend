using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// When a customer of a supplier is a "regular" one (decision SP-D6): at least <see cref="MinJobs"/> jobs taken. One place for the threshold, so the
/// "Clienti" page (SP-16a) and the automatic acceptance of the regular hosts (SP-04d) cannot disagree.
/// </summary>
public static class SupplierRegulars
{
    /// <summary>Jobs the supplier took (and not cancelled afterwards) from the same customer.</summary>
    public const int MinJobs = 3;

    /// <summary>True when <paramref name="jobsTaken"/> makes a customer a regular one.</summary>
    public static bool IsRegular(int jobsTaken) => jobsTaken >= MinJobs;
}

/// <summary>The chips of the clients page (<c>filter</c> of <c>GET api/supplier/clients</c>).</summary>
public enum SupplierClientFilter
{
    /// <summary><c>tutti</c>.</summary>
    All,

    /// <summary><c>host</c>: the hosts of CasaZen.</summary>
    Host,

    /// <summary><c>privati</c>: the private customers of the public showcase.</summary>
    Private,

    /// <summary><c>abituali</c>: at least <see cref="SupplierRegulars.MinJobs"/> jobs taken.</summary>
    Regular,

    /// <summary><c>nuovi</c>: no job taken yet (a request is waiting for the answer).</summary>
    New,
}

/// <summary>The <c>filter</c> values of the clients page.</summary>
public static class SupplierClientFilters
{
    public const string AllValue = "tutti";
    public const string HostValue = "host";
    public const string PrivateValue = "privati";
    public const string RegularValue = "abituali";
    public const string NewValue = "nuovi";

    /// <summary>The filter named <paramref name="value"/> (any case); a missing value is <c>tutti</c>. False for any other value, including numbers.</summary>
    public static bool TryParse(string? value, out SupplierClientFilter filter)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or AllValue:
                filter = SupplierClientFilter.All;
                return true;
            case HostValue:
                filter = SupplierClientFilter.Host;
                return true;
            case PrivateValue:
                filter = SupplierClientFilter.Private;
                return true;
            case RegularValue:
                filter = SupplierClientFilter.Regular;
                return true;
            case NewValue:
                filter = SupplierClientFilter.New;
                return true;
            default:
                filter = default;
                return false;
        }
    }
}

/// <summary>The bounds of the clients page (SP-16a).</summary>
public static class SupplierClientLimits
{
    /// <summary>Largest page of the list: the console searches the name of a client on the rows it loaded (decision SP-D23), so the page is the search scope.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The jobs of a client shown in its detail.</summary>
    public const int RecentJobs = 6;
}

/// <summary>
/// How to reach a customer, given to the supplier <b>only after it took a job of that customer</b> (decision D9): the host org's name and contact
/// address, or the private customer's name, e-mail and phone. Never exported (decision SP-D25).
/// </summary>
public sealed record SupplierClientContact(string? Name, string? Email, string? Phone);

/// <summary>
/// One customer of the supplier with what they did together (SP-16a). A customer is anyone who has a request that is open or taken (a request
/// refused or cancelled counts for nothing: it makes nobody a customer).
/// </summary>
/// <param name="Id">The customer id the requests carry (<c>clientId</c> of the inbox): the private customer of the showcase, or the host org.</param>
/// <param name="IsPrivate">A private customer of the public showcase; false for a host of CasaZen.</param>
/// <param name="Label">
/// The name as the supplier may see it (decision D9): the host org's name; for a private customer "Nome C." until a job of theirs is taken, then the
/// full name. Null when the customer was removed by the retention of the personal data.
/// </param>
/// <param name="Anonymized">The private customer was removed: the console says "Cliente rimosso".</param>
/// <param name="JobsAccepted">Requests the supplier took and that are still jobs (taken, in progress, completed or paid).</param>
/// <param name="JobsCompleted">Of those, completed or paid.</param>
/// <param name="LastJobAt">The latest job already begun (in progress, completed or paid); null when none.</param>
/// <param name="NextJobAt">The first request still ahead (waiting for the answer, or taken and not begun); null when none.</param>
/// <param name="GrossPaidCents">The total of the jobs marked paid, in cents (the total the supplier declared, which is what the payer paid; a refund does not reduce it).</param>
/// <param name="Regular">At least <see cref="SupplierRegulars.MinJobs"/> jobs taken.</param>
/// <param name="Contact">Set only once a job of this customer was taken (<c>JobsAccepted</c> above 0).</param>
public sealed record SupplierClientSummary(
    Guid Id,
    bool IsPrivate,
    string? Label,
    bool Anonymized,
    int JobsAccepted,
    int JobsCompleted,
    DateTime? LastJobAt,
    DateTime? NextJobAt,
    long GrossPaidCents,
    bool Regular,
    SupplierClientContact? Contact);

/// <summary>One of the latest requests of a customer, in the detail of the clients page.</summary>
/// <param name="RequestId">The request (the same id as <c>GET api/supplier/inbox/{id}</c>).</param>
/// <param name="ServiceName">The name of the service when the request was made; null for a request by category only.</param>
/// <param name="Category">The category code.</param>
/// <param name="Status">Where the request is.</param>
/// <param name="Phase">The phase of the console (<see cref="SupplierRequestPhases"/>, an open set).</param>
/// <param name="ScheduledStart">When the work is (UTC).</param>
/// <param name="CompletedAt">When it was completed.</param>
/// <param name="AmountCents">The price to show: the final total, else the quote, else the estimate; null is "da concordare".</param>
public sealed record SupplierClientJob(
    Guid RequestId,
    string? ServiceName,
    string Category,
    ServiceRequestStatus Status,
    string Phase,
    DateTime? ScheduledStart,
    DateTime? CompletedAt,
    int? AmountCents);

/// <summary>A customer with its latest requests.</summary>
public sealed record SupplierClientDetail(SupplierClientSummary Client, IReadOnlyList<SupplierClientJob> RecentJobs);

/// <summary>A page of the clients list and how many customers the filter selects.</summary>
public sealed record SupplierClientsPage(IReadOnlyList<SupplierClientSummary> Items, int Total);

/// <summary>The error of the clients endpoints (SP-16a): a customer that is not the caller's answers like one that does not exist.</summary>
public static class SupplierClientErrors
{
    /// <summary>404: no customer with that id for the caller (another supplier's, or nobody's, answers the same).</summary>
    public const string NotFound = "supplier_client_not_found";

    /// <summary>The key of <see cref="NotFound"/>.</summary>
    public const string NotFoundMessageKey = "SupplierClientNotFound";

    /// <summary>400 <c>validation_error</c>: the <c>filter</c> is not one of <see cref="SupplierClientFilters"/>.</summary>
    public const string FilterInvalidMessageKey = "SupplierClientsFilterInvalid";

    /// <summary>Every <c>SharedResources</c> key of the clients (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } = [NotFoundMessageKey, FilterInvalidMessageKey];

    /// <summary>The 404 of a customer that is not the caller's.</summary>
    public static NotFoundException Missing(Guid id) =>
        new($"Supplier client {id} not found")
        {
            Code = NotFound,
            MessageKey = NotFoundMessageKey,
        };
}
