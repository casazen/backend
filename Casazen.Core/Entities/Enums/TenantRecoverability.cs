namespace Casazen.Core.Entities.Enums;

/// <summary>Whether a condominium expense may later be charged to the tenant. Stored as an integer: never renumber.</summary>
public enum TenantRecoverability
{
    Recoverable = 0,
    OwnerOnly = 1,
    PartiallyRecoverable = 2,
    NeedsReview = 3,
}
