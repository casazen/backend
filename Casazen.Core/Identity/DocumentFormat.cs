using Casazen.Core.Services;

namespace Casazen.Core.Identity;

// E70/F01/US02/T02 (NF-189, NF-191, decisions D1 and D11): the formats of the identity documents the self-hosted verification knows, as their files spell them
// (Casazen.Web/IdentityFormats/*.json, loaded once by the DocumentFormatRegistry). These records hold NO number of a layout and no default of their own: the
// regions, the tolerances, the checks and the assurance of a format come from its file, so a new format or country is a new file plus a new qualification, never a
// change of the flow. The names are the names of the members of the file, in camel case. Codes only: nothing here ever holds a value read on a document.

/// <summary>
/// One format of identity document (the Italian electronic identity card first): how to recognize it from its MRZ, how many sides it has, where things are on the
/// card, which checks its verification requires and what level of assurance each channel can give for it. A format is <b>provisional</b> until it is qualified on a
/// dataset (D12): the numbers of its layout are approximate until then.
/// </summary>
/// <param name="Id">The id of the format, for example <c>it-cie-3.0</c>: unique in the registry, lower case letters, digits, dots, hyphens and underscores, at most 40 characters (what the log rows and the segments keep).</param>
/// <param name="Country">The country of issue, ISO 3166 alpha-3, for example <c>ITA</c>.</param>
/// <param name="Label">A name for the staff, for example <c>Carta d'identità elettronica italiana (CIE 3.0)</c>.</param>
/// <param name="DocumentCodes">
/// The prefixes of the MRZ document code (the first characters of the first line, without the filler <c>&lt;</c>) a document of this format may have, for example
/// <c>I</c> and <c>C</c> (ICAO identity cards start with <c>I</c>, <c>A</c> or <c>C</c>). The longest matching prefix wins when two formats of one country share a code.
/// A prefix that is too wide for the cards of the format is narrowed by <paramref name="ExcludedDocumentCodes"/>.
/// </param>
/// <param name="Mrz">How the MRZ of the format is laid out.</param>
/// <param name="Sides">The sides to capture: <c>front</c> and <c>back</c> (in this order), or <c>single</c> for one page that holds everything.</param>
/// <param name="Layout">Where the things of the card are.</param>
/// <param name="RequiredChecks">The checks of <see cref="DocumentFormatChecks"/> the verification of this format must pass, whatever the channel.</param>
/// <param name="Assurance">
/// The level of assurance (<see cref="IdentityAssuranceLevels"/>) each kind of channel (<see cref="DocumentFormatChannels.Kinds"/>, for example <c>web</c>) gives for
/// this format. A kind that is not listed is not admitted: that combination always goes to a person. A browser gives <c>consistency</c> only.
/// </param>
/// <param name="Status">A code of <see cref="DocumentFormatStatuses"/>: <c>provisional</c> until the format is qualified.</param>
/// <param name="ExcludedDocumentCodes">
/// Optional. Prefixes of the MRZ document code that are NOT of this format although they start with one of <paramref name="DocumentCodes"/>: the Italian card claims
/// every code that starts with <c>I</c>, and <c>IR</c> is the residence permit (the code base reads it as such, <c>MrzExtractionService</c>), so the file excludes
/// <c>IR</c> and <c>DetectFromMrz</c> answers null for it (the flow says <c>format_unsupported</c> and a person decides) unless another format claims it. Each
/// code is one or two capital letters, starts with one of the document codes and is not one of them; none repeated. Absent or empty: nothing is excluded.
/// </param>
public sealed record DocumentFormat(
    string Id,
    string Country,
    string Label,
    IReadOnlyList<string> DocumentCodes,
    DocumentFormatMrz Mrz,
    IReadOnlyList<string> Sides,
    DocumentFormatLayout Layout,
    IReadOnlyList<string> RequiredChecks,
    IReadOnlyDictionary<string, string> Assurance,
    string Status,
    IReadOnlyList<string>? ExcludedDocumentCodes = null);

public sealed record DocumentFormatMrz(string Type, string Side, string IssuingState);

