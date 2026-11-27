namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the listings of a property are today, as the person declared it at the first question of the connect wizard (section 9.1 of the
/// source requirements, D6, E02/F01/US03). Stored as its number in <c>OctorateInventoryIntents.Scenario</c>: every member has an explicit
/// value and an existing number never changes. Only <see cref="NewInventory"/> may lead to the creation of a listing on Octorate or on an
/// OTA, and only when the person chose it explicitly: it is never inferred from a missing mapping or from missing data.
/// </summary>
public enum OctorateInventoryScenario
{
    /// <summary>The structures are already on Octorate: the consent of the account finds them and they are mapped (<c>onOctorate</c>).</summary>
    OnOctorate = 1,

    /// <summary>
    /// The listings already exist on one or more OTAs, or in another channel manager: the access to each channel is given in the Octorate
    /// panel and the existing inventory is imported, keeping its identifiers. Nothing is created or published (<c>onOtas</c>).
    /// </summary>
    OnOtas = 2,

    /// <summary>The listings were never published: the only scenario in which CasaZen creates them (<c>newInventory</c>).</summary>
    NewInventory = 3,
}

/// <summary>The codes the API speaks for <see cref="OctorateInventoryScenario"/> (cross-repo contract with the web wizard).</summary>
public static class OctorateInventoryScenarioCodes
{
    public const string OnOctorate = "onOctorate";
    public const string OnOtas = "onOtas";
    public const string NewInventory = "newInventory";

    /// <summary>The code of <paramref name="scenario"/>.</summary>
    public static string CodeOf(OctorateInventoryScenario scenario) => scenario switch
    {
        OctorateInventoryScenario.OnOctorate => OnOctorate,
        OctorateInventoryScenario.OnOtas => OnOtas,
        OctorateInventoryScenario.NewInventory => NewInventory,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not an inventory scenario."),
    };

    /// <summary>The scenario of a code (compared without regard to case); <c>false</c> for anything else, an empty value included.</summary>
    public static bool TryParse(string? code, out OctorateInventoryScenario scenario)
    {
        var trimmed = code?.Trim();
        if (string.Equals(trimmed, OnOctorate, StringComparison.OrdinalIgnoreCase))
        {
            scenario = OctorateInventoryScenario.OnOctorate;
            return true;
        }

        if (string.Equals(trimmed, OnOtas, StringComparison.OrdinalIgnoreCase))
        {
            scenario = OctorateInventoryScenario.OnOtas;
            return true;
        }

        if (string.Equals(trimmed, NewInventory, StringComparison.OrdinalIgnoreCase))
        {
            scenario = OctorateInventoryScenario.NewInventory;
            return true;
        }

        scenario = default;
        return false;
    }
}
