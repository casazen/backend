using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

public interface IAccessCodeService
{
    /// <summary>
    /// Sets or updates the access code for a property (encrypted storage).
    /// </summary>
    /// <param name="propertyId">Property to set the code for</param>
    /// <param name="code">Plain text code (will be encrypted)</param>
    /// <param name="userId">User setting the code</param>
    /// <param name="ct">Cancellation token</param>
    Task SetAccessCodeAsync(Guid propertyId, string code, string userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the access code for a booking (permission and verification required).
    /// Logs the access and BLOCKS if verification not passed. <b>The verification gate</b> (shared with <see cref="ReleaseToGuestAsync"/>):
    /// the latest <c>BiometricVerificationLog</c> of the booking is an <c>Accept</c>, of the agent or of an operator's approval; a review, a
    /// failed attempt, an operator's rejection or no row at all release nothing (AC-NF50-4, AC-NF24-4). <b>The booking must be one the caller
    /// reaches</b> (<paramref name="scope"/>, AM-03): a collaborator «Solo alcuni» reads the code of the properties it was given only, and a
    /// booking of another property or org is as absent as one that does not exist.
    /// </summary>
    /// <param name="bookingId">Booking to get code for</param>
    /// <param name="userId">User requesting the code</param>
    /// <param name="scope">What the caller reaches on host data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Plain text code, or null if not set, out of scope or verification failed</returns>
    Task<string?> GetAccessCodeAsync(Guid bookingId, string userId, HostScope scope, CancellationToken ct = default);

    /// <summary>
    /// Releases the access code to a guest (verification required, logged).
    /// Used by guest-facing endpoints after verification passes. Same gate as <see cref="GetAccessCodeAsync"/>; the release log records the
    /// channel and the time, never the code. Since E03/F04/US02 the stay must be active and not over (<c>AlloggiatiStatusRules.IsDue</c>, not
    /// checked out): a cancelled stay, a request, a hold or a no-show get no code.
    /// </summary>
    /// <param name="bookingId">Booking to release code for</param>
    /// <param name="channel">How the code is released, one of <see cref="AccessCodeReleaseChannels"/>; another value is an <see cref="ArgumentException"/></param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// Plain text code, or null if verification not passed, the stay is not active, the property has no code, or the code is held back
    /// (<see cref="GetHoldReasonAsync"/>: E25/F01/US01/T01, the tourist tax asked before the code)
    /// </returns>
    Task<string?> ReleaseToGuestAsync(Guid bookingId, string channel, CancellationToken ct = default);

    Task<string?> GetReleasableCodeAsync(Guid bookingId, string channel, CancellationToken ct = default);

    Task RecordReleaseAsync(Guid bookingId, string channel, CancellationToken ct = default);

    /// <summary>
    /// Why the lock code of the booking is held back from the guest now (E25/F01/US01/T01, NF-119): one of <see cref="AccessCodeHoldReasons"/>, or <c>null</c>
    /// when nothing holds it back. The one gate of every release to a guest (<see cref="ReleaseToGuestAsync"/>: the screen, the e-mail, the operator's
    /// approval) reads it, so no channel can bypass it; the callers that tell the guest why no code came read it too.
    /// </summary>
    /// <param name="bookingId">Booking whose code is asked for</param>
    /// <param name="ct">Cancellation token</param>
    Task<string?> GetHoldReasonAsync(Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Which of the properties <paramref name="scope"/> reaches have a lock code (NF-50, E03/F01/US03/T01), a page at a time: the
    /// property, whether a code is set and when it was last set. The properties are narrowed in SQL by the scope
    /// (<c>InScope</c>, AM-03), so a collaborator «Solo alcuni» gets the properties it was given and nobody gets those of another org.
    /// </summary>
    /// <remarks>
    /// It reads that a code exists and the date of it, never the code: the encrypted column is not selected, so nothing is decrypted
    /// and neither the code nor its ciphertext can reach the answer (ADR-013). Whether a caller may release a code to a guest is
    /// another rule (<see cref="GetAccessCodeAsync"/>, <see cref="ReleaseToGuestAsync"/>) that this does not touch.
    /// </remarks>
    /// <param name="scope">What the caller reaches (resolved by the web layer from the org membership, never from client input).</param>
    /// <param name="page">The page wanted (1-based), brought within <see cref="AccessCodeStatusPage.MaxPage"/>.</param>
    /// <param name="pageSize">The page size wanted, brought within 1 and <see cref="AccessCodeStatusPage.MaxPageSize"/>.</param>
    /// <param name="ct">Cancellation token</param>
    Task<AccessCodeStatusPage> GetStatusAsync(HostScope scope, int page, int pageSize, CancellationToken ct = default);
}

public static class AccessCodeReleaseChannels
{
    public const string CheckInScreen = "checkin-screen";

    public const string Email = "email";

    public const string Operator = "operator";

    public const string Screen = "screen";

    public const string PortalChat = "portal-chat";

    public const string GuestArea = "guest-area";

    public static IReadOnlyList<string> All { get; } = [CheckInScreen, Email, Operator, Screen, PortalChat, GuestArea];
}

public static class AccessCodeHoldReasons
{
    public const string TouristTaxDue = "tourist_tax_due";
}

/// <summary>A page of the lock code status of the properties of the host (NF-50).</summary>
/// <param name="Items">The properties of the page, by name (the id as the tie-break, so a property is never on two pages nor on none).</param>
/// <param name="TotalCount">All the properties the scope reaches, on every page.</param>
/// <param name="Page">The page taken (1-based) after bringing the request within the limits.</param>
/// <param name="PageSize">The size taken after bringing the request within the limits.</param>
public sealed record AccessCodeStatusPage(IReadOnlyList<AccessCodeStatusItem> Items, int TotalCount, int Page, int PageSize)
{
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 100;

    public const int MaxPage = 100_000;
}

/// <summary>
/// Whether one property has a lock code. There is no field for the code on purpose: the status never carries it, in any form.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Name">The name of the property.</param>
/// <param name="City">The city of the property.</param>
/// <param name="AccessCodeSet">True when a lock code was set for the property.</param>
/// <param name="UpdatedAt">When the code was last set (UTC); <c>null</c> when none is set.</param>
public sealed record AccessCodeStatusItem(Guid PropertyId, string Name, string City, bool AccessCodeSet, DateTime? UpdatedAt);
