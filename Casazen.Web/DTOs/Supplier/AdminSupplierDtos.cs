using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Supplier;

public class SuspendSupplierRequest
{
    [Required(ErrorMessage = "SupplierSuspendReasonRequired")]
    [MaxLength(500, ErrorMessage = "SupplierSuspendReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}

public class AdminSupplierDto
{
    public Guid OrgId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public IEnumerable<string> Categories { get; set; } = [];
    public IEnumerable<string> Comuni { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }

    public int OpenRequests { get; set; }

    internal static AdminSupplierDto From(AdminSupplierItem item) => new()
    {
        OrgId = item.OrgId,
        LegalName = item.LegalName,
        Email = item.Email,
        Phone = item.Phone,
        Status = item.Status.ToString(),
        Categories = item.Categories,
        Comuni = item.Comuni,
        CreatedAt = item.CreatedAt,
        SuspendedAt = item.SuspendedAt,
        SuspensionReason = item.SuspensionReason,
        OpenRequests = item.OpenRequests,
    };
}

public class AdminInviteDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string ComuneCode { get; set; } = string.Empty;
    public IEnumerable<string> Categories { get; set; } = [];
    public string? Message { get; set; }

    public string State { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    internal static AdminInviteDto From(AdminInviteItem item) => new()
    {
        Id = item.Id,
        Email = item.Email,
        ComuneCode = item.ComuneCode,
        Categories = item.Categories,
        Message = item.Message,
        State = item.State.ToString(),
        CreatedAt = item.CreatedAt,
        ExpiresAt = item.ExpiresAt,
        RevokedAt = item.RevokedAt,
    };
}

public class SupplierAdminAuditDto
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public string? ActorName { get; set; }

    public DateTime OccurredAt { get; set; }
    public string? Reason { get; set; }
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }

    internal static SupplierAdminAuditDto From(SupplierAdminAuditItem item) => new()
    {
        Id = item.Id,
        Action = item.Action.ToString(),
        ActorUserId = item.ActorUserId,
        ActorName = item.ActorName,
        OccurredAt = item.OccurredAt,
        Reason = item.Reason,
        PreviousStatus = item.PreviousStatus?.ToString(),
        NewStatus = item.NewStatus?.ToString(),
    };
}
