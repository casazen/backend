using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What the assistant may say about a property (E05/F05/US01/T02, NF-66): the facts built from the listing the host already entered (never stored, so they never go
/// stale: <see cref="ListingKnowledgeBuilder"/>), the custom entries a person wrote and the ones a lesson taught it, of the property and of the whole organization
/// (the organization-wide ones apply to every property that has no entry of its own with the same topic and kind), and the topics that have no data at all, which the
/// wizard turns into questions.
/// </summary>
/// <remarks>
/// <para><b>Tenant and reach.</b> Every method names the organization and every query filters by it, on top of the tenant filter of the context. An entry of a property
/// is read and changed only through that property (the web layer checks that the caller reaches it): an id of an entry of another property, or of the organization,
/// given with the wrong property is 404 <c>assistant_entry_not_found</c>, the same as one that does not exist. An organization-wide entry is changed only through the
/// organization's routes. The services never check roles: the web layer decides who may call.</para>
/// <para><b>The text is the host's.</b> It can hold a Wi-Fi password: it is encrypted at rest, returned only to the callers who read the knowledge and to the draft
/// pipeline (<see cref="RetrieveAsync"/>), and never written to a log. Every change grows the version of the instructions
/// (<see cref="IAssistantRulesService.BumpVersionAsync"/>) in the same transaction.</para>
/// </remarks>
public interface IAssistantKnowledgeService
{
    Task<AssistantKnowledgeView> GetViewAsync(Guid orgId, Guid propertyId, string? language, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeEntryItem>> ListOrganizationEntriesAsync(Guid orgId, CancellationToken ct = default);

    Task<KnowledgeEntryItem> CreateEntryAsync(Guid orgId, Guid? propertyId, KnowledgeEntryInput entry, string userId, CancellationToken ct = default);

    /// <summary>
    /// Changes an entry (an entry that came from a lesson too: a person may edit it or switch it off). The entry has to be the one of <paramref name="propertyId"/> (null:
    /// the organization's): 404 <c>assistant_entry_not_found</c> otherwise; 422 <c>assistant_entry_invalid</c>.
    /// </summary>
    Task<KnowledgeEntryItem> UpdateEntryAsync(Guid orgId, Guid? propertyId, Guid entryId, KnowledgeEntryInput entry, string userId, CancellationToken ct = default);

    Task DeleteEntryAsync(Guid orgId, Guid? propertyId, Guid entryId, CancellationToken ct = default);

    /// <summary>
    /// <b>Internal, for the draft pipeline.</b> The facts that answer <paramref name="topics"/> for a property, each with the reference of its source: the facts of the
    /// listing and the active entries of the property and of the organization. An organization-wide entry applies unless the property has an active entry of its own
    /// with the same topic and kind; inactive entries are left out. An entry written in another language is used only when none of the topic is in <paramref name="language"/>
    /// or has no language. <b>No topics is no facts</b>, never «all of them». The text goes to the pipeline and nowhere else: it is never logged.
    /// </summary>
    Task<IReadOnlyList<RetrievedFact>> RetrieveAsync(
        Guid orgId,
        Guid propertyId,
        IReadOnlyCollection<KnowledgeTopic> topics,
        string? language,
        CancellationToken ct = default);
}

public sealed record AssistantKnowledgeView(
    Guid PropertyId,
    string Language,
    IReadOnlyList<ListingFact> Facts,
    IReadOnlyList<KnowledgeEntryItem> Entries,
    IReadOnlyList<KnowledgeTopic> TopicsWithoutData);

public sealed record KnowledgeEntryItem(
    Guid Id,
    Guid? PropertyId,
    KnowledgeTopic Topic,
    KnowledgeEntryKind Kind,
    KnowledgeSource Source,
    string? Title,
    string Text,
    string? Language,
    Guid? LessonId,
    bool IsActive,
    bool Inherited,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record KnowledgeEntryInput(
    KnowledgeTopic? Topic,
    KnowledgeEntryKind? Kind,
    string? Title,
    string? Text,
    string? Language,
    bool? IsActive);

public sealed record RetrievedFact(KnowledgeTopic Topic, string Text, string? SourceField, Guid? EntryId, bool Inherited)
{
    public string SourceRef => EntryId is { } id ? $"entry:{id}" : $"listing:{SourceField}";
}
