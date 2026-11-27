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

/// <summary>How the MRZ of a format is laid out (ICAO 9303).</summary>
/// <param name="Type">The type of MRZ, a code of <see cref="DocumentFormatMrzTypes"/>: <c>TD1</c> (cards, three lines), <c>TD2</c> or <c>TD3</c> (passports, two lines).</param>
/// <param name="Side">The side the MRZ is on, a side of the format (<c>front</c>, <c>back</c> or <c>single</c>).</param>
/// <param name="IssuingState">The code of the issuing state as the MRZ spells it (without the filler), for example <c>ITA</c>.</param>
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

/// <summary>What is on one side of the card.</summary>
/// <param name="Fields">The fields printed on this side, each with its zone and its labels.</param>
/// <param name="Portrait">The region of the portrait of the holder; only on the side that has it (<c>front</c> or <c>single</c>).</param>
/// <param name="MrzRegion">The region of the MRZ; only on the side the MRZ is on.</param>
public sealed record DocumentFormatSideLayout(
    IReadOnlyList<DocumentFormatField> Fields,
    DocumentFormatRegion? Portrait = null,
    DocumentFormatRegion? MrzRegion = null);

/// <summary>A field printed on the card: where it is and how it is labelled.</summary>
/// <param name="Name">The name of the field, in the vocabulary of the policy (<c>surname</c>, <c>givenNames</c>, <c>birthDate</c>, <c>documentNumber</c>, ...); unique on its side.</param>
/// <param name="Zone">The zone the value is printed in.</param>
/// <param name="Labels">The labels printed next to the value, as they are printed (Italian and English), for example <c>COGNOME / SURNAME</c>.</param>
public sealed record DocumentFormatField(string Name, DocumentFormatRegion Zone, IReadOnlyList<string> Labels);

/// <summary>A rectangle on the canonical card: every member is a share of the card (0 to 1) and the rectangle is inside it.</summary>
/// <param name="X">The distance of the left edge from the left edge of the card.</param>
/// <param name="Y">The distance of the top edge from the top edge of the card.</param>
/// <param name="W">The width.</param>
/// <param name="H">The height.</param>
public sealed record DocumentFormatRegion(double X, double Y, double W, double H);

/// <summary>The types of MRZ of <see cref="DocumentFormatMrz.Type"/> (ICAO 9303).</summary>
public static class DocumentFormatMrzTypes
{
    public const string Td1 = "TD1";
    public const string Td2 = "TD2";
    public const string Td3 = "TD3";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Td1, Td2, Td3];
}

/// <summary>The sides a format may have, spelled as the endpoints of the check-in spell them (<see cref="DocumentSides"/>).</summary>
public static class DocumentFormatSides
{
    /// <summary>A card with two sides, in this order.</summary>
    public static IReadOnlyList<string> TwoSided { get; } = [DocumentSides.Front, DocumentSides.Back];

    /// <summary>One page that holds the portrait and the MRZ (a passport).</summary>
    public static IReadOnlyList<string> OneSided { get; } = [DocumentSides.Single];

    /// <summary>Every side code a layout or an MRZ may name.</summary>
    public static IReadOnlyList<string> All { get; } = [DocumentSides.Front, DocumentSides.Back, DocumentSides.Single];
}

/// <summary>The checks a format may require (<see cref="DocumentFormat.RequiredChecks"/>); each is carried out by a later task of the plan.</summary>
public static class DocumentFormatChecks
{
    /// <summary>The card is whole, of the layout of the format, with its edges visible and in the right orientation.</summary>
    public const string Layout = "layout";

    /// <summary>The portrait is where the layout expects it.</summary>
    public const string Portrait = "portrait";

    /// <summary>The check digits of the MRZ are valid (ICAO 9303).</summary>
    public const string MrzCheckDigits = "mrz_check_digits";

    /// <summary>The document has not expired.</summary>
    public const string Expiry = "expiry";

    /// <summary>The two sides agree with each other.</summary>
    public const string SideConsistency = "side_consistency";

    /// <summary>The fields read by zone agree with the MRZ and with each other.</summary>
    public const string FieldCoherence = "field_coherence";

    /// <summary>The document agrees with the data of the booking.</summary>
    public const string BookingConsistency = "booking_consistency";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Layout, Portrait, MrzCheckDigits, Expiry, SideConsistency, FieldCoherence, BookingConsistency];
}

/// <summary>The statuses of a format (<see cref="DocumentFormat.Status"/>).</summary>
public static class DocumentFormatStatuses
{
    /// <summary>Not qualified: the numbers of the layout are approximate. The only status of a new format.</summary>
    public const string Provisional = "provisional";

