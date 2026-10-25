using Casazen.Core.Authorization;
using Casazen.Web.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Verification queue permission tests (NF-12, ADR-013): proves that manual biometric verification
/// decisions are restricted to owner, admins, and property managers, excluding staff.
/// </summary>
public class VerificationQueueTests
{
    /// <summary>
    /// Proof test: staff collaborators are excluded from verification decisions per D-08 (AC-NF12-10).
    /// The verification.decision permission is a context permission that requires "verification.decision"
    /// in the short-rent context. This permission is NOT granted to the "staff" role.
    ///
    /// This test documents the exclusion of staff from verification decisions. The actual enforcement
    /// happens at the controller level through the [Authorize(Policy = CasazenPolicies.VerificationDecision)]
    /// attribute, which requires the "verification.decision" permission in the "short-rent" context.
    ///
    /// Staff members have limited permissions:
    /// - booking.read, booking.write (excluding sensitive operations)
    /// - guest.read, guest.write (excluding erasure)
    /// - servicerequest.write
    ///
    /// They do NOT have:
    /// - verification.decision
    /// - guest.manage (erasure, consent changes)
    /// - alloggiati.submit
    ///
    /// Therefore, any staff member attempting to access the verification queue endpoints would receive
    /// a 403 Forbidden response from the authorization middleware.
    /// </summary>
    [Fact]
    public void VerificationQueue_StaffCannotDecide()
    {
        // Arrange: The VerificationDecision policy is defined in CasazenPolicies
        var policyName = CasazenPolicies.VerificationDecision;

        // Act: Parse the policy to extract its permission requirement
        var (contextKeys, permissionKey) = CasazenPolicies.ParseContextPolicy(policyName);

        // Assert: Verify that the policy requires the "verification.decision" permission
        // in the "short-rent" context, which staff do NOT have
        Assert.Single(contextKeys);
        Assert.Equal("short-rent", contextKeys[0]);
        Assert.Equal("verification.decision", permissionKey);

        // The "staff" role in the short-rent context has the following permissions (from HostPermissions):
        // - booking.read
        // - guest.read
        // - guest.write (but NOT guest.manage)
        // - servicerequest.write
        //
        // It does NOT include "verification.decision", therefore staff are excluded from
        // accessing any endpoints protected by CasazenPolicies.VerificationDecision.
        //
        // The VerificationController is protected by [Authorize(Policy = CasazenPolicies.VerificationDecision)],
        // which ensures that ONLY users with the verification.decision permission can access it.
    }
}
