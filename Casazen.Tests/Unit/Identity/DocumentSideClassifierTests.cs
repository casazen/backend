using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US01/T02 (NF-189): which side a picture shows and whether the two pictures of a document are two sides. The same photo twice, the same card photographed again and
/// two pictures that show the same side are <c>document_side_duplicate</c>; a picture of a side that is not the declared one is <c>document_wrong_side</c>; the front and the
/// back of the generated card pass. The distance that counts as the same picture is the one of the committed policy file, never a number of the test; the classifier has no I/O,
/// no logging and no state.
/// </summary>
public class DocumentSideClassifierTests
{
    private static readonly DocumentFormat Cie = new DocumentFormatRegistry(
        Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, AppContext.BaseDirectory).Get("it-cie-3.0")!;

    private static readonly DocumentSideClassifier Classifier = new();

    /// <summary>The widest distance a policy may allow (<c>perceptualDuplicateMaxDistance</c> goes from 0 to 64): every hash is within it of every other one.</summary>
    private const int WidestPolicyDistance = 64;

    /// <summary>The distance the committed policy file calls the same side photographed twice.</summary>
    private static int PolicyDistance()
    {
        Assert.True(IdentityConfigurationJson.TryCanonicalize(Encoding.UTF8.GetBytes(IdentityPolicyTestFiles.ShippedText()), out var canonical));
        return IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!.Document.PerceptualDuplicateMaxDistance;
    }

    private static DocumentGeometryReport Geometry(bool? portraitInZone) =>
        new(true, new NormalizedBox(0.1, 0.1, 0.8, 0.7), true, 1.58, true, 0, portraitInZone, 0, 0.4, [], DocumentGeometryRules.Version);

    private static DocumentPictureFingerprint Picture(string sha, ulong? hash, DocumentSide? side) => new(sha, hash, side);

    private static string Sha(RasterImage picture) => Convert.ToHexString(SHA256.HashData(picture.Rgb));

    /// <summary>A passport as a format of one side: the portrait and the MRZ on the same page.</summary>
    private static DocumentFormat Passport() => Cie with
    {
        Id = "it-passport-test",
        Sides = ["single"],
        Mrz = new DocumentFormatMrz("TD3", "single", "ITA"),
        Layout = Cie.Layout with
        {
            Sides = new Dictionary<string, DocumentFormatSideLayout>
            {
                ["single"] = new([], new DocumentFormatRegion(0.05, 0.2, 0.28, 0.5), new DocumentFormatRegion(0.02, 0.78, 0.96, 0.2)),
            },
        },
    };

    /// <summary>A card of two sides whose MRZ is on the front, next to the portrait: the back has neither.</summary>
    private static DocumentFormat CardWithTheMrzOnTheFront() => Cie with
    {
        Id = "it-card-mrz-front-test",
        Mrz = new DocumentFormatMrz("TD1", "front", "ITA"),
        Layout = Cie.Layout with
        {
            Sides = new Dictionary<string, DocumentFormatSideLayout>
            {
                ["front"] = new([], new DocumentFormatRegion(0.03, 0.2, 0.3, 0.5), new DocumentFormatRegion(0.02, 0.75, 0.96, 0.23)),
                ["back"] = new([]),
            },
        },
    };

    // ---- Classify ----------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Classify_ThePortraitInItsZoneAndNoMrz_IsTheFront()
    {
        Assert.Equal(DocumentSide.Front, Classifier.Classify(Geometry(portraitInZone: true), mrzFound: false, Cie));
    }

    [Fact]
    public void Classify_TheMrz_IsTheBackOfTheItalianCard()
    {
        Assert.Equal(DocumentSide.Back, Classifier.Classify(Geometry(portraitInZone: false), mrzFound: true, Cie));
        Assert.Equal(DocumentSide.Back, Classifier.Classify(Geometry(portraitInZone: null), mrzFound: true, Cie));
    }