    /// <summary>Qualified on a dataset (a person writes it, with the report of the qualification, never a task of the plan).</summary>
    public const string Qualified = "qualified";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Provisional, Qualified];
}

/// <summary>
/// The kinds of channel a format can admit (<see cref="DocumentFormat.Assurance"/>): the part of a code of <see cref="IdentityChannels"/> before the colon
/// (<c>web:ios-safari</c> is of the kind <c>web</c>).
/// </summary>
public static class DocumentFormatChannels
{
    /// <summary>The kind of every browser channel.</summary>
    public const string Web = "web";

    /// <summary>The kinds the channels of <see cref="IdentityChannels.All"/> belong to, in this order.</summary>
    public static IReadOnlyList<string> Kinds { get; } = IdentityChannels.All.Select(KindOf).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The kind of a channel code (the text before the first colon, lower case); the code itself when it has no colon.</summary>
    public static string KindOf(string channel)
    {
        var code = channel.Trim().ToLowerInvariant();
        var colon = code.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? code : code[..colon];
    }
}

/// <summary>
/// Why a format file is not used (codes only: the log of the registry speaks them, with the name of the file and nothing else; never a value of a document).
/// A code is never renamed or reused; a new one goes at the end of its class and of its <c>All</c> list.
/// </summary>
public static class DocumentFormatReasonCodes
{
    /// <summary>The file could not be read.</summary>
    public const string FormatUnreadable = "format_unreadable";

    /// <summary>The file is not strict JSON, lacks a member, has one the format does not have, or a member has the wrong type; or it made the reader or the validator throw (the file alone is skipped).</summary>
    public const string FormatJsonInvalid = "format_json_invalid";

    /// <summary>The id is not 1 to 40 lower case letters, digits, dots, hyphens or underscores starting with a letter or a digit.</summary>
    public const string FormatIdInvalid = "format_id_invalid";

    /// <summary>Another file has the same id: neither is used, since which one is meant cannot be told.</summary>
    public const string FormatIdDuplicate = "format_id_duplicate";

    /// <summary>The country is not an ISO 3166 alpha-3 code.</summary>
    public const string FormatCountryInvalid = "format_country_invalid";

    /// <summary>The label is empty or longer than 100 characters.</summary>
    public const string FormatLabelInvalid = "format_label_invalid";

    /// <summary>The document codes are missing, repeated, or not one or two capital letters; or an excluded code is repeated, is not one or two capital letters, is one of the document codes or does not start with one.</summary>
    public const string FormatDocumentCodesInvalid = "format_document_codes_invalid";

    /// <summary>The MRZ type, side or issuing state is not valid.</summary>
    public const string FormatMrzInvalid = "format_mrz_invalid";

    /// <summary>The sides are not <c>front</c> and <c>back</c> or <c>single</c>, or the MRZ is on a side the format does not have.</summary>
    public const string FormatSidesInvalid = "format_sides_invalid";

    /// <summary>The layout is incoherent: a number out of its bounds, a side without a layout (or whose layout is null) or with one it should not have, the MRZ or the portrait missing or repeated, a field that is null, repeated or without a label.</summary>
    public const string FormatLayoutInvalid = "format_layout_invalid";

    /// <summary>A region (the portrait, the MRZ or the zone of a field) is not inside the card: a member outside 0..1, or no width or height.</summary>
    public const string FormatRegionOutOfBounds = "format_region_out_of_bounds";

    /// <summary>The required checks are missing, repeated or unknown.</summary>
    public const string FormatChecksInvalid = "format_checks_invalid";

    /// <summary>The assurance names an unknown kind of channel or level, or claims <c>cryptographic</c> for a channel that cannot give it (a browser reads no chip).</summary>
    public const string FormatAssuranceInvalid = "format_assurance_invalid";

    /// <summary>The status is not a code of <see cref="DocumentFormatStatuses"/>.</summary>
    public const string FormatStatusInvalid = "format_status_invalid";

    /// <summary>The folder of the formats does not exist: no format is known and every document goes to a person.</summary>
    public const string FormatsFolderMissing = "formats_folder_missing";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        FormatUnreadable, FormatJsonInvalid, FormatIdInvalid, FormatIdDuplicate, FormatCountryInvalid, FormatLabelInvalid, FormatDocumentCodesInvalid,
        FormatMrzInvalid, FormatSidesInvalid, FormatLayoutInvalid, FormatRegionOutOfBounds, FormatChecksInvalid, FormatAssuranceInvalid, FormatStatusInvalid,
        FormatsFolderMissing,
    ];
}
