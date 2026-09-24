namespace Casazen.Core.Services;

/// <summary>
/// Profile of an Auth0 user read from the Management API. <paramref name="EmailVerified"/> is Auth0's
/// <c>email_verified</c> flag (null when Auth0 did not return it).
/// </summary>
public record Auth0UserProfile(string Email, string FirstName, string LastName, bool? EmailVerified = null);
