using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IMessageTemplateService"/>
/// <remarks>
/// <para><b>Tenant.</b> Every query names <see cref="HostScope.OrgId"/> on top of the tenant filter of the context, so a template of another organization is
/// not listed, not changed, not deleted and not rendered: it is a 404 <c>message_template_not_found</c>, the same as one that does not exist.</para>
/// <para><b>Checked when it is written.</b> A template is saved only if its name, its language (one of the 14 of the listing) and its text are valid and its
/// variables are known (<see cref="MessageTemplateRenderer.VariablesOf"/>), so a typo in a variable is found by the host that writes it and not in front of a
/// guest. The unique index on (organization, name, language) is the net under the check of <c>message_template_exists</c>.</para>
/// <para><b>Rendering reads, it does not write.</b> The values come from the booking and the property of the conversation (<see cref="MessageConversationLookup"/>,
/// in the reach of the caller); nothing is stored, and the text is not logged.</para>
/// </remarks>
public sealed class MessageTemplateService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger<MessageTemplateService> logger,
    TimeProvider? timeProvider = null) : IMessageTemplateService
{
    /// <summary>Stable code of the 404 of a template that is not the organization's.</summary>
    public const string NotFoundCode = "message_template_not_found";

    /// <summary>Stable code of the 422 of a template with no valid name, language or text.</summary>
    public const string InvalidCode = "message_template_invalid";

    /// <summary>Stable code of the 409 of a second template with the same name and language.</summary>
    public const string ExistsCode = "message_template_exists";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<MessageTemplateItem>> ListAsync(HostScope scope, bool activeOnly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var templates = db.MessageTemplates.AsNoTracking().Where(t => t.OrgId == scope.OrgId);
        if (activeOnly)
            templates = templates.Where(t => t.IsActive);

        var rows = await templates.OrderBy(t => t.Name).ThenBy(t => t.Language).ToListAsync(ct);
        return rows.Select(ItemOf).ToList();
    }

    public async Task<MessageTemplateItem> CreateAsync(
        HostScope scope,
        string? name,
        string? language,
        string? body,
        bool isActive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (cleanName, cleanLanguage, cleanBody) = Validate(name, language, body);
        if (await ExistsAsync(scope.OrgId, cleanName, cleanLanguage, null, ct))
            throw Exists();

        var now = Now();
        var template = new MessageTemplate
        {
            OrgId = scope.OrgId,
            Name = cleanName,
            Language = cleanLanguage,
            Body = cleanBody,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MessageTemplates.Add(template);
        await SaveAsync(ct);

        logger.LogInformation("Message template {TemplateId} of org {OrgId} created", template.Id, scope.OrgId);
        return ItemOf(template);
    }

    public async Task<MessageTemplateItem> UpdateAsync(
        HostScope scope,
        Guid id,
        string? name,
        string? language,
        string? body,
        bool isActive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var template = await db.MessageTemplates.FirstOrDefaultAsync(t => t.OrgId == scope.OrgId && t.Id == id, ct)
            ?? throw NotFound();

        var (cleanName, cleanLanguage, cleanBody) = Validate(name, language, body);
        if (await ExistsAsync(scope.OrgId, cleanName, cleanLanguage, id, ct))
            throw Exists();

        template.Name = cleanName;
        template.Language = cleanLanguage;
        template.Body = cleanBody;
        template.IsActive = isActive;
        template.UpdatedAt = Now();
        await SaveAsync(ct);

        logger.LogInformation("Message template {TemplateId} of org {OrgId} changed", template.Id, scope.OrgId);
        return ItemOf(template);
    }

    public async Task DeleteAsync(HostScope scope, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var template = await db.MessageTemplates.FirstOrDefaultAsync(t => t.OrgId == scope.OrgId && t.Id == id, ct)
            ?? throw NotFound();

        db.MessageTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Message template {TemplateId} of org {OrgId} deleted", id, scope.OrgId);
    }

    public async Task<string> RenderAsync(HostScope scope, Guid id, MessageThreadKey threadKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The template of this organization first, then the conversation in the reach of the caller: neither says anything about the other.
        var template = await db.MessageTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.OrgId == scope.OrgId && t.Id == id, ct)
            ?? throw NotFound();
        var conversation = await MessageConversationLookup.FindAsync(db, scope, threadKey, ct);

        var values = new MessageTemplateValues(
            conversation.GuestFirstNameForTemplates,
            conversation.PropertyName,
            conversation.BookingCode is null ? null : BookingCodes.Format(conversation.BookingCode),
            conversation.CheckInDate is { } checkIn ? RomeCalendar.DateInRome(checkIn) : null,
            conversation.CheckOutDate is { } checkOut ? RomeCalendar.DateInRome(checkOut) : null,
            conversation.CheckInFrom,
            conversation.CheckOutBy,
            conversation.HostNameForTemplates);
        return MessageTemplateRenderer.Render(template.Body, values, CultureOf(template.Language));
    }

    public async Task<IReadOnlyList<ChannelTemplateItem>> ChannelTemplatesAsync(HostScope scope, MessageThreadKey threadKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var conversation = await MessageConversationLookup.FindAsync(db, scope, threadKey, ct);

        // Only a conversation of a channel has templates on the channel manager.
        if (conversation.OtaThreadId is null || conversation.OctorateThreadId is null || conversation.OctorateAccommodationId is null)
            return [];

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            throw OctorateAuthException.NotConnected();

        OctorateChatTemplate[] found;
        using (orgScope.Use(conversation.OrgId))
            found = await client.GetChatTemplatesAsync(conversation.OctorateAccommodationId, conversation.OctorateThreadId, ct);

        return found.Select(template => new ChannelTemplateItem(template.Name, template.Language, template.Text)).ToList();
    }

    /// <summary>The culture of the language of a template (how its dates are written); the invariant one for a code the platform has no culture for.</summary>
    internal static CultureInfo CultureOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static (string Name, string Language, string Body) Validate(string? name, string? language, string? body)
    {
        var cleanName = name?.Trim();
        var cleanLanguage = ListingLanguages.Normalize(language);
        var cleanBody = GuestMessageRules.NormalizeText(body);
        if (string.IsNullOrEmpty(cleanName)
            || cleanName.Length > MessageTemplate.NameMaxLength
            || cleanName.Any(char.IsControl)
            || cleanLanguage is null
            || cleanBody is null
            || cleanBody.Length > MessageTemplate.BodyMaxLength)
        {
            throw new DomainRuleException(InvalidCode, "MessageTemplateInvalid");
        }

        // A name that is not one of the variables, or a brace that belongs to none: 422 template_variable_unknown, now and not in front of a guest.
        MessageTemplateRenderer.VariablesOf(cleanBody);
        return (cleanName, cleanLanguage, cleanBody);
    }

    private Task<bool> ExistsAsync(Guid orgId, string name, string language, Guid? exceptId, CancellationToken ct) =>
        db.MessageTemplates.AnyAsync(
            t => t.OrgId == orgId && t.Name == name && t.Language == language && (exceptId == null || t.Id != exceptId),
            ct);

    /// <summary>Saves; the unique index on (organization, name, language) answers the same 409 when two saves of the same template meet.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            throw Exists();
        }
    }

    private static MessageTemplateItem ItemOf(MessageTemplate template) => new(
        template.Id,
        template.Name,
        template.Language,
        template.Body,
        template.IsActive,
        VariablesOf(template.Body),
        template.UpdatedAt);

    /// <summary>The variables a stored body uses; none for a body that does not parse (a row written some other way must not break the list).</summary>
    private static IReadOnlyList<string> VariablesOf(string body)
    {
        try
        {
            return MessageTemplateRenderer.VariablesOf(body);
        }
        catch (DomainRuleException)
        {
            return [];
        }
    }

    private static NotFoundException NotFound() =>
        new("The message template was not found") { Code = NotFoundCode, MessageKey = "MessageTemplateNotFound" };

    private static DomainConflictException Exists() => new(ExistsCode, "MessageTemplateExists");

    private DateTime Now()
    {
        var utc = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }
}
