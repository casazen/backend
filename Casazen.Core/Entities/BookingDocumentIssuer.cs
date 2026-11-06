using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The single issuer of the documents of a booking (FIS-02, NF-82, E06/F02/US02/T01): written when the first document of the booking is issued by
/// CasaZen, or found issued by Octorate, and never changed afterwards. A booking never has two issuers: the database itself refuses a second row.
/// </summary>
/// <remarks>
/// <see cref="LockedByDocumentId"/> is a plain column with no foreign key: the table of the documents arrives with E06/F01/US02/T01 and the link is
/// made there. The row goes with its booking (cascade). <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the booking.
/// </remarks>
[Table("BookingDocumentIssuers")]
public class BookingDocumentIssuer : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="BookingId"/>: one issuer for each booking.</summary>
    public const string BookingIndexName = "UIX_BookingDocumentIssuers_BookingId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (TN-2). Inherited from the booking; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The booking. Unique: it has at most one issuer. The row goes with the booking (cascade).</summary>
    public Guid BookingId { get; set; }

    /// <summary>Who issues the documents of the booking.</summary>
    public BookingDocumentIssuerKind Issuer { get; set; }

    /// <summary>When the issuer was locked (UTC).</summary>
    public DateTime LockedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The document that locked the issuer (the first one issued or imported); <c>null</c> when the lock was made before any document.</summary>
    public Guid? LockedByDocumentId { get; set; }
}
