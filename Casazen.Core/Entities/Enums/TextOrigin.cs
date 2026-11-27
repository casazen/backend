namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who wrote a text of the listing in a language other than Italian (NF-31, ADR-035-R3): the origin the host reads next to each text and the
/// rule that keeps a machine translation from ever overwriting what a person wrote. Stored as an integer in the site profile document,
/// <b>append only</b>: never renumber a member.
/// </summary>
/// <remarks>
/// A text with no origin recorded is <see cref="UserWritten"/>: every text that existed before the origins did was written by a person.
/// </remarks>
public enum TextOrigin
{
    /// <summary>A person wrote it.</summary>
    UserWritten = 0,

    /// <summary>A machine translated it from the Italian text and nobody has touched it since.</summary>
    AutoTranslated = 1,

    /// <summary>A person changed a text that was machine translated.</summary>
    ModifiedAfterTranslation = 2,
}
