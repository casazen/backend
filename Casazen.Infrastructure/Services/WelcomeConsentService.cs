using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IWelcomeConsentService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The order of the checks</b> is the one a person meets them in: the link, the declaration of age, the channels, the address of each
/// channel, the consent text (configured, then current). Nothing is written before all of them pass.</item>
/// <item><b>What is used</b>: only what the guest consented to. The e-mail address is used when the e-mail box is ticked, the phone number when
/// the WhatsApp box is ticked; an address typed and not consented to is neither stored nor hashed.</item>
/// <item><b>The lead profile</b> holds no stay and no guest: it is found through the keyed e-mail contact (or the phone key), else made empty,
/// and the property of the link is added to its properties so that the host of that property sees it (HostScope). A later booking of the same
/// person recomputes the properties from the bookings.</item>
/// <item><b>Same answer, nothing lifted</b>: the outcome of the double opt-in (<see cref="IMarketingConfirmationService"/>) is not looked at, so
/// a suppressed or confirmed address gets the same answer as a new one; the consent contact service never clears a suppression.</item>
/// <item><b>Tenant</b>: the org is the one of the link. The logs carry ids only, never an address or a number.</item>
/// </list>
/// </remarks>
public class WelcomeConsentService(
    AppDbContext db,
    IGuestMatchingService matching,
    ICrmContactService contacts,
    IMarketingConfirmationService confirmation,
    ICrmContactIndex contactIndex,
    IOptions<GdprOptions> gdprOptions,
    IOptions<CrmOptions> crmOptions,
    TimeProvider timeProvider,
    ILogger<WelcomeConsentService> logger) : IWelcomeConsentService
{
    /// <summary>Properties kept in <see cref="GuestCrmProfile.PropertyIdsJson"/>: the column holds 2,000 characters, 39 for each id.</summary>
    private const int MaxPropertyIds = 50;

    public async Task<WelcomePage?> GetPageAsync(string token, CancellationToken cancellationToken = default)
    {
        var target = await FindAsync(token, cancellationToken);
        return target is null
            ? null
            : new WelcomePage(target.OrgName, target.PropertyName, GdprOptions.Normalize(gdprOptions.Value.MarketingConsentVersion));
    }

    public async Task SubmitAsync(
        string token,
        WelcomeConsentInput input,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var target = await FindAsync(token, cancellationToken)
            ?? throw new NotFoundException("Welcome link not found")
            {
                Code = WelcomeConsentErrorCodes.LinkInvalid,
                MessageKey = "WelcomeLinkInvalid",
            };

        // Adults only, nothing preselected, at least one channel.
        if (!input.AdultConfirmed)
            throw new DomainRuleException(WelcomeConsentErrorCodes.AdultRequired, "WelcomeAdultRequired");

        if (!input.ConsentEmail && !input.ConsentWhatsApp)
            throw new DomainRuleException(WelcomeConsentErrorCodes.ChannelRequired, "WelcomeChannelRequired");

        // The address of each ticked channel, in the form the CRM stores.
        string? email = null;
        if (input.ConsentEmail && !CrmNormalizer.TryNormalizeEmail(input.Email, out email))
            throw new DomainRuleException(WelcomeConsentErrorCodes.EmailInvalid, "WelcomeEmailInvalid");

        string? phone = null;
        if (input.ConsentWhatsApp)
        {
            phone = CrmNormalizer.NormalizePhone(input.Phone, crmOptions.Value.DefaultCallingCode)
                ?? throw new DomainRuleException(WelcomeConsentErrorCodes.PhoneInvalid, "WelcomePhoneInvalid");
        }

        // The text must have a configured version, and a CRM that can store the contact; and the version sent must be the current one.
        var version = GdprOptions.Normalize(gdprOptions.Value.MarketingConsentVersion);
        if (version is null || !contactIndex.IsConfigured)
            throw new DomainRuleException(WelcomeConsentErrorCodes.ConsentUnavailable, "WelcomeConsentUnavailable");

        if (!string.Equals(GdprOptions.Normalize(input.ConsentVersion), version, StringComparison.Ordinal))
            throw new DomainConflictException(WelcomeConsentErrorCodes.ConsentOutdated, "WelcomeConsentOutdated");

        var language = string.Equals(input.Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var evidenceIp = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress;

        var profile = await matching.FindOrCreateLeadProfileAsync(target.OrgId, email, phone, null, language, cancellationToken);
        await AddPropertyAsync(profile, target.PropertyId, now, cancellationToken);

        GuestContact? emailContact = null;
        if (email is not null)
        {
            emailContact = await contacts.RecordConsentAsync(
                new CrmConsentInput(
                    target.OrgId,
                    GuestContactChannel.Email,
                    email,
                    GuestContactOrigin.WelcomePage,
                    version,
                    GuestConsentSource.WelcomePage,
                    evidenceIp,
                    now,
                    language,
                    GuestCrmProfileId: profile.Id),
                cancellationToken);
        }

        if (phone is not null)
        {
            await contacts.RecordConsentAsync(
                new CrmConsentInput(
                    target.OrgId,
                    GuestContactChannel.WhatsApp,
                    phone,
                    GuestContactOrigin.WelcomePage,
                    version,
                    GuestConsentSource.WelcomePage,
                    evidenceIp,
                    now,
                    language,
                    GuestCrmProfileId: profile.Id),
                cancellationToken);
        }

        // The double opt-in e-mail; what it did (sent, nothing to send, suppressed, too soon) is deliberately not part of the answer.
        if (emailContact is not null)
            _ = await confirmation.SendConfirmationAsync(target.OrgId, emailContact.Id, cancellationToken);

        logger.LogInformation(
            "Welcome page consent recorded for property {PropertyId} of org {OrgId} (profile {ProfileId})",
            target.PropertyId,
            target.OrgId,
            profile.Id);
    }

    /// <summary>
    /// The property of an active link: a token that is unknown, revoked, too long to be one, of a deleted property or of an org that is not
    /// active is <c>null</c>, all the same.
    /// </summary>
    private async Task<WelcomeTarget?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > PropertyWelcomeLink.TokenMaxLength)
            return null;

        var link = await db.PropertyWelcomeLinks.AsNoTracking()
            .Where(l => l.Token == token && l.RevokedAt == null)
            .Select(l => new { l.OrgId, l.PropertyId })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null)
            return null;

        // The soft-delete filter hides a deleted property.
        var property = await db.Properties.AsNoTracking()
            .Where(p => p.Id == link.PropertyId && p.OrgId == link.OrgId)
            .Select(p => new { p.Name })
            .FirstOrDefaultAsync(cancellationToken);
        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == link.OrgId && o.IsActive)
            .Select(o => new { o.DisplayName, o.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null || org is null)
            return null;

        return new WelcomeTarget(
            link.OrgId,
            link.PropertyId,
            string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName,
            property.Name);
    }

    /// <summary>
    /// Adds the property of the link to the properties of the profile (the column is a JSON array of ids, the form the profile sync writes: sorted,
    /// at most <see cref="MaxPropertyIds"/>), so that the host of that property sees the lead. Nothing changes when it is already there.
    /// </summary>
    private async Task AddPropertyAsync(GuestCrmProfile profile, Guid propertyId, DateTime now, CancellationToken cancellationToken)
    {
        var ids = ParseIds(profile.PropertyIdsJson);
        if (ids.Contains(propertyId) || ids.Count >= MaxPropertyIds)
            return;

        ids.Add(propertyId);
        ids.Sort();
        profile.PropertyIdsJson = JsonSerializer.Serialize(ids);
        profile.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<Guid> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record WelcomeTarget(Guid OrgId, Guid PropertyId, string OrgName, string PropertyName);
}
