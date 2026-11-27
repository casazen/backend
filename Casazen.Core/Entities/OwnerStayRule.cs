using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OwnerStayRules")]
public class OwnerStayRule : ITenantOwned
{
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public Guid? PropertyOwnerId { get; set; }

    public int? YearlyNightAllowance { get; set; }

    public int MinAdvanceDays { get; set; }

    public int? MaxAdvanceDays { get; set; }

    public int MinNights { get; set; } = 1;

    public int? MaxNights { get; set; }

    public bool ApprovalRequired { get; set; } = true;

    public DateOnly? ActiveFrom { get; set; }

    public DateOnly? ActiveUntil { get; set; }

    public string BlackoutsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
