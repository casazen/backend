using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// The tourist tax of Italy and who collects it by default (FIX/BE-IT-TOURISTTAX-DEFAULTS, decision 2 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research R2), bound from
/// <c>ItalianTouristTax</c>: the Italian counterpart of <see cref="SwissTouristTaxOptions.PlatformCollected"/>, kept apart so that an Italian organization never inherits the Swiss list.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Platform collected.</b> The channels whose portal collects the tourist tax itself for an Italian issuer that has not confirmed its own list yet
/// (<see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>): read when the document is made, never copied into the profile. <b>Empty by default</b>: in Italy
/// no portal is taken to collect the tax unless somebody says so (art. 4 c. 5-ter DL 50/2017: the subject that collects the rent or intervenes in the payment is the one responsible,
/// and no official page says that Booking.com, Expedia or Vrbo collect it in Italy; Airbnb collects it only where the municipality is registered with it), so the document of a
/// portal booking says «da pagare in struttura» and never «incassata dal portale». What the host saved (<c>PlatformCollectedTouristTaxChannels</c>, an empty list included) always wins
/// over this default.</item>
/// <item><b>Validated at startup</b> (<see cref="ItalianTouristTaxOptionsValidator"/>): a channel that is not a portal stops the application with a clear message.</item>
/// </list>
/// </remarks>
public sealed class ItalianTouristTaxOptions
{
    /// <summary>The section the options are read from.</summary>
    public const string SectionName = "ItalianTouristTax";

    /// <summary>The channels whose portal collects the Italian tourist tax itself, by default; the names of <see cref="BookingSource"/>. Empty by default.</summary>
    public List<BookingSource> PlatformCollected { get; set; } = [];
}

/// <summary>The checks of the Italian tourist tax options at startup (<see cref="ItalianTouristTaxOptions"/>): every problem is named with its key.</summary>
public sealed class ItalianTouristTaxOptionsValidator : IValidateOptions<ItalianTouristTaxOptions>
{
    public ValidateOptionsResult Validate(string? name, ItalianTouristTaxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = Problems(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>What is wrong with the options; empty when they can be used.</summary>
    public static IReadOnlyList<string> Problems(ItalianTouristTaxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        foreach (var channel in options.PlatformCollected ?? [])
        {
            if (!Enum.IsDefined(channel) || !FiscalCopy.IsOtaBookingSource(channel))
                problems.Add($"ItalianTouristTax:PlatformCollected: {channel} is not a portal.");
        }

        return problems;
    }
}
