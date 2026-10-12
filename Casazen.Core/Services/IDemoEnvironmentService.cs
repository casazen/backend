using Casazen.Core.Demo;

namespace Casazen.Core.Services;

public interface IDemoEnvironmentService
{
    Task<DemoEnvironmentStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<DemoEnvironmentStatus> SetEnabledAsync(bool enabled, string actorUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DemoOrgListItem>> ListOrgsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a valid invented CIN, the documents the region requires and a complete safety checklist on every
    /// short-stay property of the org that is not the showcase draft, then activates compliance so the listing is published.
    /// </summary>
    Task<DemoComplianceResult> ApplyTestComplianceAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the previous showcase of the org and creates it again from <see cref="DemoShowcaseCatalog"/>.
    /// Host properties are removed only when <paramref name="removeUnpublished"/> is set, and only when they are still
    /// pending and have no booking.
    /// </summary>
    Task<DemoResetResult> ResetAsync(
        Guid orgId,
        string? confirmation,
        bool removeUnpublished,
        string actorUserId,
        CancellationToken cancellationToken = default);
}

public sealed record DemoEnvironmentStatus(
    bool Available,
    bool Enabled,
    string TemplateVersion,
    DateTime? LastResetAt,
    int ShowcaseProperties,
    IReadOnlyList<string> Scenarios);

public sealed record DemoOrgListItem(Guid Id, string Name, string Slug);

public sealed record DemoComplianceResult(int PropertiesSeen, int Published, int AlreadyComplete, int Filled);

public sealed record DemoResetResult(int Removed, int Created, int UnpublishedRemoved);
