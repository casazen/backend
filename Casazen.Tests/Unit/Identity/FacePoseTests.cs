using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US01/T03 (NF-192): the pose of a head from the five landmarks. The yaw is the estimate the legacy pose proof of the check-in always used, moved out of
/// <c>ActivePoseVerificationService</c> unchanged (its own tests stay green); the pitch is its counterpart, from the position of the nose between the line of the eyes and the
/// line of the mouth. Both are written here from the arithmetic of the model, independently of the code, and the constants of the pitch are geometric, not thresholds.
/// </summary>
public class FacePoseTests
{
    private const double Degrees = 180 / Math.PI;

    /// <summary>The landmarks of the face the legacy tests of the pose use: eyes 32 apart at y 22, nose at y 33, mouth at y 45; the nose moved by <paramref name="noseOffset"/> to the right.</summary>
    private static float[] LegacyFace(float noseOffset) => [16, 22, 48, 22, 32 + noseOffset, 33, 22, 45, 42, 45];

    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    [InlineData(10f)]
    [InlineData(-10f)]
    [InlineData(18f)]
    public void YawDegrees_IsTheEstimateOfTheActivePoseService_Unchanged(float noseOffset)
    {
        // The offset of the nose over the distance of the eyes (32), over the depth of the nose over the distance of the eyes (0.636267): the formula the legacy proof had.
        var expected = Math.Atan(noseOffset * 32 / (32.0 * 32.0) / 0.636267) * Degrees;

        Assert.Equal(expected, FacePose.YawDegrees(LegacyFace(noseOffset)), 6);
        Assert.Equal(expected, FacePose.Estimate(Face(LegacyFace(noseOffset))).YawDegrees, 6);
    }

    [Fact]
    public void YawDegrees_AFaceTurnedToTheRightOfThePicture_IsPositive_AndToTheLeftNegative()
    {
        Assert.InRange(FacePose.YawDegrees(LegacyFace(10)), 20, 30);
        Assert.InRange(FacePose.YawDegrees(LegacyFace(-10)), -30, -20);
        Assert.Equal(0, FacePose.YawDegrees(LegacyFace(0)), 9);
    }

    [Fact]
    public void YawDegrees_DoesNotChangeWithTheRollOrTheSizeOfTheFace()
    {
        var upright = FacePose.YawDegrees(LegacyFace(10));

        Assert.Equal(upright, FacePose.YawDegrees(Transform(LegacyFace(10), rollDegrees: 20, scale: 1)), 3);
        Assert.Equal(upright, FacePose.YawDegrees(Transform(LegacyFace(10), rollDegrees: -25, scale: 3)), 3);
        Assert.Equal(upright, FacePose.YawDegrees(Transform(LegacyFace(10), rollDegrees: 0, scale: 0.5)), 3);
    }

    [Theory]
    [MemberData(nameof(UnusableLandmarks))]
    public void Estimate_LandmarksThatAreNotAFace_GiveNoYawAndNoPitch(string reason, float[] landmarks)
    {
        var angles = FacePose.Estimate(Face(landmarks));

        Assert.True(double.IsNaN(angles.YawDegrees), $"The yaw of {reason} was estimated.");
        Assert.True(double.IsNaN(angles.PitchDegrees), $"The pitch of {reason} was estimated.");
        Assert.False(angles.HasYaw);
        Assert.False(angles.HasPitch);
    }

    public static TheoryData<string, float[]> UnusableLandmarks() => new()
    {
        { "eyes in the wrong order", [48, 22, 16, 22, 32, 33, 22, 45, 42, 45] },
        { "eyes closer than 8 pixels", [16, 22, 23, 22, 19, 25, 17, 28, 22, 28] },
        { "a head rolled by more than 31 degrees", [16, 22, 48, 45, 32, 40, 22, 60, 42, 60] },
        { "a nose above the line of the eyes", [16, 22, 48, 22, 32, 15, 22, 45, 42, 45] },
        { "a mouth above the nose", [16, 22, 48, 22, 32, 40, 22, 35, 42, 35] },
        { "a coordinate that is not a number", [16, 22, 48, 22, float.NaN, 33, 22, 45, 42, 45] },
        { "a coordinate that is infinite", [16, 22, 48, 22, 32, float.PositiveInfinity, 22, 45, 42, 45] },
        { "too few numbers", [1, 2, 3] },
    };

