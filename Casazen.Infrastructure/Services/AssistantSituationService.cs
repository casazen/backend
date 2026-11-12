using System.Globalization;
using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAssistantSituationService"/>
/// <remarks>
/// <para><b>Tenant.</b> Every query names the organization on top of the tenant filter of the context, so the rows of another organization are never read, changed or removed; a
/// property that is not the organization's is 404 <c>property_not_found</c> (<see cref="AssistantStore.EnsurePropertyAsync"/>). The rows of a property are read only together with
/// the organization's own, never the rows of another property.</para>
/// <para><b>Checked first.</b> The mode (<see cref="AssistantModeRules"/>), the template and the source are validated before anything is read or written. A template is checked
/// with <see cref="MessageTemplateRenderer"/>: its variables must be known, and it is rendered once with sample values, <b>real ones</b> (dates and times of a stay), so a text that
/// would break when it is filled in is refused when it is saved and not in front of a guest.</para>
/// <para><b>One unit of work.</b> The change and the growth of the version of the instructions are one save, and only a change that changes something grows it. The template is never
/// logged: the log lines carry ids and the situation code.</para>
/// </remarks>
public sealed class AssistantSituationService(
    AppDbContext db,
    IAssistantRulesService rules,
    ILogger<AssistantSituationService> logger,
    TimeProvider? timeProvider = null) : IAssistantSituationService
{
    /// <summary>Stable code of the 422 of a template that is too long, or breaks when it is filled in, or uses a variable that does not exist.</summary>
    public const string TemplateInvalidCode = "assistant_template_invalid";

    /// <summary>Stable code of the 422 of a source topic that is none of the topics.</summary>
    public const string SourceInvalidCode = "assistant_situation_invalid";

    /// <summary>Stable code of the 404 of a situation that is none of the 15.</summary>
    public const string SituationNotFoundCode = "assistant_situation_not_found";

    /// <summary>The values a template is filled in with to check it: a stay with every date and time, and the names of a guest, a property and a host.</summary>
    private static readonly MessageTemplateValues SampleValues = new(
        GuestName: "Maria",
        PropertyName: "Casa Esempio",
        BookingCode: "CZ-0001",
        CheckInDate: new DateOnly(2026, 7, 14),
        CheckOutDate: new DateOnly(2026, 7, 18),
        CheckInTime: new TimeOnly(15, 0),
        CheckOutTime: new TimeOnly(10, 0),
        HostName: "Luca");

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<EffectiveSituation>> ListEffectiveAsync(Guid orgId, Guid? propertyId, CancellationToken ct = default)
    {
        if (propertyId is { } id)
            await AssistantStore.EnsurePropertyAsync(db, orgId, id, ct);

        var rows = await RowsAsync(orgId, propertyId, null, ct);
        return [.. AssistantSituationCatalog.All.Select(info => Merge(info, rows))];
    }

    public async Task<EffectiveSituation> SaveAsync(
        Guid orgId,
        Guid? propertyId,
        AssistantSituation situation,
        AssistantMode mode,
        string? template,
        KnowledgeTopic? source,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Everything is checked before the database is touched.
        if (!Enum.IsDefined(situation))
            throw SituationNotFound();

        var info = AssistantSituationCatalog.Of(situation);
        AssistantModeRules.Validate(situation, mode);
        var cleanTemplate = CleanTemplate(template);
        if (source is { } topic && !Enum.IsDefined(topic))
            throw new DomainRuleException(SourceInvalidCode, "AssistantSituationInvalid");

        if (propertyId is { } id)
            await AssistantStore.EnsurePropertyAsync(db, orgId, id, ct);

        var row = await FindRowAsync(orgId, propertyId, situation, ct);
        var now = Now();
        bool changed;
        if (row is null)
        {
            // A new row starts its trust period now (PO-08: two weeks of draft only, measured from here).
            row = new AssistantSituationSetting
            {
                OrgId = orgId,
                PropertyId = propertyId,
                Situation = situation,
                Mode = mode,
                ResponseTemplate = cleanTemplate,
                ResponseSource = source,
                ModeSince = now,
                UpdatedAt = now,
                UpdatedByUserId = userId,
            };
            db.AssistantSituationSettings.Add(row);
            changed = true;
        }
        else
        {
            changed = row.Mode != mode || row.ResponseTemplate != cleanTemplate || row.ResponseSource != source;
            if (changed)
            {
                // The trust period moves only with the mode: a new text for the answer does not start it again.
                if (row.Mode != mode)
                    row.ModeSince = now;

                row.Mode = mode;
                row.ResponseTemplate = cleanTemplate;
                row.ResponseSource = source;
                row.UpdatedAt = now;
                row.UpdatedByUserId = userId;
            }
        }

        if (changed)
        {
            await rules.BumpVersionAsync(orgId, ct);
            await AssistantStore.SaveAsync(db, ct);
            logger.LogInformation(
                "Assistant situation {Situation} of org {OrgId} saved ({Scope}) by {UserId}, mode {Mode}",
                info.Code,
                orgId,
                propertyId is null ? "organization" : "property " + propertyId,
                userId,
                row.Mode);
        }

        return Merge(info, [row]);
    }

    public async Task DeleteExceptionAsync(Guid orgId, Guid propertyId, AssistantSituation situation, CancellationToken ct = default)
    {
        await AssistantStore.EnsurePropertyAsync(db, orgId, propertyId, ct);

        var row = await FindRowAsync(orgId, propertyId, situation, ct);
        if (row is null)
            return;

        db.AssistantSituationSettings.Remove(row);
        await rules.BumpVersionAsync(orgId, ct);
        await AssistantStore.SaveAsync(db, ct);

        logger.LogInformation("Assistant situation {Situation} exception of property {PropertyId} of org {OrgId} removed", situation, propertyId, orgId);
    }

    public async Task<ResolvedSituation> ResolveAsync(Guid orgId, Guid? propertyId, AssistantSituation situation, CancellationToken ct = default)
    {
        var info = AssistantSituationCatalog.Of(situation);
        var rows = await RowsAsync(orgId, propertyId, situation, ct);

        var effective = Merge(info, rows);
        return new ResolvedSituation(effective.Mode, effective.Template, effective.Source, effective.ModeSince);
    }

    /// <summary>
    /// The rows that can matter for the organization (<paramref name="propertyId"/> null) or for a property: the organization's, and the property's own. Never another property's, never
    /// another organization's.
    /// </summary>
    private async Task<List<AssistantSituationSetting>> RowsAsync(Guid orgId, Guid? propertyId, AssistantSituation? situation, CancellationToken ct)
    {
        var query = db.AssistantSituationSettings.AsNoTracking().Where(s => s.OrgId == orgId);
        query = propertyId is { } id
            ? query.Where(s => s.PropertyId == null || s.PropertyId == id)
            : query.Where(s => s.PropertyId == null);
        if (situation is { } one)
            query = query.Where(s => s.Situation == one);

        return await query.ToListAsync(ct);
    }

    /// <summary>The row of this exact scope and situation, tracked: the one this unit of work already tracks (an added row is not in the database yet), else the stored one.</summary>
    private async Task<AssistantSituationSetting?> FindRowAsync(Guid orgId, Guid? propertyId, AssistantSituation situation, CancellationToken ct)
    {
        var tracked = db.AssistantSituationSettings.Local
            .FirstOrDefault(s => s.OrgId == orgId && s.PropertyId == propertyId && s.Situation == situation);
        if (tracked is not null)
            return tracked;

        return propertyId is { } id
            ? await db.AssistantSituationSettings.FirstOrDefaultAsync(s => s.OrgId == orgId && s.Situation == situation && s.PropertyId == id, ct)
            : await db.AssistantSituationSettings.FirstOrDefaultAsync(s => s.OrgId == orgId && s.Situation == situation && s.PropertyId == null, ct);
    }

    /// <summary>
    /// The effective value of a situation: the row of the property over the row of the organization over the default of the catalog. A row is whole, so what the winner does not say
    /// (a template, a source) is not taken from the one below; the mode is clamped by the rule of the modes. <c>ModeSince</c> is the winner's: <c>null</c> when there is no row (the default),
    /// an unknown start of the trust period, which is never an elapsed one (<see cref="AssistantModeRules.TrustPeriodElapsed"/>): fail-closed.
    /// </summary>
    private static EffectiveSituation Merge(AssistantSituationInfo info, IReadOnlyCollection<AssistantSituationSetting> rows)
    {
        var own = rows.FirstOrDefault(r => r.Situation == info.Situation && r.PropertyId is not null);
        var organization = rows.FirstOrDefault(r => r.Situation == info.Situation && r.PropertyId is null);
        var row = own ?? organization;
        var origin = own is not null
            ? AssistantSettingOrigin.Property
            : organization is not null ? AssistantSettingOrigin.Organization : AssistantSettingOrigin.Default;

        return new EffectiveSituation(
            info.Situation,
            info.Code,
            row is null ? info.DefaultMode : AssistantModeRules.Effective(info.Situation, row.Mode),
            HasTemplate: !string.IsNullOrEmpty(row?.ResponseTemplate),
            Template: row?.ResponseTemplate,
            Source: row?.ResponseSource ?? info.SourceTopic,
            ModeSince: row?.ModeSince,
            origin,
            info.SensitiveCategories,
            info.AllowedModes);
    }

    /// <summary>
    /// The template to save: normalized like a message, <c>null</c> when empty (no template), at most 1,000 characters, only variables that <see cref="MessageTemplateRenderer"/> knows, and
    /// still a valid message once filled in with <see cref="SampleValues"/>. 422 <see cref="TemplateInvalidCode"/>: with the text of the unknown variable when it is one.
    /// </summary>
    private static string? CleanTemplate(string? template)
    {
        var text = GuestMessageRules.NormalizeText(template);
        if (text is null)
            return null;

        if (text.Length > AssistantSituationSetting.TemplateMaxLength)
            throw InvalidTemplate();

        try
        {
            MessageTemplateRenderer.VariablesOf(text);
            MessageTemplateRenderer.Render(text, SampleValues, CultureInfo.InvariantCulture);
        }
        catch (DomainRuleException ex) when (ex.Code == MessageTemplateRenderer.UnknownCode)
        {
            // The same message as the templates of the composer: it names the variable and lists the valid ones.
            throw new DomainRuleException(TemplateInvalidCode, ex.MessageKey, [.. ex.MessageArgs]);
        }
        catch (DomainRuleException)
        {
            throw InvalidTemplate();
        }

        return text;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static DomainRuleException InvalidTemplate() => new(TemplateInvalidCode, "AssistantTemplateInvalid");

    private static NotFoundException SituationNotFound() =>
        new("The situation was not found") { Code = SituationNotFoundCode, MessageKey = "AssistantSituationNotFound" };
}
