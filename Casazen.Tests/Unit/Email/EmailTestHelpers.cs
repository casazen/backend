using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Email;

internal static class EmailTestHelpers
{
    public const string PublicSiteBaseUrl = "https://casazen-app.test";

    public static PublicSiteLinks Links(string? baseUrl = PublicSiteBaseUrl) =>
        new(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = baseUrl }));

    public static IOptions<EmailOptions> ConfiguredEmail(string fromAddress = "noreply@casazen.app") =>
        Options.Create(new EmailOptions
        {
            Provider = EmailOptions.ResendProvider,
            ApiKey = "re_test_key",
            FromAddress = fromAddress,
            FromName = "CasaZen",
        });
}

/// <summary>In-memory <see cref="IEmailQueue"/> that records what would be queued (safe for concurrent requests).</summary>
internal sealed class RecordingEmailQueue : IEmailQueue
{
    public List<(string? To, EmailContent Content, string Template)> Queued { get; } = [];

    public bool Refuse { get; set; }

    public bool Enqueue(string? to, EmailContent content, string template)
    {
        if (Refuse)
            return false;

        lock (Queued)
            Queued.Add((to, content, template));
        return true;
    }

    public IReadOnlyList<(string? To, EmailContent Content, string Template)> Snapshot()
    {
        lock (Queued)
            return Queued.ToList();
    }
}