    [Fact]
    public void Classify_TheMrzAndAFaceTogether_IsTheBack_SinceTheFrontHasNoMrz()
    {
        Assert.Equal(DocumentSide.Back, Classifier.Classify(Geometry(portraitInZone: true), mrzFound: true, Cie));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Classify_NeitherTheMrzNorThePortrait_IsUnknown(bool? portraitInZone)
    {
        Assert.Null(Classifier.Classify(Geometry(portraitInZone), mrzFound: false, Cie));
    }

    [Fact]
    public void Classify_ACardWithTheMrzOnTheFront_RecognizesTheFrontAndNotTheBack()
    {
        var card = CardWithTheMrzOnTheFront();

        Assert.Equal(DocumentSide.Front, Classifier.Classify(Geometry(portraitInZone: false), mrzFound: true, card));
        Assert.Equal(DocumentSide.Front, Classifier.Classify(Geometry(portraitInZone: true), mrzFound: false, card));
        Assert.Null(Classifier.Classify(Geometry(portraitInZone: false), mrzFound: false, card));
    }

    [Theory]
    [InlineData(true, false, DocumentSide.Single)]
    [InlineData(false, true, DocumentSide.Single)]
    [InlineData(true, true, DocumentSide.Single)]
    [InlineData(false, false, null)]
    public void Classify_AFormatOfOneSide_HasNothingToTellApart(bool portrait, bool mrz, DocumentSide? expected)
    {
        Assert.Equal(expected, Classifier.Classify(Geometry(portrait), mrz, Passport()));
    }

    [Fact]
    public void Classify_NothingButTheGeometryTheFlagAndTheFormat_IsAskedFor()
    {
        Assert.Throws<ArgumentNullException>(() => Classifier.Classify(null!, false, Cie));
        Assert.Throws<ArgumentNullException>(() => Classifier.Classify(Geometry(true), false, null!));
    }

    // ---- CheckPair ---------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CheckPair_TheSameFileTwice_IsADuplicateSide()
    {
        using var front = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var same = new RasterImage(front.Width, front.Height, (byte[])front.Rgb.Clone());
        var other = Picture(Sha(front), PerceptualHash.DHash64(front), DocumentSide.Front);

        // The second picture is the first one sent again for the other side; it shows the front twice, and the bytes are the same.
        var code = Classifier.CheckPair(DocumentSide.Back, Picture(Sha(same), ulong.MaxValue, null), other, PolicyDistance());

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, code);
    }

