using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AiPhotosTests
{
    private const int MaxPhotos = 6;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01];

    [Fact]
    public void Decode_ImagesWithOrWithoutDataPrefix_KeepsTheOrderAndTheRealType()
    {
        var base64 = Convert.ToBase64String(Jpeg);

        var images = AiPhotos.Decode([$"data:image/png;base64,{base64}", base64], MaxPhotos);

        Assert.Equal(2, images.Count);
        Assert.All(images, image => Assert.Equal("image/jpeg", image.MediaType));
        Assert.Equal(Jpeg, images[0].Data);
    }

    [Fact]
    public void Decode_NoPhoto_IsEmpty()
    {
        Assert.Empty(AiPhotos.Decode([], MaxPhotos));
    }

    [Fact]
    public void Decode_MorePhotosThanTheEndpointTakes_IsRefused()
    {
        var photo = Convert.ToBase64String(Jpeg);

        var error = Assert.Throws<DomainRuleException>(() => AiPhotos.Decode([photo, photo, photo], maxPhotos: 2));
        Assert.Equal(AiPhotoLimits.InvalidCode, error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("non è base64!")]
    [InlineData("SGVsbG8sIHdvcmxkIQ==")]
    public void Decode_NotAnImage_IsRefused(string photo)
    {
        var error = Assert.Throws<DomainRuleException>(() => AiPhotos.Decode([photo], MaxPhotos));
        Assert.Equal(AiPhotoLimits.InvalidCode, error.Code);
    }

    [Fact]
    public void Decode_LargerThanTheLimit_IsRefused()
    {
        var large = new byte[AiPhotoLimits.MaxPhotoBytes + 1];
        Jpeg.CopyTo(large, 0);

        var error = Assert.Throws<DomainRuleException>(() => AiPhotos.Decode([Convert.ToBase64String(large)], MaxPhotos));
        Assert.Equal(AiPhotoLimits.InvalidCode, error.Code);
    }
}
