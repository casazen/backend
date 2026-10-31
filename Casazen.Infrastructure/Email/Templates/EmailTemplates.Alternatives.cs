using System.Globalization;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The other suppliers offered to the customer of a request nobody answered in time (SP-10c), as a block of the mails that tell it: a short list with
/// the name of each supplier as a link to its public page, and what its showcase says and what is measured («Verificato», the typical time to answer,
/// the «da €» of the service that corresponds). Texts in <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys <c>SupplierAlternative*</c>).
/// </summary>
/// <remarks>
/// <b>Only what is true</b> (decision D24): a list is shown only when it was looked for; when nothing qualifies the mail says so plainly, and nothing
/// promises that anybody will call back. Every dynamic value is HTML-encoded by the builder; every link is built by <see cref="PublicSiteLinks"/>
/// and passed in.
/// </remarks>
public static partial class EmailTemplates
{
    /// <summary>One supplier offered in a mail: what its showcase says about it, and the link to its page.</summary>
    public sealed record EmailAlternative(ShowcaseAlternative Supplier, string Url);

    /// <summary>
    /// The block of the alternatives. <c>null</c> adds nothing (they were not looked for, or could not be); an empty list says that there is no other
    /// supplier to suggest for now.
    /// </summary>
    private static void AppendAlternatives(EmailHtmlBuilder builder, CultureInfo culture, IReadOnlyList<EmailAlternative>? alternatives)
    {
        if (alternatives is null)
            return;

        if (alternatives.Count == 0)
        {
            builder.Paragraph("SupplierAlternatives_None");
            return;
        }

        builder
            .Paragraph("SupplierAlternatives_Intro")
            .LinkList(alternatives.Select(alternative => (alternative.Supplier.Name, alternative.Url, (string?)AlternativeDetail(culture, alternative.Supplier))));
    }

    /// <summary>What is said after the name of an alternative: «Verificato», the typical time to answer and the «da €» of its service, as far as they are known.</summary>
    internal static string AlternativeDetail(CultureInfo culture, ShowcaseAlternative supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        var parts = new List<string>();
        if (supplier.Verified)
            parts.Add(EmailTexts.Get("SupplierAlternative_Verified", culture));

        if (supplier.MedianResponseMinutes is { } minutes and > 0)
        {
            parts.Add(minutes < 120
                ? string.Format(culture, EmailTexts.Get("SupplierAlternative_Median", culture), minutes)
                : string.Format(culture, EmailTexts.Get("SupplierAlternative_MedianHours", culture), (minutes + 30) / 60));
        }

        if (supplier.Service is { } service)
        {
            parts.Add(service.FromPriceCents is { } cents
                ? string.Format(
                    culture,
                    EmailTexts.Get("SupplierAlternative_From", culture),
                    service.Name,
                    FormatEuro(cents, culture),
                    EmailTexts.Get($"SupplierAlternative_Unit_{service.PriceUnit}", culture))
                : string.Format(culture, EmailTexts.Get("SupplierAlternative_OnQuote", culture), service.Name));
        }

        return string.Join(" · ", parts);
    }
}
