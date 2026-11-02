using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// One message of the conversation between the customer of a request on measure and its supplier (SP-10b,
/// <see cref="ServiceRequestKind.Custom"/>). The conversation lives inside CasaZen on purpose: no public contact, no attachment, no
/// real-time channel — the two read it when they open the request, and an e-mail tells the other party that there is something new.
/// </summary>
/// <remarks>
/// <para><b>Personal data, encrypted.</b> The <see cref="Body"/> is free text a person wrote and can say anything; it is encrypted at
/// rest (<c>EncryptedColumns</c>, purpose <c>Casazen.ServiceRequestMessage</c>), read only by the two parties of the request, never put in
/// an e-mail, a push or a log, and goes with the customer when the retention anonymizes it.</para>
/// <para><b>Keyed by the supplier org</b> (<see cref="OrgId"/> is <see cref="ServiceRequest.SupplierOrgId"/>), not tenant-filtered, like the
/// other tables of the showcase: every statement carries the explicit <see cref="OrgId"/> predicate
/// (<c>ServiceRequestMessageTenancyTests</c>). A host never reads one.</para>
/// </remarks>
[Table("ServiceRequestMessages")]
public class ServiceRequestMessage
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The request the message belongs to; the messages go with it.</summary>
    public Guid ServiceRequestId { get; set; }

    /// <summary>The supplier org of the request (<see cref="ServiceRequest.SupplierOrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    public ServiceRequestMessageAuthor Author { get; set; }

    /// <summary>What was written, plain text. <b>Encrypted at rest</b>; empty once the retention anonymized the customer.</summary>
    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the <b>other</b> party first read it (UTC); <c>null</c> while it did not.</summary>
    public DateTime? ReadAt { get; set; }

    [ForeignKey(nameof(ServiceRequestId))]
    public ServiceRequest ServiceRequest { get; set; } = null!;
}