    [Fact]
    public void CheckPair_TheSameDigestInAnotherCase_IsADuplicateSide()
    {
        var code = Classifier.CheckPair(
            DocumentSide.Back, Picture("ab12cd34ef56", 0, null), Picture("AB12CD34EF56", ulong.MaxValue, null), perceptualDuplicateMaxDistance: 0);

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, code);
    }

    [Fact]
    public void CheckPair_TheSameCardPhotographedAgainAHairShiftedAndBrighter_IsADuplicateSide()
    {
        using var first = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var shifted = DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(81, 71, 480, 303));
        using var again = DocumentImageFactory.Brightened(shifted, 12);
        var stored = Picture(Sha(first), PerceptualHash.DHash64(first), null);
        var current = Picture(Sha(again), PerceptualHash.DHash64(again), null);

        var code = Classifier.CheckPair(DocumentSide.Back, current, stored, PolicyDistance());

        Assert.NotEqual(Sha(first), Sha(again));
        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, code);
    }

    [Fact]
    public void CheckPair_TheFrontAndTheBackOfTheGeneratedCard_AreTwoSides()
    {
        using var front = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var back = DocumentImageFactory.Render(Cie, DocumentSide.Back, DocumentImageFactory.WholeCard);
        var stored = Picture(Sha(front), PerceptualHash.DHash64(front), DocumentSide.Front);
        var current = Picture(Sha(back), PerceptualHash.DHash64(back), DocumentSide.Back);

        Assert.Null(Classifier.CheckPair(DocumentSide.Back, current, stored, PolicyDistance()));
        Assert.Null(Classifier.CheckPair(DocumentSide.Front, stored, current, PolicyDistance()));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(10, false)]
    public void CheckPair_TheDistanceOfTheHashes_IsComparedWithTheOneOfThePolicy(int pastThePolicyDistance, bool duplicate)
    {
        // Two hashes that differ in exactly as many bits as the policy allows, plus the offset: the first `differing` bits of the other hash are set.
        var differing = PolicyDistance() + pastThePolicyDistance;
        var other = differing >= 64 ? ulong.MaxValue : (1UL << differing) - 1;
        Assert.Equal(differing, PerceptualHash.Distance(0, other));

        var code = Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, null), Picture("BB", other, null), PolicyDistance());

        Assert.Equal(duplicate ? IdentityGuidanceCodes.DocumentSideDuplicate : null, code);
    }

    [Fact]
    public void CheckPair_TheLargestDistanceIsTheOneOfThePolicyAndItComesFromTheArgument()
    {
        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, null), Picture("BB", 0b1111, null), 4));
        Assert.Null(Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, null), Picture("BB", 0b1111, null), 3));
    }

    [Fact]
    public void CheckPair_AMissingHashOnOnePicture_IsNeverADuplicateByTheHashAlone()
    {
        // 0 is the hash of a plain picture, so a missing hash must not read as 0: nothing is close to a hash that is not there, whichever picture lacks it.
        Assert.Null(Classifier.CheckPair(DocumentSide.Back, Picture("AA", null, null), Picture("BB", 0, null), PolicyDistance()));
        Assert.Null(Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, null), Picture("BB", null, null), PolicyDistance()));
        Assert.Null(Classifier.CheckPair(DocumentSide.Back, Picture("AA", null, null), Picture("BB", 0b101, null), WidestPolicyDistance));
    }

    [Fact]
    public void CheckPair_AMissingHashOnBothPictures_IsNeverADuplicateByTheHashAlone()
    {
        // Even with the widest distance a policy may allow (64: every hash is close to every other one), two pictures with no hash are not told apart by the hash.
        Assert.Null(Classifier.CheckPair(DocumentSide.Back, Picture("AA", null, null), Picture("BB", null, null), PolicyDistance()));
        Assert.Null(Classifier.CheckPair(DocumentSide.Back, Picture("AA", null, null), Picture("BB", null, null), WidestPolicyDistance));
        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, null), Picture("BB", ulong.MaxValue, null), WidestPolicyDistance));
    }

    [Fact]
    public void CheckPair_AMissingHash_LeavesTheSha256AndTheSideToSayThePicturesAreTheSame()
    {
        var sameFile = Classifier.CheckPair(DocumentSide.Back, Picture("ab12cd", null, null), Picture("AB12CD", null, null), PolicyDistance());
        var sameSide = Classifier.CheckPair(DocumentSide.Back, Picture("AA", null, DocumentSide.Front), Picture("BB", null, DocumentSide.Front), PolicyDistance());

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, sameFile);
        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, sameSide);
    }

    [Fact]
    public void CheckPair_TwoPicturesThatShowTheSameSide_AreADuplicateSideWhateverTheirHashes()
    {
        var code = Classifier.CheckPair(
            DocumentSide.Back, Picture("AA", 0, DocumentSide.Front), Picture("BB", ulong.MaxValue, DocumentSide.Front), PolicyDistance());

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, code);
    }

    [Fact]
    public void CheckPair_ADeclaredFrontThatIsABack_IsTheWrongSide_AndTheGuestIsAskedForTheOtherOne()
    {
        var code = Classifier.CheckPair(DocumentSide.Front, Picture("AA", 0, DocumentSide.Back), other: null, PolicyDistance());

        Assert.Equal(IdentityGuidanceCodes.DocumentWrongSide, code);
    }

    [Fact]
    public void CheckPair_ADeclaredBackThatIsAFront_IsTheWrongSide()
    {
        var code = Classifier.CheckPair(
            DocumentSide.Back, Picture("AA", 0, DocumentSide.Front), Picture("BB", ulong.MaxValue, DocumentSide.Back), PolicyDistance());

        Assert.Equal(IdentityGuidanceCodes.DocumentWrongSide, code);
    }

    [Fact]
    public void CheckPair_ADuplicateIsToldBeforeAWrongSide_SinceTheGuestIsAskedForTheOtherSideEitherWay()
    {
        var code = Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, DocumentSide.Front), Picture("AA", 0, DocumentSide.Front), PolicyDistance());

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, code);
    }

    [Theory]
    [InlineData(DocumentSide.Front, DocumentSide.Front)]
    [InlineData(DocumentSide.Back, DocumentSide.Back)]
    public void CheckPair_APictureOfTheDeclaredSideNextToTheOtherSide_IsFine(DocumentSide declared, DocumentSide shown)
    {
        var otherSide = shown == DocumentSide.Front ? DocumentSide.Back : DocumentSide.Front;

        var code = Classifier.CheckPair(declared, Picture("AA", 0, shown), Picture("BB", ulong.MaxValue, otherSide), PolicyDistance());

        Assert.Null(code);
    }

    [Fact]
    public void CheckPair_APictureOfAnUnknownSide_IsNeitherAWrongSideNorADuplicateByItsSide()
    {
        var code = Classifier.CheckPair(DocumentSide.Back, Picture("AA", 0, null), Picture("BB", ulong.MaxValue, null), PolicyDistance());

        Assert.Null(code);
    }

    [Fact]
    public void CheckPair_ASideDeclaredAsSingle_IsNeverTheWrongSide()
    {
        Assert.Null(Classifier.CheckPair(DocumentSide.Single, Picture("AA", 0, DocumentSide.Front), other: null, PolicyDistance()));
        Assert.Null(Classifier.CheckPair(DocumentSide.Single, Picture("AA", 0, DocumentSide.Single), other: null, PolicyDistance()));
    }

    [Fact]
    public void CheckPair_AnEmptyDigest_IsNotTheSameFile()
    {
        // The flow keeps an empty digest while only the back was sent: two empty digests are not the same file, only the hashes and the sides can say it.
        var code = Classifier.CheckPair(DocumentSide.Back, Picture(string.Empty, 0, null), Picture(string.Empty, ulong.MaxValue, null), PolicyDistance());

        Assert.Null(code);
    }

    // ---- no I/O, no logging, nothing kept -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheClassifier_HasNoStateNoDependencyAndNoPictureToLeakThrough()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var type = typeof(DocumentSideClassifier);

        Assert.Empty(type.GetFields(all));
        Assert.All(type.GetConstructors(), constructor => Assert.Empty(constructor.GetParameters()));
        var parameterTypes = type.GetMethods(all).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)).Distinct().ToList();
        Assert.DoesNotContain(parameterTypes, parameter => typeof(RasterImage).IsAssignableFrom(parameter) || typeof(Stream).IsAssignableFrom(parameter) || parameter == typeof(byte[]));
        Assert.DoesNotContain(parameterTypes, parameter => parameter.Namespace is { } name && name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePictureFingerprint_NeverPrintsItsValues()
    {
        var fingerprint = Picture("A1B2C3D4E5F60718", 0xDEAD_BEEF_0BAD_F00DUL, DocumentSide.Front);

        var text = fingerprint.ToString();

        Assert.DoesNotContain("A1B2C3D4", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEADBEEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(0xDEAD_BEEF_0BAD_F00DUL.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Front", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCasazenServices_RegistersTheClassifierAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddJsonFile(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false).Build());
        services.AddLogging();
        services.AddCasazenServices();
        using var provider = services.BuildServiceProvider();

        var classifier = provider.GetRequiredService<DocumentSideClassifier>();

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DocumentSideClassifier)).Lifetime);
        Assert.Same(classifier, provider.GetRequiredService<DocumentSideClassifier>());
    }
}
