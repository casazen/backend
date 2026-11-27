namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the access to a channel in the Octorate panel stands, as the import shows it (E02/F01/US03): the states the wizard names
/// «Da collegare», «In attesa del login», «Verifica in corso», «Da confermare», «Collegato». Stored as its number in
/// <c>OctorateInventoryImportChannels.AccessState</c>; an existing number never changes.
/// </summary>
public enum ChannelAccessState
{
    NotStarted = 1,

    Waiting = 2,

    /// <summary>CasaZen checked at least once and did not see the access yet: it checks again.</summary>
    Verifying = 3,

    NeedsConfirmation = 4,

    Verified = 5,

    Confirmed = 6,

    NotValid = 7,
}
