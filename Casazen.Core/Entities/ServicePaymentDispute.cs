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

    /// <summary>The payment that is disputed. A payment with a dispute is never deleted (restrict).</summary>
    public Guid ServiceRequestPaymentId { get; set; }

    /// <summary>The request the payment is for (kept next to the payment so the parties find the dispute by the request they know).</summary>
    public Guid ServiceRequestId { get; set; }

    /// <summary>The supplier that is paid: the only supplier that sees and answers the dispute.</summary>
    public Guid SupplierOrgId { get; set; }

    public ServiceDisputeStatus Status { get; set; } = ServiceDisputeStatus.AwaitingSupplier;

    public ServiceDisputeReason Reason { get; set; }

    public ServiceDisputeOpener OpenedBy { get; set; }

    /// <summary>
    /// The supplier had not marked the work completed when the complaint was opened (the time agreed for it had ended): the no-show. It tells the
    /// admin what to look for and, with the outcome, which penalty the supplier gets.
    /// </summary>
    public bool NoShow { get; set; }

    /// <summary>The Auth0 subject of the host member who opened it; empty for a showcase customer (who has no account) and for CasaZen.</summary>
    [MaxLength(255)]
    public string? OpenedByUserId { get; set; }

    /// <summary>
    /// What the payer wrote (at most <see cref="Casazen.Core.Suppliers.ServiceDisputeLimits.TextMaxLength"/> characters); empty for the no-show
    /// CasaZen opens. Encrypted at rest.
    /// </summary>
    public string? CustomerText { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The instant the supplier's time to reply ends: <see cref="OpenedAt"/> plus <c>DisputeSupplierReplyHours</c>.</summary>
    public DateTime SupplierReplyDueAt { get; set; }

    /// <summary>What the supplier answered; one reply (a late one is taken while the dispute is open). Encrypted at rest.</summary>
    public string? SupplierReply { get; set; }

    public DateTime? SupplierRepliedAt { get; set; }

    /// <summary>
    /// The target by which an admin decides: set when the dispute reaches <see cref="ServiceDisputeStatus.AwaitingDecision"/>, plus
    /// <c>DisputeDecisionTargetHours</c>. Nothing is decided by itself when it passes: the admins are told, once.
    /// </summary>
    public DateTime? DecisionDueAt { get; set; }

    /// <summary>When the admins were told that the target had passed (once).</summary>
    public DateTime? DecisionOverdueAlertedAt { get; set; }

    /// <summary>When the dispute was resolved or withdrawn; empty while it is open.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>The Auth0 subject of the admin who decided; empty for a withdrawal.</summary>
    [MaxLength(255)]
    public string? ResolvedByUserId { get; set; }

    /// <summary>The admin's note on the decision (internal, never shown to the parties). Encrypted at rest.</summary>
    public string? AdminNote { get; set; }

    /// <summary>What the admin decided to give back to the payer, in cents (1 to the price minus the commission); set only when resolved for the customer (0 when nothing was left to give back).</summary>
    public int? DecidedRefundCents { get; set; }

    /// <summary>
    /// The total the payment must have given back for the decision to be done: what was refunded or being refunded when the admin decided, plus
    /// <see cref="DecidedRefundCents"/>. Set with it. The payment leaves <see cref="ServicePaymentStatus.Disputed"/> when its refunded amount reaches it.
    /// </summary>
    public int? RefundTargetCents { get; set; }

    /// <summary>
    /// When the money of a decision for the customer was settled: the refund that was decided succeeded and what is left of the supplier's part is
    /// free to be paid (or nothing was left). Empty until then, and for every other outcome.
    /// </summary>
    public DateTime? SettledAt { get; set; }

    /// <summary>When the admins were told that the refund of the decision had not gone through (once).</summary>
    public DateTime? RefundAlertedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ServiceRequestPaymentId))]
    public ServiceRequestPayment ServiceRequestPayment { get; set; } = null!;
}
