namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the house rules say about children (NF-37, AC-NF37-1): one of the three answers, next to pets, smoking, parties and the quiet hours.
/// Stored as an integer in <c>Properties.ChildrenPolicy</c>, <b>null = the host never said</b>: <b>append only</b>. It is the rule the
/// channel manager receives in its structured rules; the public page keeps reading <c>Properties.ChildrenWelcome</c> of the site profile.
/// </summary>
public enum ChildrenPolicy
{
    /// <summary>Non adatto ai bambini.</summary>
    No = 0,

    /// <summary>Su richiesta.</summary>
    OnRequest = 1,

    /// <summary>Benvenuti.</summary>
    Yes = 2,
}
