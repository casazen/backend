namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the house rules say about parties and events (SR-07a): one of the two answers of the wizard. Stored as an integer in
/// <c>Properties.PartiesPolicy</c>, <b>null = the host never said</b>: <b>append only</b>.
/// </summary>
public enum PartiesPolicy
{
    /// <summary>Vietati.</summary>
    No = 0,

    /// <summary>Solo su richiesta.</summary>
    OnRequest = 1,
}
