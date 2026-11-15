using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Who issued a document of a booking (FIS-02, NF-82, NF-116, E06/F02/US02/T01, E17/F01/US01/T02): one row for each document CasaZen issues for the booking,
/// written in the transaction of its issue and never changed afterwards. Since the owners issue the documents of their stays (E17), a booking may have several
/// documents of several issuers (the receipt of its owner, the invoice of the agency for its services): the rule is no longer «never two issuers» but «never two
/// documents of the same issuer and purpose for the same booking», which the document service checks (the issuer of a document is its
/// <see cref="FiscalDocument.IssuerProfileId"/>). A legacy row locked to Octorate (before the decision of 2026-11-14) still keeps CasaZen off the booking.
/// </summary>
/// <remarks>
/// <see cref="LockedByDocumentId"/> is a plain column with no foreign key, unique where set (<see cref="DocumentIndexName"/>): one row for each document. The
/// row goes with its booking (cascade). <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the booking.
/// </remarks>
[Table("BookingDocumentIssuers")]
public class BookingDocumentIssuer : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="LockedByDocumentId"/> where it is set: one row for each document.</summary>
    public const string DocumentIndexName = "UIX_BookingDocumentIssuers_LockedByDocumentId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (TN-2). Inherited from the booking; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The booking (indexed, not unique: one row for each of its documents). The row goes with the booking (cascade).</summary>
    public Guid BookingId { get; set; }

    /// <summary>Who issues the documents of the booking.</summary>
    public BookingDocumentIssuerKind Issuer { get; set; }

    /// <summary>When the issuer was locked (UTC).</summary>
    public DateTime LockedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The document of the row (the one issued); <c>null</c> for a legacy row made before any document.</summary>
    public Guid? LockedByDocumentId { get; set; }
}