/// <summary>
/// The layout of the card, on the canonical landscape card: every region is normalized to 0..1 (x and y from the top left corner, width and height) and is
/// calibrated on the qualification dataset of the format.
/// </summary>
/// <param name="AspectRatio">The width over the height of the card, for example 1.586 for an ID-1 card (85.60 by 53.98 mm).</param>
/// <param name="AspectTolerance">The tolerance around <paramref name="AspectRatio"/>, in units of the ratio (an absolute difference), for the card to be of this format.</param>
/// <param name="MinCardAreaShare">The least share (0 to 1) of the picture the card must cover.</param>
/// <param name="MarginMin">The least margin (0 to 0.5), as a share of the picture, between the card and the border of the picture: the edges of the card must be visible.</param>
/// <param name="Sides">The layout of every side of the format, by side (<c>front</c>, <c>back</c> or <c>single</c>).</param>
public sealed record DocumentFormatLayout(
    double AspectRatio,
    double AspectTolerance,
    double MinCardAreaShare,
    double MarginMin,
    IReadOnlyDictionary<string, DocumentFormatSideLayout> Sides);

public sealed record DocumentFormatSideLayout(
    IReadOnlyList<DocumentFormatField> Fields,
    DocumentFormatRegion? Portrait = null,
    DocumentFormatRegion? MrzRegion = null);

public sealed record DocumentFormatField(string Name, DocumentFormatRegion Zone, IReadOnlyList<string> Labels);

public sealed record DocumentFormatRegion(double X, double Y, double W, double H);

public static class DocumentFormatMrzTypes
{
    public const string Td1 = "TD1";
    public const string Td2 = "TD2";
    public const string Td3 = "TD3";

    public static IReadOnlyList<string> All { get; } = [Td1, Td2, Td3];
}

public static class DocumentFormatSides
{
    public static IReadOnlyList<string> TwoSided { get; } = [DocumentSides.Front, DocumentSides.Back];

    public static IReadOnlyList<string> OneSided { get; } = [DocumentSides.Single];

    public static IReadOnlyList<string> All { get; } = [DocumentSides.Front, DocumentSides.Back, DocumentSides.Single];
}

public static class DocumentFormatChecks
{
    public const string Layout = "layout";

    public const string Portrait = "portrait";

    public const string MrzCheckDigits = "mrz_check_digits";

    public const string Expiry = "expiry";

    public const string SideConsistency = "side_consistency";

    public const string FieldCoherence = "field_coherence";

    public const string BookingConsistency = "booking_consistency";

    public static IReadOnlyList<string> All { get; } = [Layout, Portrait, MrzCheckDigits, Expiry, SideConsistency, FieldCoherence, BookingConsistency];
}

public static class DocumentFormatStatuses
{
    public const string Provisional = "provisional";

    public const string Qualified = "qualified";

    public static IReadOnlyList<string> All { get; } = [Provisional, Qualified];
}

public static class DocumentFormatChannels
{
    public const string Web = "web";

    public static IReadOnlyList<string> Kinds { get; } = IdentityChannels.All.Select(KindOf).Distinct(StringComparer.Ordinal).ToList();

    public static string KindOf(string channel)
    {
        var code = channel.Trim().ToLowerInvariant();
        var colon = code.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? code : code[..colon];
    }
}

public static class DocumentFormatReasonCodes
{
    public const string FormatUnreadable = "format_unreadable";

    public const string FormatJsonInvalid = "format_json_invalid";

    public const string FormatIdInvalid = "format_id_invalid";

    /// <summary>Another file has the same id: neither is used, since which one is meant cannot be told.</summary>
    public const string FormatIdDuplicate = "format_id_duplicate";

    public const string FormatCountryInvalid = "format_country_invalid";

    public const string FormatLabelInvalid = "format_label_invalid";

    public const string FormatDocumentCodesInvalid = "format_document_codes_invalid";

    public const string FormatMrzInvalid = "format_mrz_invalid";

    public const string FormatSidesInvalid = "format_sides_invalid";

    public const string FormatLayoutInvalid = "format_layout_invalid";

    public const string FormatRegionOutOfBounds = "format_region_out_of_bounds";

    public const string FormatChecksInvalid = "format_checks_invalid";

    public const string FormatAssuranceInvalid = "format_assurance_invalid";

    public const string FormatStatusInvalid = "format_status_invalid";

    public const string FormatsFolderMissing = "formats_folder_missing";

    public static IReadOnlyList<string> All { get; } =
    [
        FormatUnreadable, FormatJsonInvalid, FormatIdInvalid, FormatIdDuplicate, FormatCountryInvalid, FormatLabelInvalid, FormatDocumentCodesInvalid,
        FormatMrzInvalid, FormatSidesInvalid, FormatLayoutInvalid, FormatRegionOutOfBounds, FormatChecksInvalid, FormatAssuranceInvalid, FormatStatusInvalid,
        FormatsFolderMissing,
    ];
}
