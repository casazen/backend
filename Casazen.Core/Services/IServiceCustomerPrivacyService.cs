namespace Casazen.Core.Services;

/// <summary>
/// Personal data of the private customers of the suppliers (SP-10; runbook <c>docs/runbooks/gdpr.md</c>). The customers and the
/// place of their requests are anonymized by the nightly retention job (<c>GdprDataRetentionJob</c>) only when
/// <c>Gdpr:Retention:SupplierCustomers</c> has a period <b>and its source</b>: no period is invented, so until the product owner
/// and legal give one nothing is anonymized by retention (and every run says so in the log). Idempotent: an anonymized customer
/// or request carries a marker and is never processed again.
/// </summary>
/// <remarks>
/// <para><b>The reference date</b> is the last thing that happened to the customer: the most recent end of a request of the
/// customer (its scheduled end, else its creation), or, for a customer with no request, its creation. A customer with a request
/// still open (<c>Richiesto</c>, <c>PresoInCarico</c>, <c>InCorso</c>) is never anonymized.</para>
/// <para><b>What is removed</b>: name, e-mail, phone, consent address of the customer and the e-mail index (so the same address
/// can never match the row again); street address, floor and access notes of its requests. <b>What stays</b>: the comune and the
/// postal code of the work, the dates, the amounts, the service and the code of every request (the supplier's own accounts and
/// statistics), and the consent version and date.</para>
/// <para>A system job: no tenant, the queries span every supplier on purpose.</para>
/// </remarks>
public interface IServiceCustomerPrivacyService
{
    /// <summary>One nightly run over every supplier.</summary>
    Task<ServiceCustomerRetentionRun> ApplyRetentionAsync(CancellationToken cancellationToken = default);
}

/// <param name="RetentionConfigured">Whether <c>Gdpr:Retention:SupplierCustomers</c> has a period and its source.</param>
/// <param name="Customers">Customers anonymized by this run.</param>
/// <param name="Requests">Requests whose place was anonymized by this run.</param>
public sealed record ServiceCustomerRetentionRun(bool RetentionConfigured, int Customers, int Requests);
