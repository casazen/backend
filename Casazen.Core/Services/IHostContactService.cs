using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// "Scrivi a {host}" on the public booking site (DB-03b): a visitor writes a few lines to the host, who gets them by email and answers
/// from their mailbox. Nothing is stored (no entity, no thread: a visitor has no booking yet) and nothing is answered about what happened to
/// the message: the caller always says «received», whether the org publishes its contact email or not, has one or not.
/// </summary>
public interface IHostContactService
{
    /// <summary>
    /// Forwards <paramref name="message"/> to the contact email of <paramref name="org"/>, with the visitor's address as the
    /// reply-to of the email. Does nothing, quietly, when the org has no contact email or the email queue refuses it. Never throws for
    /// that and never logs the address or the text.
    /// </summary>
    Task SendAsync(Org org, HostContactMessage message, CancellationToken cancellationToken = default);
}

/// <summary>The message of a visitor to the host (already validated and normalized by the endpoint).</summary>
/// <param name="Email">The visitor's address: where the host's answer goes.</param>
/// <param name="Message">The text, 5 to 2000 characters.</param>
/// <param name="Name">The visitor's name, when given.</param>
/// <param name="PropertyId">The property the visitor was looking at, when given; it counts only if it is a published property of the org.</param>
/// <param name="Lang">The language of the site the visitor was reading, <c>it</c> or <c>en</c>, when given: the host answers in it.</param>
public sealed record HostContactMessage(string Email, string Message, string? Name, Guid? PropertyId, string? Lang);

public static class HostContactRules
{
    public const int MessageMinLength = 5;
    public const int MessageMaxLength = 2000;
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 254;

    public const string ConsentRequiredCode = "host_contact_consent_required";

    public const string EmailInvalidCode = "host_contact_email_invalid";

    public const string MessageInvalidCode = "host_contact_message_invalid";

    public static string? NormalizeLang(string? lang) =>
        lang?.Trim().ToLowerInvariant() switch
        {
            "it" => "it",
            "en" => "en",
            _ => null,
        };

    public static string? NormalizeMessage(string? message)
    {
        var text = message?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return text is { Length: >= MessageMinLength and <= MessageMaxLength } ? text : null;
    }
}
