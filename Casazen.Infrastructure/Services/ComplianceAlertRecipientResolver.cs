using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceAlertRecipientResolver" />
/// <remarks>
/// <para><b>Today's audience, narrowed to who reaches the property.</b> The email goes to the address of the organization, as the stay alerts do; the
/// people are the ones <see cref="INotificationPreferenceService"/> tells about a legal deadline (<see cref="NotificationEventType.LegalDeadline"/>
/// is the event «a legal deadline is near or missed»): the audience of a property is the person in charge while they still reach it and the
/// administrators of the org, a collaborator «Solo alcuni» is among them only for a property it was given (in the area of its mode), a deactivated
/// member never. That service is the one place that decides it (<c>HostNotifyingPathsArchitectureTests</c>), so this class never rebuilds the reach
/// of anybody. The channels of each person (the push, the bell, the email to its own address, the quiet hours) stay today's until the per-person
/// preferences of NF-65 replace the delivery of these alerts; here only the audience is read.</para>
/// <para><b>An alert only a person with a permission can act on</b> (the notification of the guests to the police, <see cref="ComplianceAlertAudience"/>): the permission is handed to the same
/// service as the <see cref="NotificationEventContext.RequiredPermission"/> of the event, which asks <see cref="IContextAuthorizationService"/> (the evaluation of a permission the policies
/// use) for each person of the reach it decided: this class neither reads a role nor a permission. The address of the organization is not a person and is not narrowed.</para>
/// <para>The answer holds ids and addresses and nothing else; nothing is logged.</para>
/// </remarks>
public sealed class ComplianceAlertRecipientResolver(
    AppDbContext db,
    INotificationPreferenceService preferences) : IComplianceAlertRecipientResolver
{
    public Task<ComplianceAlertRecipients> ForPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct) =>
        ForPropertyCoreAsync(orgId, propertyId, requiredPermission: null, ct);

    public Task<ComplianceAlertRecipients> ForPropertyAsync(Guid orgId, Guid propertyId, NotificationPermission requiredPermission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requiredPermission);
        return ForPropertyCoreAsync(orgId, propertyId, requiredPermission, ct);
    }

    private async Task<ComplianceAlertRecipients> ForPropertyCoreAsync(Guid orgId, Guid propertyId, NotificationPermission? requiredPermission, CancellationToken ct)
    {
        var people = await preferences.ResolveAsync(
            new NotificationEventContext(orgId, NotificationEventType.LegalDeadline, propertyId, RequiredPermission: requiredPermission), ct);
        return new ComplianceAlertRecipients(await OrganizationAddressAsync(orgId, ct), UserIdsOf(people));
    }

    public async Task<ComplianceAlertRecipients> ForOrganizationAsync(Guid orgId, CancellationToken ct)
    {
        var people = await preferences.ResolveAsync(new NotificationEventContext(orgId, NotificationEventType.LegalDeadline, PropertyId: null), ct);
        var emails = (await OrganizationAddressAsync(orgId, ct))
            .Concat(people.Select(p => p.Email))
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ComplianceAlertRecipients(emails, UserIdsOf(people));
    }

    private static List<string> UserIdsOf(IReadOnlyList<NotificationRecipient> people) =>
        people.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();

    private async Task<List<string>> OrganizationAddressAsync(Guid orgId, CancellationToken ct)
    {
        var address = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.ContactEmail)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(address) ? [] : [address.Trim()];
    }
}
