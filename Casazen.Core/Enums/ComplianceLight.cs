namespace Casazen.Core.Enums;

/// <summary>
/// The traffic light of one item of the compliance cockpit (E03/F02/US01/T03, NF-52, CHK-12): "am I compliant?". Serialized by name
/// (<c>Green</c>, <c>Yellow</c>, <c>Red</c>) and numbered from 1 so that an unset value (0) is never read as green.
/// </summary>
/// <remarks>
/// Green is a claim that needs a proof: <see cref="Services.ComplianceLightRules"/>, the one rule class, returns it only when it receives a
/// proof reference (a receipt, a declaration, a valid CIN, a passed verification). Yellow is the work in hand, red is what the host has to do.
/// </remarks>
public enum ComplianceLight
{
    /// <summary>Compliant, and an archived proof says so (an Alloggiati receipt, a tax receipt, a valid CIN, a passed identity verification).</summary>
    Green = 1,

    /// <summary>In progress: the data were entrusted to a sender within the deadline and the receipt is awaited, or the period is open.</summary>
    Yellow = 2,

    /// <summary>The host has to do something: a state or a deadline that already produced an item or an alert, or a new case of CHK-12.</summary>
    Red = 3,
}
