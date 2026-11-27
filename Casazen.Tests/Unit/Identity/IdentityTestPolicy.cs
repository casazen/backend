using System.Text.Json;
using Casazen.Infrastructure.Identity;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// The decision policy of the plan, as a value for the tests of the presence (E72/F01/US01): the committed file <c>identity-policy-2026.11-1.json</c>, read the way the
/// application reads its members (camel case), so a test works on the very numbers the product ships with and changes one with <c>with</c> when it needs another.
/// Public so that the tests of the later epics (E74) can use it.
/// </summary>
public static class IdentityTestPolicy
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The committed policy, as the checkout of this machine has it.</summary>
    public static IdentityDecisionPolicy Shipped()
    {
        return JsonSerializer.Deserialize<IdentityDecisionPolicy>(IdentityPolicyTestFiles.ShippedText(), Web)
            ?? throw new InvalidOperationException("The committed policy file is not a policy.");
    }
}
