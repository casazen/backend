namespace Casazen.Core.Suppliers;

/// <summary>
/// Bounds of the content of a supplier's showcase (SP-13a). The ones the design states (the sentence, the introduction, "chi
/// siamo", the guarantee, the captions) are the ones of the editor's counters; the others are technical bounds that keep a
/// page readable and a request small. One place, so the rules, the DTOs and the editor cannot drift apart.
/// </summary>
public static class ShowcaseContentLimits
{
    /// <summary>The format of the content document. A change of shape gets a new number, never a silent change of the old one.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The sentence in large type (design: 90).</summary>
    public const int TaglineMax = 90;

    /// <summary>The text under the title (design: 180).</summary>
    public const int IntroMax = 180;

    /// <summary>"Chi siamo" (design: 700).</summary>
    public const int BioMax = 700;

    public const int TeamMax = 12;
    public const int TeamNameMax = 60;
    public const int TeamRoleMax = 60;

    public const int ValuesMax = 6;
    public const int ValueMax = 100;

    /// <summary>The guarantee the supplier writes itself (design: 60 and 160).</summary>
    public const int GuaranteeTitleMax = 60;

    /// <inheritdoc cref="GuaranteeTitleMax"/>
    public const int GuaranteeTextMax = 160;

    public const int CertsMax = 8;
    public const int CertLabelMax = 80;
    public const int CertDetailMax = 160;

    public const int FaqMax = 12;
    public const int FaqQuestionMax = 120;
    public const int FaqAnswerMax = 600;

    /// <summary>Jobs shown in the gallery.</summary>
    public const int GalleryMax = 12;

    /// <summary>Caption and place of a job (design: 70 and 40).</summary>
    public const int CaptionMax = 70;

    /// <inheritdoc cref="CaptionMax"/>
    public const int PlaceMax = 40;

    /// <summary>Alternative text of a photo.</summary>
    public const int AltMax = 120;

    /// <summary>"Prima che arriviamo".</summary>
    public const int PrepMax = 6;

    /// <inheritdoc cref="PrepMax"/>
    public const int PrepItemMax = 160;

    /// <summary>Longest image URL.</summary>
    public const int UrlMax = 500;

    /// <summary>Services shown, in order: never more than a supplier can have.</summary>
    public const int ServiceOrderMax = SupplierServiceCatalogLimits.MaxServicesPerSupplier;

    /// <summary>Largest body of a save (the whole document is a few kilobytes of text).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>How long the last publication can be undone.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(5);

    /// <summary>The button color must keep white text at least this readable (WCAG AA, normal text).</summary>
    public const double MinContrastWithWhite = 4.5;

    // What the readiness recommends (nothing of this blocks a publication).

    /// <summary>The sentence of the presentation is worth showing from this many characters.</summary>
    public const int RecommendedTaglineMin = 12;

    /// <summary>"Chi siamo" is worth showing from this many characters.</summary>
    public const int RecommendedBioMin = 60;

    /// <summary>Photos of jobs the page looks right with.</summary>
    public const int RecommendedPhotos = 3;

    /// <summary>Questions the page looks right with.</summary>
    public const int RecommendedFaq = 3;

    /// <summary>Jobs done with CasaZen shown to customers only from this many (a small number is not a proof).</summary>
    public const int JobsDoneMinToShow = 5;
}

/// <summary>The four styles of the showcase.</summary>
public static class ShowcaseStyles
{
    public const string Acqua = "acqua";
    public const string Scintilla = "scintilla";
    public const string Fresco = "fresco";
    public const string Naturale = "naturale";

    public static IReadOnlyList<string> All { get; } = [Acqua, Scintilla, Fresco, Naturale];
}

/// <summary>The three typefaces of the showcase.</summary>
public static class ShowcaseFonts
{
    public const string Moderno = "moderno";
    public const string Editoriale = "editoriale";
    public const string Classico = "classico";

    public static IReadOnlyList<string> All { get; } = [Moderno, Editoriale, Classico];
}

/// <summary>How the supplier's logo is drawn: its initials, its name, or an uploaded image.</summary>
public static class ShowcaseLogoModes
{
    public const string Mark = "mark";
    public const string Text = "text";
    public const string Image = "image";