    [Fact]
    public void Pitch_TheFrontalFaceOfTheAlignmentTemplate_IsLevel()
    {
        // The template the recognition models align faces to (FaceAlignment.Template112): the constants of the pitch are its proportions, so it reads as a level face.
        float[] template = [38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f];

        var angles = FacePose.Estimate(Face(template));

        Assert.InRange(angles.PitchDegrees, -0.3, 0.3);
        Assert.InRange(angles.YawDegrees, -0.5, 0.5);
        Assert.True(angles.HasYaw);
        Assert.True(angles.HasPitch);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.20)]
    [InlineData(0.30)]
    public void Pitch_TheNoseTowardsTheEyes_IsAFaceLookingUp_AndTowardsTheMouthLookingDown(double shareOfEyesToMouth)
    {
        // The nose tip stands 0.495 of the way from the eyes to the mouth; moved by a share of that distance it is a pitch of atan(share / 0.551).
        var expected = Math.Atan(shareOfEyesToMouth / 0.551) * Degrees;

        Assert.Equal(expected, FacePose.PitchDegrees(Frontal(noseRatio: 0.495 - shareOfEyesToMouth)), 0);
        Assert.Equal(-expected, FacePose.PitchDegrees(Frontal(noseRatio: 0.495 + shareOfEyesToMouth)), 0);
        Assert.True(FacePose.PitchDegrees(Frontal(noseRatio: 0.495 - shareOfEyesToMouth)) > 0);
        Assert.True(FacePose.PitchDegrees(Frontal(noseRatio: 0.495 + shareOfEyesToMouth)) < 0);
    }

    [Fact]
    public void Pitch_IsAsTheModelSaysItIs_ForTheDepthOfTheNose()
    {
        // The model, written from its start: a nose tip 0.551 distances from the eyes to the mouth in front of the plane of the face, the head pitched by 15 degrees upward (the nose goes towards the eyes by the depth of the nose times the tangent of the pitch).
        var pitch = 15 * Math.PI / 180;
        var ratio = 0.495 - (0.551 * Math.Tan(pitch));

        Assert.Equal(15, FacePose.PitchDegrees(Frontal(noseRatio: ratio)), 1);
    }

    [Fact]
    public void Pitch_DoesNotChangeWithTheYawTheRollOrTheSizeOfTheFace()
    {
        var level = Frontal(noseRatio: 0.40);
        var pitch = FacePose.PitchDegrees(level);

        // The nose moved sideways (a turn) leaves the pitch alone.
        float[] turned = [.. level];
        turned[4] += 7;
        Assert.Equal(pitch, FacePose.PitchDegrees(turned), 6);
        Assert.Equal(pitch, FacePose.PitchDegrees(Transform(level, rollDegrees: 25, scale: 1)), 3);
        Assert.Equal(pitch, FacePose.PitchDegrees(Transform(level, rollDegrees: -20, scale: 2.5)), 3);
        Assert.Equal(pitch, FacePose.PitchDegrees(Transform(level, rollDegrees: 0, scale: 0.4)), 3);
    }

    [Fact]
    public void Pitch_IsBoundedByTheGeometry_AndNeedsAFaceThatIsUpright()
    {
        // The nose just under the eyes and just over the mouth: the most the model gives (about +42 and -42 degrees), not more.
        Assert.InRange(FacePose.PitchDegrees(Frontal(noseRatio: 0.01)), 40, 43);
        Assert.InRange(FacePose.PitchDegrees(Frontal(noseRatio: 0.99)), -43, -40);

        // The nose on the line of the eyes, above it, on the line of the mouth or below it, or the mouth above the eyes: no estimate.
        Assert.True(double.IsNaN(FacePose.PitchDegrees(Frontal(noseRatio: 0))));
        Assert.True(double.IsNaN(FacePose.PitchDegrees(Frontal(noseRatio: -0.2))));
        Assert.True(double.IsNaN(FacePose.PitchDegrees(Frontal(noseRatio: 1))));
        Assert.True(double.IsNaN(FacePose.PitchDegrees(Frontal(noseRatio: 1.3))));
        Assert.True(double.IsNaN(FacePose.PitchDegrees([16, 22, 48, 22, 32, 15, 22, 5, 42, 5])));
    }

    [Fact]
    public void SequenceTestFrames_WritesLandmarksThatTheEstimateReadsBack()
    {
        // The scripts of the tests of the validation write the head with the inverse of this estimate: they have to agree.
        foreach (var (yaw, pitch) in new[] { (0.0, 0.0), (30.0, 0.0), (-30.0, 0.0), (0.0, 18.0), (0.0, -18.0), (-25.0, 12.0) })
        {
            var angles = FacePose.Estimate(SequenceTestFrames.Face(640, 480, yaw, pitch));

            Assert.Equal(yaw, angles.YawDegrees, 1);
            Assert.Equal(pitch, angles.PitchDegrees, 1);
        }
    }

    /// <summary>A face of the model: the eyes 40 apart at y 40 (level), the mouth 46.2 below them (the proportion of the template), and the nose tip at <paramref name="noseRatio"/> of the way down.</summary>
    private static float[] Frontal(double noseRatio)
    {
        const double eyeDistance = 40;
        const double eyesToMouth = 1.1546 * eyeDistance;
        return [(float)(100 - (eyeDistance / 2)), 40, (float)(100 + (eyeDistance / 2)), 40, 100, (float)(40 + (noseRatio * eyesToMouth)), 82, (float)(40 + eyesToMouth), 118, (float)(40 + eyesToMouth)];
    }

    /// <summary>The landmarks turned by <paramref name="rollDegrees"/> about their middle and scaled.</summary>
    private static float[] Transform(float[] points, double rollDegrees, double scale)
    {
        var roll = rollDegrees * Math.PI / 180;
        double centreX = 0, centreY = 0;
        for (var index = 0; index < 5; index++)
        {
            centreX += points[2 * index] / 5;
            centreY += points[(2 * index) + 1] / 5;
        }

        var result = new float[10];
        for (var index = 0; index < 5; index++)
        {
            var x = points[2 * index] - centreX;
            var y = points[(2 * index) + 1] - centreY;
            result[2 * index] = (float)(centreX + (scale * ((x * Math.Cos(roll)) - (y * Math.Sin(roll)))));
            result[(2 * index) + 1] = (float)(centreY + (scale * ((x * Math.Sin(roll)) + (y * Math.Cos(roll)))));
        }

        return result;
    }

    private static DetectedFace Face(float[] landmarks) => new(0, 0, 100, 100, 0.99f, landmarks, 0.5);
}
