namespace Casazen.Core.Services;

public static class AiPhotoLimits
{
    public const int MaxPhotoBytes = 1_500_000;

    public const string InvalidCode = "ai_photos_invalid";

    public const long RequestOverheadBytes = 64 * 1024;
}
