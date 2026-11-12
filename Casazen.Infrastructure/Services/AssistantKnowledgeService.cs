using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAssistantKnowledgeService"/>
/// <remarks>
/// <para><b>Tenant and reach.</b> Every query names the organization on top of the tenant filter of the context. An entry is looked up together with the property it was
/// asked through (<see cref="FindEntryAsync"/>): an id of another property's entry, of an organization-wide one asked through a property, or of a property's one asked
/// through the organization's routes, is 404 <c>assistant_entry_not_found</c>, the same as an id that does not exist. A property that is not the organization's is 404
/// <c>property_not_found</c>.</para>
/// <para><b>The text is the host's and is never logged.</b> It is encrypted at rest by the converter of the column; the log lines carry ids. Every change grows the
/// version of the instructions in the same save (<see cref="IAssistantRulesService.BumpVersionAsync"/>), and only when something changed.</para>
/// </remarks>
public sealed class AssistantKnowledgeService(
    AppDbContext db,
    IAssistantRulesService rules,
    ILogger<AssistantKnowledgeService> logger,
    TimeProvider? timeProvider = null) : IAssistantKnowledgeService
{
    /// <summary>Stable code of the 404 of an entry that is not the one of the property (or organization) it was asked through.</summary>
    public const string EntryNotFoundCode = "assistant_entry_not_found";

    /// <summary>Stable code of the 422 of an entry with no topic or kind, no text or too long a text, a FAQ with no question, or a language that is not a code.</summary>
    public const string EntryInvalidCode = "assistant_entry_invalid";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AssistantKnowledgeView> GetViewAsync(Guid orgId, Guid propertyId, string? language, CancellationToken ct = default)
    {
        var property = await FindPropertyAsync(orgId, propertyId, ct);
        var resolved = ListingKnowledgeBuilder.ResolveLanguage(language);
        var facts = ListingKnowledgeBuilder.Build(property, SitePropertyProfileJson.Parse(property.SiteProfileJson), resolved);

        // The entries of the property and, under them, the organization's: the ones that apply to it.
        var entries = await db.PropertyKnowledgeEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && (e.PropertyId == propertyId || e.PropertyId == null))
            .OrderBy(e => e.Topic)
            .ThenBy(e => e.PropertyId == null ? 1 : 0)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var topicsWithData = facts.Select(fact => fact.Topic).Concat(entries.Where(e => e.IsActive).Select(e => e.Topic));
        return new AssistantKnowledgeView(
            propertyId,
            resolved,
            facts,
            [.. entries.Select(entry => ItemOf(entry, inherited: entry.PropertyId is null))],
            ListingKnowledgeBuilder.TopicsWithoutData(topicsWithData));
    }

    public async Task<IReadOnlyList<KnowledgeEntryItem>> ListOrganizationEntriesAsync(Guid orgId, CancellationToken ct = default)
    {
        var entries = await db.PropertyKnowledgeEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PropertyId == null)
            .OrderBy(e => e.Topic)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);
        return [.. entries.Select(entry => ItemOf(entry, inherited: false))];
    }

    public async Task<KnowledgeEntryItem> CreateEntryAsync(
        Guid orgId,
        Guid? propertyId,
        KnowledgeEntryInput entry,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var clean = Validate(entry);
        if (propertyId is { } id)
            await AssistantStore.EnsurePropertyAsync(db, orgId, id, ct);

        var now = Now();
        var created = new PropertyKnowledgeEntry
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Topic = clean.Topic,
            Kind = clean.Kind,
            Source = KnowledgeSource.Custom,
            Title = clean.Title,
            Text = clean.Text,
            Language = clean.Language,
            IsActive = clean.IsActive,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PropertyKnowledgeEntries.Add(created);
        await rules.BumpVersionAsync(orgId, ct);
        await AssistantStore.SaveAsync(db, ct);

        logger.LogInformation("Assistant knowledge entry {EntryId} of org {OrgId} created", created.Id, orgId);
        return ItemOf(created, inherited: false);
    }

    public async Task<KnowledgeEntryItem> UpdateEntryAsync(
        Guid orgId,
        Guid? propertyId,
        Guid entryId,
        KnowledgeEntryInput entry,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var clean = Validate(entry);
        var stored = await FindEntryAsync(orgId, propertyId, entryId, ct) ?? throw EntryNotFound();

        var changed = stored.Topic != clean.Topic
                      || stored.Kind != clean.Kind
                      || stored.Title != clean.Title
                      || stored.Text != clean.Text
                      || stored.Language != clean.Language
                      || stored.IsActive != clean.IsActive;
        if (changed)
        {
            // The source and the lesson an entry came from stay: a person may edit what a lesson taught, and it is still the lesson's.
            stored.Topic = clean.Topic;
            stored.Kind = clean.Kind;
            stored.Title = clean.Title;
            stored.Text = clean.Text;
            stored.Language = clean.Language;
            stored.IsActive = clean.IsActive;
            stored.UpdatedAt = Now();
            await rules.BumpVersionAsync(orgId, ct);
            await AssistantStore.SaveAsync(db, ct);
            logger.LogInformation("Assistant knowledge entry {EntryId} of org {OrgId} changed", stored.Id, orgId);
        }

        return ItemOf(stored, inherited: false);
    }

    public async Task DeleteEntryAsync(Guid orgId, Guid? propertyId, Guid entryId, CancellationToken ct = default)
    {
        var stored = await FindEntryAsync(orgId, propertyId, entryId, ct) ?? throw EntryNotFound();

        db.PropertyKnowledgeEntries.Remove(stored);
        await rules.BumpVersionAsync(orgId, ct);
        await AssistantStore.SaveAsync(db, ct);

        logger.LogInformation("Assistant knowledge entry {EntryId} of org {OrgId} deleted", entryId, orgId);
    }

    public async Task<IReadOnlyList<RetrievedFact>> RetrieveAsync(
        Guid orgId,
        Guid propertyId,
        IReadOnlyCollection<KnowledgeTopic> topics,
        string? language,
        CancellationToken ct = default)
    {
        // No topics is no facts, never «all of them».
        if (topics is null || topics.Count == 0)
            return [];

        var wanted = topics.Distinct().ToList();
        var property = await FindPropertyAsync(orgId, propertyId, ct);
        var resolved = ListingKnowledgeBuilder.ResolveLanguage(language);

        var facts = new List<(int Rank, RetrievedFact Fact)>();
        foreach (var fact in ListingKnowledgeBuilder.Build(property, SitePropertyProfileJson.Parse(property.SiteProfileJson), resolved))
        {
            if (wanted.Contains(fact.Topic))
                facts.Add((0, new RetrievedFact(fact.Topic, fact.Text, fact.SourceField, null, false)));
        }

        var entries = await db.PropertyKnowledgeEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.IsActive && wanted.Contains(e.Topic) && (e.PropertyId == propertyId || e.PropertyId == null))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        // An organization-wide entry applies unless the property has an entry of its own with the same topic and kind.
        var own = entries.Where(e => e.PropertyId == propertyId).ToList();
        var ownKeys = own.Select(e => (e.Topic, e.Kind)).ToHashSet();
        var inherited = entries.Where(e => e.PropertyId is null && !ownKeys.Contains((e.Topic, e.Kind)));

        foreach (var topic in own.Concat(inherited).GroupBy(e => e.Topic))
        {
            // The entries in the language asked (or in any), and only when a topic has none of those, the ones written in another language.
            var inLanguage = topic.Where(e => e.Language is null || e.Language.Equals(resolved, StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = inLanguage.Count > 0 ? inLanguage : topic.ToList();
            foreach (var entry in chosen)
            {
                var isInherited = entry.PropertyId is null;
                facts.Add((isInherited ? 2 : 1, new RetrievedFact(entry.Topic, entry.Text, null, entry.Id, isInherited)));
            }
        }

        return [.. facts.OrderBy(item => (int)item.Fact.Topic).ThenBy(item => item.Rank).Select(item => item.Fact)];
    }

    /// <summary>The property of the organization, whole (the facts are built from it); 404 <c>property_not_found</c> otherwise.</summary>
    private async Task<Property> FindPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgId && p.Id == propertyId, ct)
        ?? throw AssistantStore.PropertyNotFound();

    /// <summary>The entry asked for through its property (<c>null</c>: through the organization): tracked, so a change is saved with the new version.</summary>
    private Task<PropertyKnowledgeEntry?> FindEntryAsync(Guid orgId, Guid? propertyId, Guid entryId, CancellationToken ct) =>
        propertyId is { } id
            ? db.PropertyKnowledgeEntries.FirstOrDefaultAsync(e => e.OrgId == orgId && e.Id == entryId && e.PropertyId == id, ct)
            : db.PropertyKnowledgeEntries.FirstOrDefaultAsync(e => e.OrgId == orgId && e.Id == entryId && e.PropertyId == null, ct);

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static CleanEntry Validate(KnowledgeEntryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Topic is not { } topic || !Enum.IsDefined(topic) || input.Kind is not { } kind || !Enum.IsDefined(kind))
            throw Invalid();

        var text = GuestMessageRules.NormalizeText(input.Text);
        if (text is null || text.Length > PropertyKnowledgeEntry.TextMaxLength)
            throw Invalid();

        // The question of a FAQ is one line.
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            title = null;
        if (title is not null && (title.Length > PropertyKnowledgeEntry.TitleMaxLength || title.Any(char.IsControl)))
            throw Invalid();

        if (kind == KnowledgeEntryKind.Faq && title is null)
            throw Invalid();

        // A language is a two-letter lower-case code, or none (any language).
        var language = string.IsNullOrWhiteSpace(input.Language) ? null : input.Language;
        if (language is not null && !(language.Length == 2 && language.All(c => c is >= 'a' and <= 'z')))
            throw Invalid();

        return new CleanEntry(topic, kind, title, text, language, input.IsActive ?? true);
    }

    private static KnowledgeEntryItem ItemOf(PropertyKnowledgeEntry entry, bool inherited) => new(
        entry.Id,
        entry.PropertyId,
        entry.Topic,
        entry.Kind,
        entry.Source,
        entry.Title,
        entry.Text,
        entry.Language,
        entry.LessonId,
        entry.IsActive,
        inherited,
        entry.CreatedAt,
        entry.UpdatedAt);

    private static NotFoundException EntryNotFound() =>
        new("The knowledge entry was not found") { Code = EntryNotFoundCode, MessageKey = "AssistantEntryNotFound" };

    private static DomainRuleException Invalid() => new(EntryInvalidCode, "AssistantEntryInvalid");

    private sealed record CleanEntry(KnowledgeTopic Topic, KnowledgeEntryKind Kind, string? Title, string Text, string? Language, bool IsActive);
}
