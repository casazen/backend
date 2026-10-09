using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

/// <summary>The stable values of <c>ServiceRequest.CancellationReason</c> that are codes and not text a person wrote.</summary>
public static class ServiceRequestCancellationReasons
{
    /// <summary>
    /// The automatic cancellation (decision D8, job <c>service-request-auto-cancel</c>): the supplier did not answer before
    /// <c>ResponseDueAt</c>. Stored with <c>CancelledBy = System</c>; the client translates it, it is not a sentence.
    /// </summary>
    public const string NoResponse = "NoResponse";
}

/// <summary>The kinds of line of <see cref="ServiceRequestPriceLine"/>.</summary>
public static class ServiceRequestPriceLineKinds
{
    /// <summary>The agreed price of the work (the quote, else the estimate).</summary>
    public const string Base = "base";

    /// <summary>An extra the supplier added when it completed the work (a bathroom more, extra linen).</summary>
    public const string Extra = "extra";
}

/// <summary>One line of the price of a completed request (<c>ServiceRequest.PriceLinesJson</c>); the lines add up to the final amount.</summary>
public sealed record ServiceRequestPriceLine(string Kind, string Label, int AmountCents);

/// <summary>An extra the supplier asks for on top of the agreed price when it completes the work.</summary>
public sealed record ServiceRequestExtra(string Label, int AmountCents);

/// <summary>An option (supplement) of the catalog the customer picked, snapshotted on the request (<c>ServiceRequest.OptionsJson</c>).</summary>
public sealed record ServiceRequestOption(string Code, string Label, int AmountCents, string Per, int Quantity);

/// <summary>The codes of the entries of <c>ServiceRequest.OptionsJson</c> that are not a supplement of the price list (SP-10).</summary>
public static class ServiceRequestOptionCodes
{
    /// <summary>
    /// The quantity the customer chose for a price per hour, per set or per square meter (the base of the estimate): the label is
    /// the name of the service, <c>Per</c> the unit, <c>AmountCents</c> the price of one unit, <c>Quantity</c> how many.
    /// </summary>
    public const string Quantity = "quantity";

    /// <summary><c>Per</c> of the quantity of a price per square meter (the supplements have <see cref="SupplierServiceSupplementUnits"/>).</summary>
    public const string SquareMeter = "sqm";
}

/// <summary>A photo of the work in the private bucket (<c>ServiceRequest.WorkPhotosJson</c>): the id is what the download endpoint takes.</summary>
public sealed record ServiceRequestPhoto(Guid Id, string Key, DateTime UploadedAt);

/// <summary>The price of a completed request: the total, and the lines that add up to it.</summary>
public sealed record ServiceRequestFinalPrice(int? FinalAmountCents, IReadOnlyList<ServiceRequestPriceLine> Lines);

/// <summary>
/// The price rules of a service request (SP-04): which amount is the reference of a request, how the final price is made
/// of the agreed price and the extras, and when the final price is too far above the quote (decision D7).
/// </summary>
public static class ServiceRequestPricing
{
    /// <summary>The default tolerance of decision D7: a final amount more than 20 % above the quote needs the customer's confirmation.</summary>
    public const int DefaultTolerancePercent = 20;

    /// <summary>The price the customer was given: what the supplier quoted when it took the request, else the estimate.</summary>
    public static int? ReferenceAmount(int? quotedAmountCents, int? estimatedAmountCents) => quotedAmountCents ?? estimatedAmountCents;

    /// <summary>
    /// Decision D7: true when <paramref name="finalAmountCents"/> is more than <paramref name="tolerancePercent"/> % above
    /// <paramref name="referenceAmountCents"/>. Exactly at the limit is fine ("oltre il 20 %"); with no reference there is
    /// nothing to be above, so a request nobody priced never needs a confirmation.
    /// </summary>
    public static bool ExceedsQuote(int? referenceAmountCents, int? finalAmountCents, int tolerancePercent)
    {
        if (referenceAmountCents is not > 0 || finalAmountCents is not { } final)
            return false;

        return (long)final * 100 > (long)referenceAmountCents.Value * (100 + Math.Max(0, tolerancePercent));
    }

