using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

public static class ServiceRequestCancellationReasons
{
    public const string NoResponse = "NoResponse";

    public const string ProposalNotAnswered = "ProposalNotAnswered";

    public const string CancelledByCustomer = "CancelledByCustomer";

    public const string PrimaryTookIt = "PrimaryTookIt";

    public const string TakenByBackup = "TakenByBackup";

    public const string RequestCancelledByHost = "RequestCancelledByHost";

    public const string BackupSlotUnavailable = "BackupSlotUnavailable";

    /// <summary>
    /// SP-BKP, a backup supplier's request: another backup accepted first, which closes the race for the others at that instant — only the
    /// first backup that accepts can have the job, and it has it if the primary supplier does not take it. Closed by CasaZen
    /// (<c>CancelledBy = System</c>) and with no penalty: the supplier did nothing wrong. The client translates it.
    /// </summary>
    public const string BackupRaceClosed = "BackupRaceClosed";

    public const string QuoteNotAnswered = "QuoteNotAnswered";

    public static bool IsCode(string? reason) =>
        reason is NoResponse or ProposalNotAnswered or CancelledByCustomer or QuoteNotAnswered or PrimaryTookIt or TakenByBackup
            or RequestCancelledByHost or BackupSlotUnavailable or BackupRaceClosed;
}

public static class ServiceRequestPriceLineKinds
{
    public const string Base = "base";

    public const string PlatformFee = "platformFee";

    public const string Extra = "extra";

    public const string Quote = "quote";
}

public sealed record ServiceRequestPriceLine(string Kind, string Label, int AmountCents, int? Quantity = null, int? UnitAmountCents = null);

public sealed record ServiceRequestExtra(string Label, int AmountCents);

public sealed record ServiceRequestOption(string Code, string Label, int AmountCents, string Per, int Quantity);

public static class ServiceRequestOptionCodes
{
    public const string Quantity = "quantity";

    public const string SquareMeter = "sqm";

    public const string Urgent = "urgent";
}

public sealed record ServiceRequestPhoto(Guid Id, string Key, DateTime UploadedAt);

public sealed record ServiceRequestFinalPrice(int? FinalAmountCents, IReadOnlyList<ServiceRequestPriceLine> Lines);

public static class ServiceRequestPricing
{
    public const int DefaultTolerancePercent = 20;

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
    /// <param name="agreedLines">
    /// The lines of the quote the customer accepted (SP-04b), if any: when they add up to the price of the work itself (the total less the
    /// extras) they take the place of the single base line, so the final price keeps the lines the customer approved.
    /// </param>
    /// <exception cref="DomainRuleException">
    /// <see cref="ServiceRequestErrorCodes.AmountInvalid"/> for an amount or an extra outside its limits;
    /// <see cref="ServiceRequestErrorCodes.FinalAmountInvalid"/> when the declared total does not cover the extras.
    /// </exception>
    public static ServiceRequestFinalPrice ComposeFinalPrice(
        int? referenceAmountCents,
        int? declaredFinalAmountCents,
        IReadOnlyList<ServiceRequestExtra>? extras,
        string baseLabel,
        IReadOnlyList<ServiceRequestPriceLine>? agreedLines = null)
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
        {
            if (agreedLines is { Count: > 0 } && agreedLines.Sum(line => (long)line.AmountCents) == baseAmount)
                lines.AddRange(agreedLines);
            else
                lines.Add(new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, baseLabel.Trim(), (int)baseAmount));
        }

        lines.AddRange(extraLines.Select(extra => new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Extra, extra.Label, extra.AmountCents)));

        return new ServiceRequestFinalPrice(total > 0 ? (int)total : null, lines);
    }

    public static bool IsValidAmount(long amountCents) => amountCents is >= 1 and <= ServiceRequestLimits.MaxAmountCents;

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

public static class ServiceRequestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items, Options);

    public static IReadOnlyList<ServiceRequestPriceLine> ReadPriceLines(string? json) => Read<ServiceRequestPriceLine>(json);

    public static IReadOnlyList<ServiceRequestOption> ReadOptions(string? json) => Read<ServiceRequestOption>(json);

    public static IReadOnlyList<ServiceRequestPhoto> ReadPhotos(string? json) => Read<ServiceRequestPhoto>(json);

    public static IReadOnlyList<ServiceRequestQuoteLine> ReadQuoteLines(string? json) => Read<ServiceRequestQuoteLine>(json);

    public static IReadOnlyList<ServiceRequestQuoteEvent> ReadQuoteEvents(string? json) => Read<ServiceRequestQuoteEvent>(json);

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
