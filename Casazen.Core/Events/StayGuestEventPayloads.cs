using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// The payload of <see cref="DomainEventType.StayGuestsChanged"/> (E20/F01/US01/T01, ADR-022-R21): which stay, which property and who saved the
/// guests, ids and one word, never a name, a document or a place. A consumer reads the guests from the booking by <c>EntityId</c>, in its own org.
/// </summary>
/// <param name="BookingId">The stay.</param>
/// <param name="PropertyId">Its property.</param>
/// <param name="Author"><see cref="StayGuestsChangedAuthors.Guest"/> or <see cref="StayGuestsChangedAuthors.Host"/>.</param>
public sealed record StayGuestsChangedPayload(Guid BookingId, Guid PropertyId, string Author);

/// <summary>The words of <see cref="StayGuestsChangedPayload.Author"/>: who saved the guests.</summary>
public static class StayGuestsChangedAuthors
{
    /// <summary>The guest, on the online check-in page.</summary>
    public const string Guest = "guest";

    /// <summary>A person of the host, in the check-in wizard or on the Alloggiati page.</summary>
    public const string Host = "host";
}

/// <summary>
/// The payload of <see cref="DomainEventType.IdVerificationCompleted"/> (E20/F01/US01/T01): the identity of the guest of a stay was verified, by the
/// agent (<see cref="IdVerificationMethods.Biometric"/>) or by an operator (<see cref="IdVerificationMethods.Operator"/>). Only a passed verification
/// is recorded. <see cref="IdVerified"/> is always <c>true</c>: it is the name the server-side guardrails of the routines read (ADR-022-R11). No
/// picture, no name, no value read on the document.
/// </summary>
/// <param name="BookingId">The stay.</param>
/// <param name="PropertyId">Its property.</param>
/// <param name="Method">How it passed.</param>
/// <param name="IdVerified">Always <c>true</c>.</param>
public sealed record IdVerificationCompletedPayload(Guid BookingId, Guid PropertyId, string Method, bool IdVerified = true);

/// <summary>The words of <see cref="IdVerificationCompletedPayload.Method"/>.</summary>
public static class IdVerificationMethods
{
    /// <summary>The face comparison passed on its own.</summary>
    public const string Biometric = "biometric";

    /// <summary>An operator approved it.</summary>
    public const string Operator = "operator";
}
