namespace Casazen.Core.Entities.Enums;

public enum SeoContentStatus
{
    Generated = 0,

    Manual = 5,

    AiProviderNotConfigured = 1,

    EmptyOutput = 2,

    InvalidOutput = 3,

    Placeholder = 4,
}

public static class SeoContentStatusExtensions
{
    public static bool IsPublishable(this SeoContentStatus status) =>
        status is SeoContentStatus.Generated or SeoContentStatus.Manual;
}
