using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// A private customer of a supplier, born when the customer checks the e-mail of a booking made from the supplier's public
/// showcase (SP-10). One row per supplier and e-mail address: the same person booking the same supplier twice is one customer
/// (the base of "Clienti" and of the regular customers), booking two suppliers is two customers, because the data belongs to
/// the supplier it was given to.
/// </summary>
/// <remarks>
/// <para><b>Personal data, encrypted.</b> <see cref="FullName"/>, <see cref="Email"/> and <see cref="Phone"/> are encrypted at rest
/// (<c>EncryptedColumns</c>, purpose <c>Casazen.ServiceCustomer</c>): the code reads and writes the clear value through EF, the
/// database only holds the payload. Nothing here is searched: the address is compared with <see cref="EmailHash"/>, an HMAC-SHA256
/// of the normalized address keyed by <c>Suppliers:CustomerIndexKey</c> (a key that is not in the database), and, to be sure of
/// a customer, with the decrypted address in constant time. It differs on purpose from the guests of the hosts, whose name,
/// e-mail and phone are in clear for the guest list search (<c>docs/runbooks/encryption.md</c>).</para>
/// <para><b>Keyed by the supplier org</b>, not tenant-filtered, like the other tables of the supplier side (a supplier-only account
/// has no <c>User.OrgId</c>): every statement carries the explicit <see cref="OrgId"/> predicate
/// (<c>ShowcaseBookingTenancyTests</c>). A customer is never visible to a host.</para>
/// <para><b>Retention.</b> <c>Gdpr:Retention:SupplierCustomers</c> (off until the product owner and legal give a period and a
/// source) anonymizes the customer in place: <see cref="AnonymizedAt"/> is set, the personal fields are replaced and the e-mail
/// hash is changed so that the address never matches again; the requests keep pointing at the row.</para>
/// </remarks>
[Table("ServiceCustomers")]
public class ServiceCustomer
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid OrgId { get; set; }

    /// <summary>
    /// HMAC-SHA256 (lowercase hex, 64 characters) of the normalized e-mail address, keyed by <c>Suppliers:CustomerIndexKey</c>:
    /// the index that finds the customer of an address (unique with <see cref="OrgId"/>) without decrypting anything. After the
    /// anonymization it is a tombstone (<c>anon-</c> and the id) that matches no address.
    /// </summary>
    [Required, MaxLength(64)]
    public string EmailHash { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? Phone { get; set; }

    [Required, MaxLength(ShowcaseBookingLimits.LocaleLength)]
    public string Locale { get; set; } = ServiceCustomerLocales.Default;

    [MaxLength(ShowcaseBookingLimits.PrivacyNoticeVersionMaxLength)]
    public string PrivacyNoticeVersion { get; set; } = string.Empty;

    public DateTime PrivacyAcceptedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(ShowcaseBookingLimits.ConsentIpMaxLength)]
    public string ConsentIp { get; set; } = string.Empty;

    public DateTime? AnonymizedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
