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
    public const string DocumentIndexName = "UIX_BookingDocumentIssuers_LockedByDocumentId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public BookingDocumentIssuerKind Issuer { get; set; }

    public DateTime LockedAt { get; set; } = DateTime.UtcNow;

    public Guid? LockedByDocumentId { get; set; }
}