    public static IReadOnlyList<string> All { get; } = [Mark, Text, Image];
}

/// <summary>The logo: <see cref="Url"/> only for <see cref="ShowcaseLogoModes.Image"/>, a PNG/JPG/WebP of the supplier's own storage (never SVG).</summary>
public sealed record ShowcaseLogo(string? Mode, string? Url);

/// <summary>The cover photo; without one the page uses a neutral fill of the style.</summary>
public sealed record ShowcaseCover(string? Url, string? Alt);

public sealed record ShowcaseTeamMember(string? Name, string? Role);

/// <summary>A guarantee <b>written by the supplier</b>; CasaZen never writes one for it.</summary>
public sealed record ShowcaseGuarantee(string? Title, string? Text);

/// <summary>A certification <b>written by the supplier</b>; never prefilled.</summary>
public sealed record ShowcaseCertification(string? Label, string? Detail);

public sealed record ShowcaseFaq(string? Q, string? A);

/// <summary>A job of the gallery; a before/after needs two photos (<see cref="BeforeUrl"/> and <see cref="PhotoUrl"/>).</summary>
public sealed record ShowcaseWork(string? PhotoUrl, string? BeforeUrl, string? Caption, string? Place, string? Alt);

/// <summary>Which blocks of the home are shown. The services and the final actions are always shown.</summary>
public sealed record ShowcaseSections
{
    public bool Trust { get; init; } = true;
    public bool How { get; init; } = true;
    public bool Works { get; init; } = true;
    public bool Zones { get; init; } = true;
    public bool Certs { get; init; } = true;
    public bool Faq { get; init; } = true;
}

/// <summary>
/// The content of a showcase, format <see cref="ShowcaseContentLimits.SchemaVersion"/> (SP-13a). Plain text, no HTML, and no
/// contact data (phone, WhatsApp, e-mail, hours of answer, domain, search-engine title/description, indexing): those are not
/// the supplier's to publish here. Every member is optional; a draft may be incomplete, a publication may not
/// (<see cref="ShowcaseContentRules.ForPublication"/>).
/// </summary>
public sealed record ShowcaseContentDocument
{
    /// <summary>One of <see cref="ShowcaseStyles"/>; <c>null</c> = the style recommended for the supplier's trade.</summary>
    public string? Style { get; init; }

    /// <summary>One of <see cref="ShowcaseFonts"/>; <c>null</c> = the style's own.</summary>
    public string? Font { get; init; }

    /// <summary><c>#RRGGBB</c> of the buttons; <c>null</c> = the style's own. Accepted only if white text on it reads at 4.5:1 or more.</summary>
    public string? Color { get; init; }

    public ShowcaseLogo? Logo { get; init; }

    public ShowcaseCover? Cover { get; init; }

    public string? Tagline { get; init; }

    public string? Intro { get; init; }

    public string? Bio { get; init; }

    public IReadOnlyList<ShowcaseTeamMember> Team { get; init; } = [];

    public IReadOnlyList<string> Values { get; init; } = [];

    public ShowcaseGuarantee? Guarantee { get; init; }

    public IReadOnlyList<ShowcaseCertification> Certs { get; init; } = [];

    public IReadOnlyList<ShowcaseFaq> Faq { get; init; } = [];

    public IReadOnlyList<ShowcaseWork> Gallery { get; init; } = [];

    public IReadOnlyList<string> Prep { get; init; } = [];

    public ShowcaseSections Sections { get; init; } = new();

    /// <summary>
    /// Slugs of the services to show, in order, all of them <c>Active</c>. Empty = every active service, in the order of the
    /// catalog; otherwise only these.
    /// </summary>
    public IReadOnlyList<string> ServiceOrder { get; init; } = [];
}

/// <summary>The state the editor shows, derived from the row (never stored).</summary>
public enum ShowcaseContentState
{
    /// <summary>Nothing published yet.</summary>
    Draft = 0,

    /// <summary>Published, and the draft is what is published.</summary>
    Online = 1,

    /// <summary>Published, and the draft has changes that are not.</summary>
    ChangesReady = 2,

    /// <summary>Published, and the supplier paused it.</summary>
    Paused = 3,
}
