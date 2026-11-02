using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Admin;

/// <summary>
/// One dispute in the platform admin's work queue (<c>GET api/admin/service-payment-disputes</c>, SP-15c-c): the facts the queue needs and the numbers of
/// the money, no words of either party. Ids and names of the supplier, never an e-mail, a phone or an address. Amounts are in cents.
/// </summary>
public class AdminServiceDisputeDto
{
    public Guid Id { get; set; }

    public Guid ServiceRequestId { get; set; }

    public Guid PaymentId { get; set; }

    public Guid SupplierOrgId { get; set; }

    public string SupplierName { get; set; } = string.Empty;

    /// <summary><c>AwaitingSupplier</c> (the supplier has until <c>supplierReplyDueAt</c>), <c>AwaitingDecision</c> (an admin decides), <c>ResolvedForSupplier</c>, <c>ResolvedForCustomer</c> or <c>Withdrawn</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or <c>other</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary><c>customer</c>, <c>host</c> or <c>system</c>.</summary>
    public string OpenedBy { get; set; } = string.Empty;

    /// <summary>The supplier never marked the work completed and the time agreed for it ended.</summary>
    public bool NoShow { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime SupplierReplyDueAt { get; set; }

    public DateTime? SupplierRepliedAt { get; set; }

    /// <summary>The target by which an admin decides; set when the dispute reaches the admins.</summary>
    public DateTime? DecisionDueAt { get; set; }

    /// <summary>The target to decide has passed.</summary>
    public bool Overdue { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>What the payer paid.</summary>
    public int AmountCents { get; set; }

    /// <summary>What the supplier would receive: the price minus CasaZen's commission.</summary>
    public int NetCents { get; set; }

    /// <summary>What was given back to the payer so far.</summary>
    public int RefundedCents { get; set; }

    /// <summary>The most that can still go back to the payer (the supplier's part that is left): the commission is never given back by a dispute.</summary>
    public int RefundableCents { get; set; }

    /// <summary>What the admin decided to give back, when decided for the customer.</summary>
    public int? DecidedRefundCents { get; set; }

    /// <summary>The refund that was decided has been made.</summary>
    public bool Settled { get; set; }

    public static AdminServiceDisputeDto From(AdminServiceDisputeSummary summary) => Fill(new AdminServiceDisputeDto(), summary);

    protected static T Fill<T>(T dto, AdminServiceDisputeSummary summary)
        where T : AdminServiceDisputeDto
    {
        dto.Id = summary.Id;
        dto.ServiceRequestId = summary.ServiceRequestId;
        dto.PaymentId = summary.ServiceRequestPaymentId;
        dto.SupplierOrgId = summary.SupplierOrgId;
        dto.SupplierName = summary.SupplierName;
        dto.Status = summary.Status.ToString();
        dto.Reason = ServiceDisputeRules.ReasonName(summary.Reason);
        dto.OpenedBy = ServiceDisputeRules.OpenerName(summary.OpenedBy);
        dto.NoShow = summary.NoShow;
        dto.OpenedAt = AsUtc(summary.OpenedAt);
        dto.SupplierReplyDueAt = AsUtc(summary.SupplierReplyDueAt);
        dto.SupplierRepliedAt = summary.SupplierRepliedAt is { } repliedAt ? AsUtc(repliedAt) : null;
        dto.DecisionDueAt = summary.DecisionDueAt is { } dueAt ? AsUtc(dueAt) : null;
        dto.Overdue = summary.Overdue;
        dto.ResolvedAt = summary.ResolvedAt is { } resolvedAt ? AsUtc(resolvedAt) : null;
        dto.AmountCents = summary.AmountCents;
        dto.NetCents = summary.NetCents;
        dto.RefundedCents = summary.RefundedCents;
        dto.RefundableCents = summary.RefundableCents;
        dto.DecidedRefundCents = summary.DecidedRefundCents;
        dto.Settled = summary.Settled;
        return dto;
    }

    protected static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>
/// One dispute for the admin who decides it (<c>GET api/admin/service-payment-disputes/{id}</c>): the line of the queue plus the state of the payment and
/// the request, the words of both parties and the admin's own note. Only an admin reads this.
/// </summary>
public sealed class AdminServiceDisputeDetailDto : AdminServiceDisputeDto
{
    /// <summary>The status of the payment: <c>Disputed</c> while the money is stopped.</summary>
    public string PaymentStatus { get; set; } = string.Empty;

    /// <summary>CasaZen's commission in cents, which a dispute never gives back.</summary>
    public int CommissionCents { get; set; }

    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Where the request is: <c>Completato</c>, or a job still taken (<c>PresoInCarico</c>, <c>InCorso</c>) for a no-show.</summary>
    public string RequestStatus { get; set; } = string.Empty;

    /// <summary>When the time agreed for the work ends.</summary>
    public DateTime? ScheduledEndUtc { get; set; }

    /// <summary>What the payer wrote.</summary>
    public string? CustomerText { get; set; }

    /// <summary>What the supplier answered.</summary>
    public string? SupplierReply { get; set; }

    /// <summary>The admin's internal note on the decision; never shown to the parties.</summary>
    public string? AdminNote { get; set; }

    /// <summary>The admin who decided.</summary>
    public string? ResolvedByUserId { get; set; }

    public static AdminServiceDisputeDetailDto From(AdminServiceDisputeDetail detail)
    {
        var dto = Fill(new AdminServiceDisputeDetailDto(), detail.Summary);
        dto.PaymentStatus = detail.PaymentStatus.ToString();
        dto.CommissionCents = detail.CommissionCents;
        dto.ServiceName = detail.ServiceName;
        dto.RequestStatus = detail.RequestStatus.ToString();
        dto.ScheduledEndUtc = detail.ScheduledEndUtc is { } end ? AsUtc(end) : null;
        dto.CustomerText = detail.CustomerText;
        dto.SupplierReply = detail.SupplierReply;
        dto.AdminNote = detail.AdminNote;
        dto.ResolvedByUserId = detail.ResolvedByUserId;
        return dto;
    }
}

/// <summary>Body of <c>POST api/admin/service-payment-disputes/{id}/resolve</c> (SP-15c-c): the decision of the admin.</summary>
public sealed class ResolveServiceDisputeBody
{
    /// <summary><c>for_supplier</c> (the complaint is unfounded: the supplier is paid) or <c>for_customer</c> (the supplier was at fault).</summary>
    [Required(ErrorMessage = ServiceDisputeErrors.OutcomeInvalidMessageKey)]
    [MaxLength(32, ErrorMessage = ServiceDisputeErrors.OutcomeInvalidMessageKey)]
    public string? Outcome { get; set; }

    /// <summary>
    /// For the customer: what to give back to the payer, in cents, from 1 to <c>refundableCents</c> (the supplier's part that is left); left out means all
    /// of it. CasaZen keeps its commission whatever the amount. Not allowed for the supplier.
    /// </summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = ServiceDisputeErrors.RefundInvalidMessageKey)]
    public int? RefundCents { get; set; }

    /// <summary>The admin's internal note, at most 1000 characters. It is kept encrypted, never shown to the parties and never sent to Stripe.</summary>
    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Note { get; set; }

    public ResolveServiceDisputeRequest ToRequest() => new(Outcome, RefundCents, Note);
}
