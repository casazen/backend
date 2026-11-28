using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

public static class ShowcaseMediaKinds
{
    public const string Logo = "logo";

    public const string Cover = "cover";

    public const string Work = "work";

    public const string Before = "before";

    public static IReadOnlyList<string> All { get; } = [Logo, Cover, Work, Before];

    public static bool TryParse(string? value, out string kind)
    {
        var text = value?.Trim().ToLowerInvariant();
        kind = All.FirstOrDefault(known => known == text) ?? string.Empty;
        return kind.Length > 0;
    }

    public static long MaxBytesOf(string kind) =>
        kind == Logo ? ShowcaseMediaLimits.LogoMaxBytes : ShowcaseMediaLimits.PhotoMaxBytes;
}

public static class ShowcaseMediaLimits
{
    public const int KindMaxLength = 16;
    public const int StorageKeyMaxLength = 300;
    public const int ContentTypeMaxLength = 32;

    public const long LogoMaxBytes = 200 * 1024;

    public const long PhotoMaxBytes = 10 * 1024 * 1024;

    public const long MaxRequestBytes = PhotoMaxBytes + (1024 * 1024);

    public const int MaxGalleryPhotos = ShowcaseContentLimits.GalleryMax * 2;

    public const int MaxStoredFiles = 60;

    public const int MaxSidePixels = 12_000;

    public const long MaxPixels = 60_000_000;

    /// <summary>
    /// How long a file nobody refers to is kept before the daily cleanup removes it: the time to save it into the draft (a poor connection
    /// retries), to undo a removal and to come back from a night's break. A file the draft, the published version or the one before it
    /// points to is never removed, whatever its age.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);

    public const int CleanupBatch = 500;
}

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the errors of the
/// images of a showcase (SP-13b). 413 <see cref="TooLarge"/>, 415 <see cref="UnsupportedType"/>, 404 <see cref="NotFound"/>, 409
/// <see cref="InUse"/>, 422 every other one. The codes are snake_case and never renamed: the editor branches on them.
/// </summary>
public static class ShowcaseMediaErrors
{
    public const string None = "showcase_media_none";

    public const string KindInvalid = "showcase_media_kind_invalid";

    public const string TooLarge = "showcase_media_too_large";

    public const string UnsupportedType = "showcase_media_unsupported_type";

    public const string InvalidImage = "showcase_media_invalid_image";

    public const string Dimensions = "showcase_media_dimensions";

    public const string LimitReached = "showcase_media_limit_reached";

    public const string NotFound = "showcase_media_not_found";

    public const string InUse = "showcase_media_in_use";

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

    public static DomainPayloadTooLargeException Large(string kind) => new(
        TooLarge,
        kind == ShowcaseMediaKinds.Logo ? "ShowcaseMediaLogoTooLarge" : "ShowcaseMediaPhotoTooLarge")
    {
        Extensions = new Dictionary<string, object?> { ["maxBytes"] = ShowcaseMediaKinds.MaxBytesOf(kind) },
    };

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
