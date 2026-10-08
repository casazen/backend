namespace Casazen.Core.Suppliers;

/// <summary>
/// Limits and defaults of the public read side of the supplier showcase (SP-09: <c>GET api/public/suppliers/{slug}</c> with its
/// services, <c>…/services</c>, <c>…/slots</c> and <c>POST …/quote</c>). Technical bounds that keep an anonymous read small and
/// cheap, and the few rules of the estimate that the gap report leaves to the estimate itself: they live in one place so the
/// service, the controller, the DTO attributes and the docs cannot drift apart.
/// </summary>
public static class PublicShowcaseLimits
{
    // ─── Slots ───────────────────────────────────────────────────────────────────

    /// <summary>Days a slots read covers when the client does not say: the 14 days the showcase has always shown.</summary>
    public const int SlotsDefaultDays = 14;

    /// <summary>Most days one slots read can cover: the bound of the supplier's own calendar read (a longer horizon is paged).</summary>
    public const int SlotsMaxDays = SupplierAgendaLimits.MaxCalendarDays;

    /// <summary>
    /// How long the slots of a service are served again without being recomputed (30 s, per replica). A slot shown is <b>not a
    /// promise</b>: the booking recomputes under the supplier's calendar lock (SP-10).
    /// </summary>
    public static readonly TimeSpan SlotsCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Plans the slots cache holds at the same time. The key is a real service of a real supplier (never a value the client
    /// makes up), so the number of keys follows the catalogs; this is only the last line against a runaway.
    /// </summary>
    public const int SlotsCacheCapacity = 2_000;

    // ─── Response time ───────────────────────────────────────────────────────────

    /// <summary>
    /// Requests a supplier must have answered (taken) in the last <c>SupplierEarningsSummary.ResponseWindowDays</c> days before
    /// the public page shows its typical response time. With fewer the time is simply not shown: it is never written by hand.
    /// </summary>
    public const int ResponseTimeMinSamples = 5;

    /// <summary>Most requests the median is made of: the latest ones taken in the window (a public page is read often, so the read is bounded).</summary>
    public const int ResponseTimeMaxSamples = 500;

    // ─── Estimate ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Surface, in square meters, the base price of a service already covers: a supplement priced <c>sqm30</c> counts the 30 m²
    /// blocks <b>above</b> it (the demo's "oltre 60 m², ogni 30 m²"). A product default until the catalog can say it per service.
    /// </summary>
    public const int QuoteIncludedSurfaceSqm = 60;

    /// <summary>Size, in square meters, of the unit of a <c>sqm30</c> supplement; a started block counts as a whole one.</summary>
    public const int QuoteSurfaceStepSqm = 30;

    /// <summary>Most units of the main price (hours, sets, square meters) one estimate takes.</summary>
    public const int QuoteMaxQuantity = 1_000;

    /// <summary>Largest surface, in square meters, an estimate takes: a guard against a typo, not a rule of the business.</summary>
    public const int QuoteMaxSurfaceSqm = 10_000;

    /// <summary>Supplements one estimate can pick: all the ones a service can have.</summary>
    public const int QuoteMaxOptions = SupplierServiceCatalogLimits.MaxSupplements;

    /// <summary>Longest comune (name or ISTAT code) an estimate takes.</summary>
    public const int QuoteComuneMaxLength = 100;

    /// <summary>Longest postal code (the field is checked to be five digits; the limit stops an oversized body).</summary>
    public const int QuotePostalCodeMaxLength = 10;

    /// <summary>Largest body of an estimate request, in bytes: a handful of short fields, never a document.</summary>
    public const int QuoteMaxBodyBytes = 8 * 1024;

    /// <summary>Longest slug the public reads look up (the column of the supplier slug is 100): anything longer is not one.</summary>
    public const int SlugMaxLength = 100;
}
