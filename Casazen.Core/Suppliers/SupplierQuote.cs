using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

public enum SupplierQuoteOutcome
{
    Estimate = 0,

    OnQuote = 1,

    OutsideArea = 2,
}

public enum SupplierQuoteReason
{
    RequiresQuote = 0,

    NoPrice = 1,

    SurfaceOverLimit = 2,

    AmountOverLimit = 3,

    OutsideArea = 4,
}

public enum SupplierQuoteLineKind
{
    Base = 0,

    Supplement = 1,

    Urgent = 2,
}

public static class SupplierQuoteLabels
{
    public const string Urgent = "Urgenza";
}

/// <summary>
/// One line of an estimate: <c>quantity × unitAmountCents = amountCents</c>, always (integer cents, nothing is rounded).
/// </summary>
/// <param name="Kind">Base price, supplement or the surcharge of an urgent request.</param>
/// <param name="Code">The supplement's code; <c>null</c> for the base price and the urgent surcharge.</param>
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
    public bool IsEstimate => Outcome == SupplierQuoteOutcome.Estimate;

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
/// <param name="Urgent">
/// The customer asks for an urgent slot (SP-04c): the estimate gets the surcharge of the service as one more line. Refused (422, field
/// <c>urgent</c>) for a service that does not offer urgent requests. <c>false</c> is the estimate every client got before.
/// </param>
public sealed record SupplierQuoteRequest(
    int? Quantity,
    int? SurfaceSqm,
    IReadOnlyList<SupplierQuoteOption?>? Options,
    string? Comune,
    string? PostalCode,
    bool Urgent = false);

public sealed record SupplierQuoteOption(string? Code, int? Quantity);

public sealed record SupplierQuotePick(SupplierServiceSupplement Supplement, int Quantity);

public sealed record SupplierQuoteChoices(
    int? Quantity,
    int? SurfaceSqm,
    IReadOnlyList<SupplierQuotePick> Picks,
    ComuneTarget? Place,
    string? PostalCode,
    bool Urgent = false);

public static class SupplierQuoteFields
{
    public const string Service = "service";
    public const string Quantity = "quantity";
    public const string SurfaceSqm = "surfaceSqm";
    public const string Options = "options";
    public const string Comune = "comune";
    public const string PostalCode = "postalCode";
    public const string Urgent = "urgent";
}

public static class SupplierQuoteErrors
{
    public const string Invalid = "supplier_quote_invalid";

    public static IReadOnlyList<string> MessageKeys { get; } = ["SupplierQuoteInvalid"];

    public static SupplierQuoteRuleException InvalidFields(IReadOnlyList<string> fields) =>
        new(Invalid, "SupplierQuoteInvalid", fields);
}

public sealed class SupplierQuoteRuleException : DomainRuleException
{
    public SupplierQuoteRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    public IReadOnlyList<string> Fields { get; }
}
