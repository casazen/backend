using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// Tenant key. Every tenant-scoped table carries an <c>OrgId</c> FK to this entity (RF1).
/// Slug is unique. Stripe identifiers are non-secret account references, never credentials.
/// </summary>
[Table("Orgs")]
public class Org
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Unique, URL-safe org identifier (internal in US-004; public branding added later).</summary>
    [Required, MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [Required]
    public PlanTier PlanTier { get; set; } = PlanTier.Starter;

    [Required, MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? LogoUrl { get; set; }

    [MaxLength(20)]
    public string? ThemeColor { get; set; }

    [MaxLength(255)]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>Non-secret Stripe customer reference (billing). Set by <c>spec-saas-billing</c>.</summary>
    [MaxLength(255)]
    public string? StripeCustomerId { get; set; }

    /// <summary>Non-secret Stripe Connect account reference (payouts). Used by <c>spec-direct-checkout</c>.</summary>
    [MaxLength(255)]
    public string? StripeConnectedAccountId { get; set; }

    /// <summary>Cached from Stripe <c>account.updated</c> — true when the connected account can accept charges.</summary>
    public bool ConnectChargesEnabled { get; set; }

    /// <summary>Cached from Stripe — payouts capability on the connected account.</summary>
    public bool ConnectPayoutsEnabled { get; set; }

    /// <summary>Cached from Stripe — KYC/details submitted on the connected account.</summary>
    public bool ConnectDetailsSubmitted { get; set; }

    /// <summary>JSON array of outstanding Stripe requirement field names (e.g. <c>["individual.verification.document"]</c>).</summary>
    public string? ConnectRequirementsDueJson { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
