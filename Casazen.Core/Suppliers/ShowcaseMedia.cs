using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>What an image of a supplier's showcase is for (SP-13b).</summary>
public static class ShowcaseMediaKinds
{
    /// <summary>The supplier's logo (only when the showcase draws an image logo).</summary>
    public const string Logo = "logo";

    /// <summary>The cover photo of the home.</summary>
    public const string Cover = "cover";

    /// <summary>The photo of a job of the gallery.</summary>
    public const string Work = "work";

    /// <summary>The "before" photo of a job, for the before and after.</summary>
    public const string Before = "before";

    public static IReadOnlyList<string> All { get; } = [Logo, Cover, Work, Before];

    /// <summary>The kind named by the text of a form (case does not matter); <c>false</c> for a missing or unknown one.</summary>
    public static bool TryParse(string? value, out string kind)
    {
        var text = value?.Trim().ToLowerInvariant();
        kind = All.FirstOrDefault(known => known == text) ?? string.Empty;
        return kind.Length > 0;
    }

    /// <summary>The largest size of an upload of this kind, as it arrives: the logo is a mark, the others are photographs.</summary>
    public static long MaxBytesOf(string kind) =>
        kind == Logo ? ShowcaseMediaLimits.LogoMaxBytes : ShowcaseMediaLimits.PhotoMaxBytes;
}

/// <summary>
/// Bounds of the images of a showcase (SP-13b). The ones the design states (a logo of 200 KB at most, no SVG, the twelve jobs) are the
/// editor's; the others are technical bounds that keep the storage and the pages small. One place, so the service, the DTOs and the editor
/// cannot drift apart.
/// </summary>
public static class ShowcaseMediaLimits
{
    public const int KindMaxLength = 16;
    public const int StorageKeyMaxLength = 300;
    public const int ContentTypeMaxLength = 32;

    /// <summary>A logo, as it arrives (design: 200 KB).</summary>
    public const long LogoMaxBytes = 200 * 1024;

    /// <summary>A photo as it arrives: 10 MB. The editor makes it smaller before it sends it; the server never makes a file bigger.</summary>
    public const long PhotoMaxBytes = 10 * 1024 * 1024;

    /// <summary>The largest request: the largest photo and the form around it.</summary>
    public const long MaxRequestBytes = PhotoMaxBytes + (1024 * 1024);

    /// <summary>
    /// Photos of the jobs a showcase can show at once: twelve jobs (<see cref="ShowcaseContentLimits.GalleryMax"/>), each with its photo and its
    /// "before". The content refuses more, so this is the number the editor shows; the upload does not count them again.
    /// </summary>
    public const int MaxGalleryPhotos = ShowcaseContentLimits.GalleryMax * 2;

    /// <summary>
    /// Files a supplier can have stored at once, whatever they are for: the 24 photos of the jobs, the logo and the cover, and room for the
    /// ones a replaced or removed photo leaves behind until the daily cleanup takes them. A bound on the storage, not a number the editor shows.
    /// </summary>
    public const int MaxStoredFiles = 60;

    /// <summary>The longest side of a picture, in pixels (the editor sends photos of at most 2,400; a camera of 48 megapixels is 8,000).</summary>
    public const int MaxSidePixels = 12_000;

    /// <summary>The most pixels a picture can have: a small file can hold a picture a browser cannot draw.</summary>
    public const long MaxPixels = 60_000_000;

    /// <summary>
    /// How long a file nobody refers to is kept before the daily cleanup removes it: the time to save it into the draft (a poor connection
    /// retries), to undo a removal and to come back from a night's break. A file the draft, the published version or the one before it
    /// points to is never removed, whatever its age.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);

    /// <summary>Files removed in one run of the cleanup: the next run takes the rest.</summary>
    public const int CleanupBatch = 500;
}

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the errors of the
/// images of a showcase (SP-13b). 413 <see cref="TooLarge"/>, 415 <see cref="UnsupportedType"/>, 404 <see cref="NotFound"/>, 409
/// <see cref="InUse"/>, 422 every other one. The codes are snake_case and never renamed: the editor branches on them.
/// </summary>
public static class ShowcaseMediaErrors
{
    /// <summary>422: the request has no file.</summary>
    public const string None = "showcase_media_none";