    /// <summary>
    /// The final price of a completed request. Without a declared total it is the reference price plus the extras (nothing at
    /// all when there is neither); with a declared total, that is the total, and it must cover the extras: what is left is
    /// the price of the work itself. The lines always add up to the total.
    /// </summary>
    /// <param name="referenceAmountCents">The quote, else the estimate; <c>null</c> when the work was never priced.</param>
    /// <param name="declaredFinalAmountCents">The total the supplier declared, or <c>null</c> to compute it.</param>
    /// <param name="extras">The extras, already trimmed or not (they are trimmed here).</param>
    /// <param name="baseLabel">What the base line is called (the name of the service).</param>
    /// <exception cref="DomainRuleException">
    /// <see cref="ServiceRequestErrorCodes.AmountInvalid"/> for an amount or an extra outside its limits;
    /// <see cref="ServiceRequestErrorCodes.FinalAmountInvalid"/> when the declared total does not cover the extras.
    /// </exception>
    public static ServiceRequestFinalPrice ComposeFinalPrice(
        int? referenceAmountCents,
        int? declaredFinalAmountCents,
        IReadOnlyList<ServiceRequestExtra>? extras,
        string baseLabel)
    {
        var lines = new List<ServiceRequestPriceLine>();
        var extraLines = NormalizeExtras(extras);
        long extrasTotal = extraLines.Sum(extra => (long)extra.AmountCents);

        long total;
        long baseAmount;
        if (declaredFinalAmountCents is { } declared)
        {
            if (!IsValidAmount(declared))
                throw AmountInvalid();
            if (declared < extrasTotal)
                throw new DomainRuleException(ServiceRequestErrorCodes.FinalAmountInvalid, ServiceRequestErrorCodes.FinalAmountInvalidMessageKey);

            total = declared;
            baseAmount = declared - extrasTotal;
        }
        else
        {
            baseAmount = referenceAmountCents ?? 0;
            total = baseAmount + extrasTotal;
        }

        if (total > ServiceRequestLimits.MaxAmountCents)
            throw AmountInvalid();

        if (baseAmount > 0)
            lines.Add(new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, baseLabel.Trim(), (int)baseAmount));
        lines.AddRange(extraLines.Select(extra => new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Extra, extra.Label, extra.AmountCents)));

        return new ServiceRequestFinalPrice(total > 0 ? (int)total : null, lines);
    }

    /// <summary>True for an amount a request may carry: from 1 cent to the bound of the catalog.</summary>
    public static bool IsValidAmount(long amountCents) => amountCents is >= 1 and <= ServiceRequestLimits.MaxAmountCents;

    /// <summary>Refuses an amount a request may not carry.</summary>
    /// <exception cref="DomainRuleException"><see cref="ServiceRequestErrorCodes.AmountInvalid"/>.</exception>
    public static void EnsureValidAmount(int amountCents)
    {
        if (!IsValidAmount(amountCents))
            throw AmountInvalid();
    }

    private static List<ServiceRequestExtra> NormalizeExtras(IReadOnlyList<ServiceRequestExtra>? extras)
    {
        if (extras is null || extras.Count == 0)
            return [];

        if (extras.Count > ServiceRequestLimits.MaxExtras)
            throw AmountInvalid();

        var normalized = new List<ServiceRequestExtra>(extras.Count);
        foreach (var extra in extras)
        {
            var label = extra?.Label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Length > ServiceRequestLimits.ExtraLabelMaxLength || !IsValidAmount(extra!.AmountCents))
                throw AmountInvalid();

            normalized.Add(new ServiceRequestExtra(label, extra.AmountCents));
        }

        return normalized;
    }

    private static DomainRuleException AmountInvalid() =>
        new(
            ServiceRequestErrorCodes.AmountInvalid,
            ServiceRequestErrorCodes.AmountInvalidMessageKey,
            ServiceRequestLimits.MaxAmountCents / 100,
            ServiceRequestLimits.MaxExtras,
            ServiceRequestLimits.ExtraLabelMaxLength);
}

/// <summary>Reads and writes the JSON columns of <c>ServiceRequest</c> (<c>OptionsJson</c>, <c>PriceLinesJson</c>, <c>WorkPhotosJson</c>): camelCase.</summary>
public static class ServiceRequestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The JSON array of <paramref name="items"/>.</summary>
    public static string Serialize<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items, Options);

    /// <summary>The price lines stored in <paramref name="json"/>; an empty list for a missing or unreadable value.</summary>
    public static IReadOnlyList<ServiceRequestPriceLine> ReadPriceLines(string? json) => Read<ServiceRequestPriceLine>(json);

    /// <summary>The options stored in <paramref name="json"/>; an empty list for a missing or unreadable value.</summary>
    public static IReadOnlyList<ServiceRequestOption> ReadOptions(string? json) => Read<ServiceRequestOption>(json);

    /// <summary>The photos stored in <paramref name="json"/>; an empty list for a missing or unreadable value.</summary>
    public static IReadOnlyList<ServiceRequestPhoto> ReadPhotos(string? json) => Read<ServiceRequestPhoto>(json);

    private static List<T> Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options)?.Where(item => item is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            // Written by this module only; an unreadable value is treated as empty rather than failing a whole read.
            return [];
        }
    }
}
