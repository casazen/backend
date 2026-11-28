namespace Casazen.Core.Suppliers;

public static class ShowcaseContentLimits
{
    public const int SchemaVersion = 1;

    public const int TaglineMax = 90;

    public const int IntroMax = 180;

    public const int BioMax = 700;

    public const int TeamMax = 12;
    public const int TeamNameMax = 60;
    public const int TeamRoleMax = 60;

    public const int ValuesMax = 6;
    public const int ValueMax = 100;

    public const int GuaranteeTitleMax = 60;

    public const int GuaranteeTextMax = 160;

    public const int CertsMax = 8;
    public const int CertLabelMax = 80;
    public const int CertDetailMax = 160;

    public const int FaqMax = 12;
    public const int FaqQuestionMax = 120;
    public const int FaqAnswerMax = 600;

    public const int GalleryMax = 12;

    public const int CaptionMax = 70;

    public const int PlaceMax = 40;

    public const int AltMax = 120;

    public const int PrepMax = 6;

    public const int PrepItemMax = 160;

    public const int UrlMax = 500;

    public const int ServiceOrderMax = SupplierServiceCatalogLimits.MaxServicesPerSupplier;

    public const int MaxBodyBytes = 64 * 1024;

    public static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(5);

    public const double MinContrastWithWhite = 4.5;

    public const int RecommendedTaglineMin = 12;

    public const int RecommendedBioMin = 60;

    public const int RecommendedPhotos = 3;

    public const int RecommendedFaq = 3;

    public const int JobsDoneMinToShow = 5;
}

public static class ShowcaseStyles
{
    public const string Acqua = "acqua";
    public const string Scintilla = "scintilla";
    public const string Fresco = "fresco";
    public const string Naturale = "naturale";

    public static IReadOnlyList<string> All { get; } = [Acqua, Scintilla, Fresco, Naturale];
}

public static class ShowcaseFonts
{
    public const string Moderno = "moderno";
    public const string Editoriale = "editoriale";
    public const string Classico = "classico";

    public static IReadOnlyList<string> All { get; } = [Moderno, Editoriale, Classico];
}

public static class ShowcaseLogoModes
{
    public const string Mark = "mark";
    public const string Text = "text";
    public const string Image = "image";

    public static IReadOnlyList<string> All { get; } = [Mark, Text, Image];
}

public sealed record ShowcaseLogo(string? Mode, string? Url);

public sealed record ShowcaseCover(string? Url, string? Alt);

public sealed record ShowcaseTeamMember(string? Name, string? Role);

public sealed record ShowcaseGuarantee(string? Title, string? Text);

public sealed record ShowcaseCertification(string? Label, string? Detail);

public sealed record ShowcaseFaq(string? Q, string? A);

public sealed record ShowcaseWork(string? PhotoUrl, string? BeforeUrl, string? Caption, string? Place, string? Alt);

public sealed record ShowcaseSections
{
    public bool Trust { get; init; } = true;
    public bool How { get; init; } = true;
    public bool Works { get; init; } = true;
    public bool Zones { get; init; } = true;
    public bool Certs { get; init; } = true;
    public bool Faq { get; init; } = true;
}

public sealed record ShowcaseContentDocument
{
    public string? Style { get; init; }

    public string? Font { get; init; }

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

public enum ShowcaseContentState
{
    Draft = 0,

    Online = 1,

    ChangesReady = 2,

    Paused = 3,
}