    /// <summary>422: the kind is missing or is not one of <see cref="ShowcaseMediaKinds"/>.</summary>
    public const string KindInvalid = "showcase_media_kind_invalid";

    /// <summary>413: the file is larger than its kind allows (<c>maxBytes</c> in the response). A request larger than any file can be is a 413 too.</summary>
    public const string TooLarge = "showcase_media_too_large";

    /// <summary>415: the content is not a PNG, a JPEG or a WebP image (an SVG, a PDF, a text renamed to <c>.jpg</c>...). The name and the declared type do not count.</summary>
    public const string UnsupportedType = "showcase_media_unsupported_type";

    /// <summary>422: the file starts like an image of an accepted type but is not one a browser can draw (damaged, cut short) or is animated.</summary>
    public const string InvalidImage = "showcase_media_invalid_image";

    /// <summary>422: the picture has too many pixels (<c>maxSide</c> in the response).</summary>
    public const string Dimensions = "showcase_media_dimensions";

    /// <summary>422: the supplier already has as many files as it may store (<c>limit</c> in the response).</summary>
    public const string LimitReached = "showcase_media_limit_reached";

    /// <summary>404: no file of this supplier has this id.</summary>
    public const string NotFound = "showcase_media_not_found";

    /// <summary>409: the draft, the published version or the one before it still shows this file: take it out of the content first.</summary>
    public const string InUse = "showcase_media_in_use";

    /// <summary>The <c>SharedResources</c> keys the images use (Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "ShowcaseMediaNone",
        "ShowcaseMediaKindInvalid",
        "ShowcaseMediaLogoTooLarge",
        "ShowcaseMediaPhotoTooLarge",
        "ShowcaseMediaUnsupportedType",
        "ShowcaseMediaInvalidImage",
        "ShowcaseMediaDimensions",
        "ShowcaseMediaLimitReached",
        "ShowcaseMediaNotFound",
        "ShowcaseMediaInUse",
    ];

    public static DomainRuleException NoFile() => new(None, "ShowcaseMediaNone");

    public static DomainRuleException BadKind() => new(KindInvalid, "ShowcaseMediaKindInvalid")
    {
        Extensions = new Dictionary<string, object?> { ["field"] = "kind" },
    };

    /// <summary>The 413 of a file larger than its kind allows.</summary>
    public static DomainPayloadTooLargeException Large(string kind) => new(
        TooLarge,
        kind == ShowcaseMediaKinds.Logo ? "ShowcaseMediaLogoTooLarge" : "ShowcaseMediaPhotoTooLarge")
    {
        Extensions = new Dictionary<string, object?> { ["maxBytes"] = ShowcaseMediaKinds.MaxBytesOf(kind) },
    };

    /// <summary>The 415 of a content that is not an accepted image.</summary>
    public static DomainUnsupportedMediaTypeException WrongType() => new(UnsupportedType, "ShowcaseMediaUnsupportedType");

    public static DomainRuleException NotAnImage() => new(InvalidImage, "ShowcaseMediaInvalidImage");

    public static DomainRuleException TooManyPixels() => new(Dimensions, "ShowcaseMediaDimensions", ShowcaseMediaLimits.MaxSidePixels)
    {
        Extensions = new Dictionary<string, object?> { ["maxSide"] = ShowcaseMediaLimits.MaxSidePixels },
    };

    public static DomainRuleException TooManyFiles() => new(LimitReached, "ShowcaseMediaLimitReached", ShowcaseMediaLimits.MaxStoredFiles)
    {
        Extensions = new Dictionary<string, object?> { ["limit"] = ShowcaseMediaLimits.MaxStoredFiles },
    };

    public static NotFoundException Missing() => new("Showcase media not found") { Code = NotFound, MessageKey = "ShowcaseMediaNotFound" };

    public static DomainConflictException Used() => new(InUse, "ShowcaseMediaInUse");
}
