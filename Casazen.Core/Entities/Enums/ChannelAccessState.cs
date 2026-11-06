namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the access to a channel in the Octorate panel stands, as the import shows it (E02/F01/US03): the states the wizard names
/// «Da collegare», «In attesa del login», «Verifica in corso», «Da confermare», «Collegato». Stored as its number in
/// <c>OctorateInventoryImportChannels.AccessState</c>; an existing number never changes.
/// </summary>
public enum ChannelAccessState
{
    /// <summary>The person was not sent to the panel yet.</summary>
    NotStarted = 1,

    /// <summary>The person was sent to the panel and CasaZen has not checked the result yet.</summary>
    Waiting = 2,

    /// <summary>CasaZen checked at least once and did not see the access yet: it checks again.</summary>
    Verifying = 3,

    /// <summary>CasaZen could not tell by itself (the check ran out): only now the person may say it is done.</summary>
    NeedsConfirmation = 4,

    /// <summary>CasaZen saw the access given.</summary>
    Verified = 5,

    /// <summary>The person confirmed the access after CasaZen could not check it.</summary>
    Confirmed = 6,

    /// <summary>The check found the access refused or not valid: it has to be given again.</summary>
    NotValid = 7,
}
