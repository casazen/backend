using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostContactService"/>
/// <remarks>
/// <para>The one email goes to <see cref="Org.ContactEmail"/> whether or not the org publishes that address on its site
/// (<see cref="Org.ContactEmailPublic"/> is about showing the address; the form never shows it), with the visitor's address as the
/// <c>Reply-To</c>, so the host answers from their mailbox. An org with no contact email, or an email queue that cannot take it, is a
/// message nobody receives: the caller cannot tell, so the answer never reveals what an org has or publishes.</para>
/// <para>Privacy: nothing is stored here; the log has the org id and whether the email was queued, never the address, the name or
/// the text. The text sits in the Hangfire job of the email like the body of every other email, until the job is deleted.</para>
/// </remarks>
public sealed class HostContactService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicOrgSiteUrls siteUrls,
    ILogger<HostContactService> logger) : IHostContactService
{
    public async Task SendAsync(Org org, HostContactMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(org.ContactEmail))
        {
            logger.LogInformation("Contact message for org {OrgId} dropped: the org has no contact email", org.Id);
            return;
        }

        // The property the visitor was looking at, only when it really is a published property of this org: any other id is ignored,
        // so a made-up one tells nothing and puts nothing in the email.
        string? propertyName = null;
        string? propertyUrl = null;
        if (message.PropertyId is { } propertyId)
        {
            var property = await db.Properties
                .AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(PublicListing.IsPublished)
                .Where(p => p.Id == propertyId && p.OrgId == org.Id)
                .Select(p => new { p.Name, p.Slug })
                .FirstOrDefaultAsync(cancellationToken);
            if (property is not null)
            {
                propertyName = property.Name;
                propertyUrl = siteUrls.TryPropertyUrl(org, string.IsNullOrWhiteSpace(property.Slug) ? propertyId.ToString() : property.Slug);
            }
        }

        var rendered = EmailTemplates.HostContactMessage(
            EmailTemplates.DefaultCulture,
            message.Name ?? string.Empty,
            message.Email,
            message.Message,
            propertyName,
            propertyUrl,
            message.Lang);

        // The host answers by replying to the email: the answer goes to the visitor, the sender stays the configured one.
        var content = rendered with { ReplyTo = message.Email };

        var queued = emailQueue.Enqueue(org.ContactEmail, content, EmailTemplates.Names.HostContactMessage);
        logger.LogInformation("Contact message for org {OrgId}: email queued={Queued}", org.Id, queued);
    }
}
