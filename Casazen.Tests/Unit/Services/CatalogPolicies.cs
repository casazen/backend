using Casazen.Core.Entities;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The five short-stay cancellation policies of the catalog with the values the migration seeds
/// (<c>20261008172218_SeedCancellationPolicyCatalog.Data</c>), for the tests of the refund a guest gets when they cancel.
/// A new instance on every call: a test may keep it in a property without sharing the row with another test.
/// </summary>
internal static class CatalogPolicies
{
    public const string Ampia = "ampia";
    public const string Intermedia = "intermedia";
    public const string Contenuta = "contenuta";
    public const string Anticipata = "anticipata";
    public const string NonRimborsabile = "non_rimborsabile";

    public static CancellationPolicy Get(string slug) => slug switch
    {
        Ampia => New(slug, "Ampia", fullRefundHours: 24, partialRefundHours: 0, partialRefundPercent: 0m),
        Intermedia => New(slug, "Intermedia", fullRefundHours: 120, partialRefundHours: 0, partialRefundPercent: 50m),
        Contenuta => New(slug, "Contenuta", fullRefundHours: 336, partialRefundHours: 168, partialRefundPercent: 50m),
        Anticipata => New(slug, "Anticipata", fullRefundHours: 720, partialRefundHours: 168, partialRefundPercent: 50m),
        NonRimborsabile => New(slug, "Non rimborsabile", fullRefundHours: 0, partialRefundHours: 0, partialRefundPercent: 0m),
        _ => throw new ArgumentOutOfRangeException(nameof(slug), slug, "Not a policy of the catalog."),
    };

    private static CancellationPolicy New(
        string slug,
        string name,
        int fullRefundHours,
        int partialRefundHours,
        decimal partialRefundPercent) => new()
        {
            // Unique per instance (UIX_CancellationPolicies_Slug), at most 49 characters (the column holds 50).
            Slug = $"{slug}-{Guid.NewGuid():N}",
            Name = name,
            Description = $"Test copy of the catalog policy {name}.",
            FullRefundHours = fullRefundHours,
            PartialRefundHours = partialRefundHours,
            PartialRefundPercent = partialRefundPercent,
            GraceBookingDaysBeforeCheckin = 7,
            GraceWindowHours = 24,
        };
}
