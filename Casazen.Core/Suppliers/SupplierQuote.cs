using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>What a price estimate came to (SP-09, <c>POST api/public/suppliers/{slug}/quote</c>).</summary>
public enum SupplierQuoteOutcome
{
    /// <summary>A total could be computed. It is an estimate: the supplier confirms the price before starting the work.</summary>
    Estimate = 0,

    /// <summary>No total: the customer asks for a quote and the supplier makes the offer (see <see cref="SupplierQuoteReason"/>).</summary>
    OnQuote = 1,

    /// <summary>The place is outside the zones the supplier covers: the customer may ask for a quote and the supplier decides.</summary>
    OutsideArea = 2,
}

/// <summary>Why an estimate has no total. Stable names: the clients write the explanation from them.</summary>
public enum SupplierQuoteReason
{
    /// <summary>The service is on quote (<c>requiresQuote</c>): the price is always the supplier's offer.</summary>
    RequiresQuote = 0,

    /// <summary>The service has no price to start from.</summary>
    NoPrice = 1,

    /// <summary>The surface needs more 30 m² blocks than the supplement allows (<c>max</c>): the supplier prices a bigger home.</summary>
    SurfaceOverLimit = 2,

    /// <summary>The total would pass the highest price the catalog accepts: not an amount to promise on a form.</summary>
    AmountOverLimit = 3,

    /// <summary>The comune is not one of the supplier's zones.</summary>
    OutsideArea = 4,
}

/// <summary>The two kinds of line of an estimate.</summary>
public enum SupplierQuoteLineKind
{
    /// <summary>The base price of the service (the "from" price times the quantity of its unit).</summary>
    Base = 0,

    /// <summary>A supplement of the service.</summary>
    Supplement = 1,
}

/// <summary>
/// One line of an estimate: <c>quantity × unitAmountCents = amountCents</c>, always (integer cents, nothing is rounded).
/// </summary>
/// <param name="Kind">Base price or supplement.</param>
/// <param name="Code">The supplement's code; <c>null</c> for the base price.</param>
/// <param name="Label">What the customer reads: the name of the service or the supplement's label, as the supplier wrote them.</param>
/// <param name="Per">The unit of a supplement (<see cref="SupplierServiceSupplementUnits"/>); <c>null</c> for the base price.</param>
public sealed record SupplierQuoteLine(
    SupplierQuoteLineKind Kind,
    string? Code,
    string Label,
    string? Per,
    int Quantity,
    int UnitAmountCents,
    int AmountCents);

/// <summary>
/// The estimate of a service for what the customer chose (SP-09). A total only for <see cref="SupplierQuoteOutcome.Estimate"/>;
/// otherwise <see cref="TotalCents"/> is <c>null</c>, there are no lines and <see cref="Reason"/> says why. VAT is only the
/// supplier's declaration (decision D4): nothing is added or subtracted, and "VAT included" may be said only when
/// <see cref="PricesIncludeVat"/> is true.
/// </summary>
public sealed record SupplierQuote(
    SupplierQuoteOutcome Outcome,
    SupplierQuoteReason? Reason,
    int? TotalCents,
    IReadOnlyList<SupplierQuoteLine> Lines,
    bool PricesIncludeVat)
{
    /// <summary>True when there is a total, which is an estimate: the supplier confirms the price before starting.</summary>
    public bool IsEstimate => Outcome == SupplierQuoteOutcome.Estimate;

    /// <summary>True when the customer has to ask for a quote: the service is on quote, or the supplier decides for this place.</summary>
    public bool RequiresQuote => Outcome != SupplierQuoteOutcome.Estimate;
}

/// <summary>
/// What the customer sends for an estimate, as read from the request: nothing is checked yet
/// (<see cref="SupplierQuoteCalculator.Validate"/> checks it against the service).
/// </summary>
/// <param name="Quantity">
/// Units of the service's own price: hours (<c>PerHour</c>, default the indicative duration rounded up to whole hours), sets
/// (<c>PerSet</c>, default 1) or square meters (<c>PerSquareMeter</c>, required). A price <c>PerJob</c> has no quantity.
/// </param>
/// <param name="SurfaceSqm">Surface of the home in square meters: what the <c>sqm30</c> supplements count.</param>
/// <param name="Options">The supplements picked (by code). A <c>sqm30</c> supplement is not picked: it follows the surface.</param>
/// <param name="Comune">Where the work is: an ISTAT code or the name of the comune. The supplier covers comuni, not postal codes.</param>
/// <param name="PostalCode">Five digits; checked for its shape and echoed, it does not decide the coverage.</param>
public sealed record SupplierQuoteRequest(
    int? Quantity,
    int? SurfaceSqm,
    IReadOnlyList<SupplierQuoteOption?>? Options,
    string? Comune,
    string? PostalCode);

/// <summary>A supplement the customer picked: its <paramref name="Code"/> and how many units (default 1).</summary>
public sealed record SupplierQuoteOption(string? Code, int? Quantity);

/// <summary>A supplement picked and found in the service, with its units.</summary>
public sealed record SupplierQuotePick(SupplierServiceSupplement Supplement, int Quantity);

/// <summary>
/// The customer's choices after <see cref="SupplierQuoteCalculator.Validate"/>: every value checked against the service, the
/// codes resolved to the service's supplements, the place ready for the coverage check.
/// </summary>
/// <param name="Place">The comune to check against the supplier's zones; <c>null</c> when the request named none.</param>
public sealed record SupplierQuoteChoices(
    int? Quantity,
    int? SurfaceSqm,
    IReadOnlyList<SupplierQuotePick> Picks,
    ComuneTarget? Place,
    string? PostalCode);

/// <summary>
/// Names of the fields of a quote request, as the client sends them (what a 422 <c>supplier_quote_invalid</c> lists in
/// <c>fields</c>; an option is <c>options[0].code</c> or <c>options[0].quantity</c>).
/// </summary>
public static class SupplierQuoteFields
{
    public const string Service = "service";
    public const string Quantity = "quantity";
    public const string SurfaceSqm = "surfaceSqm";
    public const string Options = "options";
    public const string Comune = "comune";
    public const string PostalCode = "postalCode";
}

/// <summary>
/// The code (ProblemDetails <c>code</c>) and message key (<c>SharedResources.resx</c>, Italian and English) of the error of
/// the estimate (SP-09). 422, with the fields at fault. An estimate that cannot be given (on quote, outside the zones) is
/// <b>not</b> an error: it is a 200 answer that says so.
/// </summary>
public static class SupplierQuoteErrors
{
    /// <summary>422: a value of the request is not valid for the service (see <see cref="SupplierQuoteRuleException.Fields"/>).</summary>
    public const string Invalid = "supplier_quote_invalid";

    /// <summary>Every <c>SharedResources</c> key the estimate uses (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } = ["SupplierQuoteInvalid"];

    /// <summary>The 422 of a request with values that are not valid; <paramref name="fields"/> are the JSON names of what is wrong.</summary>
    public static SupplierQuoteRuleException InvalidFields(IReadOnlyList<string> fields) =>
        new(Invalid, "SupplierQuoteInvalid", fields);
}

/// <summary>
/// A 422 of the estimate that names the fields at fault: the controller adds them to the response as <c>fields</c>, so the
/// form can mark them.
/// </summary>
public sealed class SupplierQuoteRuleException : DomainRuleException
{
    public SupplierQuoteRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    /// <summary>JSON names of the fields at fault (<c>quantity</c>, <c>options[0].code</c>, <c>postalCode</c>...).</summary>
    public IReadOnlyList<string> Fields { get; }
}
