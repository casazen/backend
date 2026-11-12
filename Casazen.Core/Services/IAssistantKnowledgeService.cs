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
    /// <summary>
    /// The knowledge base of a property in <paramref name="language"/> (<c>en</c>, Italian for anything else): the facts of the listing, the active and inactive entries of
    /// the property and of the organization (the organization-wide ones flagged <c>inherited</c>) and the topics with no fact and no active entry. 404
    /// <c>property_not_found</c> for a property that is not the organization's.
    /// </summary>
    Task<AssistantKnowledgeView> GetViewAsync(Guid orgId, Guid propertyId, string? language, CancellationToken ct = default);

    /// <summary>The organization-wide entries (no property), by topic, active or not.</summary>
    Task<IReadOnlyList<KnowledgeEntryItem>> ListOrganizationEntriesAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Creates an entry of the property, or of the organization with <paramref name="propertyId"/> null. A FAQ needs a title (its question) and a text of 1 to 1,000
    /// characters: 422 <c>assistant_entry_invalid</c>; 404 <c>property_not_found</c>.
    /// </summary>
    Task<KnowledgeEntryItem> CreateEntryAsync(Guid orgId, Guid? propertyId, KnowledgeEntryInput entry, string userId, CancellationToken ct = default);

    /// <summary>
    /// Changes an entry (an entry that came from a lesson too: a person may edit it or switch it off). The entry has to be the one of <paramref name="propertyId"/> (null:
    /// the organization's): 404 <c>assistant_entry_not_found</c> otherwise; 422 <c>assistant_entry_invalid</c>.
    /// </summary>
    Task<KnowledgeEntryItem> UpdateEntryAsync(Guid orgId, Guid? propertyId, Guid entryId, KnowledgeEntryInput entry, string userId, CancellationToken ct = default);

    /// <summary>Deletes an entry of <paramref name="propertyId"/> (null: of the organization). 404 <c>assistant_entry_not_found</c>.</summary>
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

/// <summary>The knowledge base of a property as the screen reads it.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Language">The language the facts were built in: <c>it</c> or <c>en</c>.</param>
/// <param name="Facts">What was read from the listing, each with the field it came from («from your listing»).</param>
/// <param name="Entries">The entries of the property and of the organization (<see cref="KnowledgeEntryItem.Inherited"/>), by topic.</param>
/// <param name="TopicsWithoutData">The topics with no fact and no active entry: the gaps the wizard asks the host about.</param>
public sealed record AssistantKnowledgeView(
    Guid PropertyId,
    string Language,
    IReadOnlyList<ListingFact> Facts,
    IReadOnlyList<KnowledgeEntryItem> Entries,
    IReadOnlyList<KnowledgeTopic> TopicsWithoutData);

/// <summary>An entry of the knowledge base. <paramref name="Inherited"/> is true for an organization-wide entry shown under a property.</summary>
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

/// <summary>An entry as a request gives it: <paramref name="IsActive"/> not given is active. A bad value is a 422 of the service, with its own code.</summary>
public sealed record KnowledgeEntryInput(
    KnowledgeTopic? Topic,
    KnowledgeEntryKind? Kind,
    string? Title,
    string? Text,
    string? Language,
    bool? IsActive);

/// <summary>
/// A fact for the draft pipeline: the <paramref name="Topic"/>, the <paramref name="Text"/>, and where it comes from: the field of the listing
/// (<paramref name="SourceField"/>, an entry has none) or the id of the entry (<paramref name="EntryId"/>, a fact of the listing has none).
/// <paramref name="Inherited"/> is true for an organization-wide entry.
/// </summary>
public sealed record RetrievedFact(KnowledgeTopic Topic, string Text, string? SourceField, Guid? EntryId, bool Inherited)
{
    /// <summary>The reference the decision log keeps (ids and topics, never the text): <c>entry:{id}</c> or <c>listing:{field}</c>.</summary>
    public string SourceRef => EntryId is { } id ? $"entry:{id}" : $"listing:{SourceField}";
}
