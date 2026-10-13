using System.Text.RegularExpressions;

namespace Casazen.Core.Utilities;

/// <summary>
/// Masks personal data before it reaches the logs (FD-17, A9-36): the Railway logs must not hold the email addresses
/// of guests, hosts or suppliers. Log the entity id and, when an address really helps, only its masked form.
/// </summary>
public static partial class LogRedaction
{
    /// <summary>Placeholder for a missing value.</summary>
    public const string Empty = "(none)";

    /// <summary>Placeholder for a value that is not an email address: nothing of it is kept.</summary>
    public const string Hidden = "***";

    /// <summary>
    /// First character of the local part plus the domain: <c>mario.rossi@example.it</c> becomes <c>m***@example.it</c>.
    /// A value that is not an address (no <c>@</c>) is hidden completely.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Empty;

        var value = email.Trim();
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return Hidden;

        return $"{value[0]}{Hidden}{value[at..]}";
    }

    /// <summary>
    /// Masks every email address found in a free text, such as a provider error message. A text too long to scan in
    /// time is hidden completely rather than logged as is.
    /// </summary>
    public static string MaskEmails(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        try
        {
            return EmailInText().Replace(text, match => MaskEmail(match.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            return Hidden;
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailInText();
}
