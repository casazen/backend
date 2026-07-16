namespace Casazen.Core.Entities.Enums;

/// <summary>Verification state of an <see cref="Org.CustomDomain"/> TXT ownership challenge (#298).</summary>
public enum DomainVerificationStatus
{
    Pending = 0,
    Verified = 1,
    Failed = 2,
}
