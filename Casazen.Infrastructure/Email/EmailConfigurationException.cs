namespace Casazen.Infrastructure.Email;

/// <summary>
/// A value needed to build an email is missing or invalid (e.g. <c>App:PublicSiteBaseUrl</c>). It is a server
/// configuration error (500), never a reason to send a wrong link.
/// </summary>
public sealed class EmailConfigurationException(string message) : Exception(message);
