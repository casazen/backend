using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// A complaint about a job that is paid through CasaZen (SP-15c-c, decisions VT-D1 and VT-D16): the payer says the work was not done, was left half
/// done, was done badly or is not what was agreed, and the money CasaZen holds for it stops (the payment is
/// <see cref="ServicePaymentStatus.Disputed"/>) until a person decides. <b>Text only</b>: no photos (VT-D12).
/// </summary>
/// <remarks>
/// <para><b>Life.</b> <see cref="ServiceDisputeStatus.AwaitingSupplier"/> (the supplier has <c>DisputeSupplierReplyHours</c>, 48 by default, to reply)
/// → <see cref="ServiceDisputeStatus.AwaitingDecision"/> (it replied, or its time passed; an admin decides, target 72 hours) → resolved for the supplier
/// (it is paid), resolved for the customer (the payer gets back what the admin decided, at most the price minus the commission, which CasaZen keeps and
/// the supplier loses — and the supplier is penalized in the ranking) or withdrawn by the payer. <b>One open dispute per payment</b> (a unique partial
/// index): a second complaint on the same payment is the same dispute.</para>
/// <para><b>Money.</b> Opening, replying, withdrawing and resolving all run under the payment lock of the request (the same as the release and the
/// refunds), so a release and a complaint never happen on the same money: whichever takes the lock first wins, and the other is refused or finds
/// the dispute. A decision for the customer reserves its refund in the same transaction that records it (the supplier is not paid in between), and
/// the payment leaves <see cref="ServicePaymentStatus.Disputed"/> only when the money of the decision was given back (<see cref="SettledAt"/>).</para>
/// <para><b>Privacy.</b> The words of the payer, of the supplier and of the admin are encrypted at rest (<c>EncryptedColumns</c>, columns
/// <c>CustomerTextEncrypted</c>, <c>SupplierReplyEncrypted</c>, <c>AdminNoteEncrypted</c>) and returned only to the people they concern; they are in
/// no log and in no e-mail.</para>
/// <para><b>Tenancy.</b> Like the payment it belongs to, it has two parties and an anonymous payer, so it is not <c>ITenantOwned</c> (allow-list of
/// <c>TenantQueryFilterArchitectureTests</c>, guard <c>ServicePaymentDisputeTenancyTests</c>): every statement names the payment, the request or the
/// supplier org it is for.</para>
/// </remarks>
[Table("ServicePaymentDisputes")]
public class ServicePaymentDispute
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ServiceRequestPaymentId { get; set; }

    public Guid ServiceRequestId { get; set; }

    public Guid SupplierOrgId { get; set; }

    public ServiceDisputeStatus Status { get; set; } = ServiceDisputeStatus.AwaitingSupplier;

    public ServiceDisputeReason Reason { get; set; }

    public ServiceDisputeOpener OpenedBy { get; set; }

    public bool NoShow { get; set; }

    [MaxLength(255)]
    public string? OpenedByUserId { get; set; }

    public string? CustomerText { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    public DateTime SupplierReplyDueAt { get; set; }

    public string? SupplierReply { get; set; }

    public DateTime? SupplierRepliedAt { get; set; }

    public DateTime? DecisionDueAt { get; set; }

    public DateTime? DecisionOverdueAlertedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    [MaxLength(255)]
    public string? ResolvedByUserId { get; set; }

    public string? AdminNote { get; set; }

    public int? DecidedRefundCents { get; set; }

    public int? RefundTargetCents { get; set; }

    public DateTime? SettledAt { get; set; }

    public DateTime? RefundAlertedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ServiceRequestPaymentId))]
    public ServiceRequestPayment ServiceRequestPayment { get; set; } = null!;
}
