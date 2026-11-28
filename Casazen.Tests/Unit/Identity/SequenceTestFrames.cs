using System.Security.Cryptography;
using Casazen.Core.Identity;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;

namespace Casazen.Tests.Unit.Identity;

public sealed class ScriptedFrame
{
    public long TimestampMs { get; set; }

    public byte[] Gray { get; set; } = [];

    public List<DetectedFace> Faces { get; set; } = [];

    public float[] Person { get; set; } = [];
}

/// <summary>
/// A sequence of frames written in code for the tests of the presence, with the challenge it answers: what the doubles of the face models need to say about each frame,
/// frame by frame (E72/F01/US01/T03, NF-192), never a photograph (the repository is public). <see cref="SequenceTestFrames"/> makes them; a test changes the frames and the
/// times, then <see cref="Run"/> builds the sequence of <c>RasterImage</c>s and the two doubles that agree with it.
/// </summary>
public sealed class ScriptedSequence
{
    public required StoredPresenceChallenge Challenge { get; set; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public List<ScriptedFrame> Frames { get; } = [];

    public TimeSpan ReceivedAfterIssue { get; set; }

    public long DurationMs => Frames.Count < 2 ? 0 : Frames[^1].TimestampMs - Frames[0].TimestampMs;

    internal ScriptedRun Run(IdentityDecisionPolicy policy)
    {
        var frames = new List<SequenceFrame>(Frames.Count);
        var answers = new List<IReadOnlyList<DetectedFace>>(Frames.Count);
        var embedder = new FrameKeyedEmbedder();
        var (shownWidth, shownHeight) = SequenceTestFrames.Shown(Width, Height, policy.Presence.Sequence.DetectorMaxSide);
        for (var index = 0; index < Frames.Count; index++)
        {
            var scripted = Frames[index];
            var rgb = new byte[Width * Height * 3];
            for (var pixel = 0; pixel < scripted.Gray.Length; pixel++)
            {
                rgb[pixel * 3] = scripted.Gray[pixel];
                rgb[(pixel * 3) + 1] = scripted.Gray[pixel];
                rgb[(pixel * 3) + 2] = scripted.Gray[pixel];
            }

            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. SHA256.HashData(scripted.Gray)[..12]];
            var image = new RasterImage(Width, Height, rgb);
            frames.Add(new SequenceFrame(index, scripted.TimestampMs, image, jpeg));
            answers.Add([.. scripted.Faces.Select(face => SequenceTestFrames.Scaled(face, (double)shownWidth / Width, (double)shownHeight / Height))]);
            embedder.Add(image, scripted.Person, index);
        }

        var sequence = new FrameSequence(frames, Challenge.IssuedAt + ReceivedAfterIssue);
        return new ScriptedRun(sequence, Challenge, new ScriptedFaceDetector([.. answers]), embedder);
    }
}

internal sealed class ScriptedRun(FrameSequence sequence, StoredPresenceChallenge challenge, ScriptedFaceDetector detector, FrameKeyedEmbedder embedder) : IDisposable
{
    public FrameSequence Sequence { get; } = sequence;

    public StoredPresenceChallenge Challenge { get; } = challenge;

    public ScriptedFaceDetector Detector { get; } = detector;

    public FrameKeyedEmbedder Embedder { get; } = embedder;

    public void Dispose() => Sequence.Dispose();
}

internal sealed class FrameKeyedEmbedder : IFaceEmbedder
{
    private readonly Dictionary<RasterImage, (float[] Vector, int Index)> _frames = new(ReferenceEqualityComparer.Instance);

    public List<float[]> Issued { get; } = [];

    public List<int> Embedded { get; } = [];

    public Exception? Failure { get; set; }

    public void Add(RasterImage image, float[] vector, int index) => _frames[image] = (vector, index);

    public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
    {
        if (Failure is not null)
            throw Failure;

        if (!_frames.TryGetValue(image, out var frame))
            throw new InvalidOperationException("The embedder was shown a picture that is not a frame of the sequence.");

        var issued = (float[])frame.Vector.Clone();
        Issued.Add(issued);
        Embedded.Add(frame.Index);
        return issued;
    }
}

/// <summary>
/// The scripted sequences of the presence (E72/F01/US01/T03, NF-192), public for the qualification tests of E74: a genuine guest who answers the challenge in order, and
/// each defect the validation must name: a still picture played again, moves out of order, a recording that is too short, one made before the challenge, two faces, two
/// people. The pose of the head follows the moves of the challenge with the numbers of the policy (a turn of 1.5 times the least yaw that counts, a look of 1.5 times the
/// least pitch), and the landmarks are written with the same model of a face that <c>FacePose</c> inverts. Every frame is random gray noise (so it has the light and the focus
/// of a crisp picture and differs from the others) with a face whose box and landmarks the script says.
/// </summary>
public static class SequenceTestFrames
{
    public static readonly DateTimeOffset IssuedAt = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private const double NoseDepthOverEyeDistance = 0.636267;
    private const double FrontalNoseRatio = 0.495;
    private const double NoseDepthOverEyesToMouth = 0.551;
    private const double EyesToMouthOverEyeDistance = 1.1546;

    public static IReadOnlyList<string> DefaultSteps { get; } = [PresenceActions.TurnLeft, PresenceActions.LookUp, PresenceActions.TurnRight];

    public static ScriptedSequence Genuine(
        IdentityDecisionPolicy policy,
        IReadOnlyList<string>? steps = null,
        IReadOnlyList<string>? performed = null,
        double amplitude = 1.5,
        int width = 96,
        int height = 72,
        double baselineYaw = 0,
        double baselinePitch = 0)
    {
        steps ??= DefaultSteps;
        performed ??= steps;
        var rules = policy.Presence.Challenge;
        var interval = (long)Math.Round(1000.0 / policy.Presence.Sequence.TargetFps);
        var duration = rules.LeadInMs + ((long)steps.Count * rules.StepMs) + (rules.StepMs / 2);
        var sequence = new ScriptedSequence
        {
            Challenge = new StoredPresenceChallenge(Guid.NewGuid(), steps, IssuedAt, IssuedAt.AddSeconds(rules.LifetimeSeconds), 1),
            Width = width,
            Height = height,
            ReceivedAfterIssue = TimeSpan.FromMilliseconds(duration + 1500),
        };
        for (long time = 0, index = 0; time <= duration; time += interval, index++)
        {
            var (yaw, pitch) = Pose(policy, performed, amplitude, time);
            sequence.Frames.Add(new ScriptedFrame
            {
                TimestampMs = time,
                Gray = TestJpeg.NoisePixels(width, height, 1000 + (int)index),
                Faces = [Face(width, height, baselineYaw + yaw, baselinePitch + pitch)],
                Person = Person(0),
            });
        }

        return sequence;
    }

    public static ScriptedSequence StaticReplay(IdentityDecisionPolicy policy, int noise = 0)
    {
        var sequence = Genuine(policy);
        var first = sequence.Frames[0];
        var random = new Random(7);
        foreach (var frame in sequence.Frames)
        {
            frame.Faces = [.. first.Faces];
            frame.Gray = [.. first.Gray];
            if (noise > 0 && frame != first)
            {
                for (var pixel = 0; pixel < frame.Gray.Length; pixel++)
                {
                    if (random.Next(2) == 0)
                        frame.Gray[pixel] = (byte)Math.Clamp(frame.Gray[pixel] + noise, 0, 255);
                }
            }
        }

        return sequence;
    }

    public static ScriptedSequence OutOfOrder(IdentityDecisionPolicy policy)
    {
        return Genuine(policy, performed: [PresenceActions.LookUp, PresenceActions.TurnLeft, PresenceActions.TurnRight]);
    }

    public static ScriptedSequence TooShort(IdentityDecisionPolicy policy)
    {
        var sequence = Genuine(policy);
        sequence.Frames.RemoveAll(frame => frame.TimestampMs > 2000);
        sequence.ReceivedAfterIssue = TimeSpan.FromMilliseconds(sequence.DurationMs + 1500);
        return sequence;
    }

    public static ScriptedSequence PreChallenge(IdentityDecisionPolicy policy)
    {
        var sequence = Genuine(policy);
        sequence.ReceivedAfterIssue = TimeSpan.FromMilliseconds(sequence.DurationMs - 1000);
        return sequence;
    }

    public static ScriptedSequence TwoFaces(IdentityDecisionPolicy policy)
    {
        var sequence = Genuine(policy);
        for (var index = 10; index < 15; index++)
            sequence.Frames[index].Faces.Add(Face(sequence.Width, sequence.Height, 0, 0, centerX: 0.18, centerY: 0.55, widthShare: 0.2));

        return sequence;
    }

    public static ScriptedSequence Splice(IdentityDecisionPolicy policy)
    {
        var sequence = Genuine(policy);
        for (var index = sequence.Frames.Count / 2; index < sequence.Frames.Count; index++)
            sequence.Frames[index].Person = Person(1);

        return sequence;
    }

    public static DetectedFace Face(
        int imageWidth,
        int imageHeight,
        double yawDegrees = 0,
        double pitchDegrees = 0,
        double centerX = 0.5,
        double centerY = 0.5,
        double widthShare = 0.5)
    {
        var boxWidth = imageWidth * widthShare;
        var boxHeight = boxWidth * 1.2;
        var centreX = imageWidth * centerX;
        var left = centreX - (boxWidth / 2);
        var top = (imageHeight * centerY) - (boxHeight / 2);
        var eyeDistance = 0.55 * boxWidth;
        var eyesToMouth = EyesToMouthOverEyeDistance * eyeDistance;
        var eyesY = top + (0.38 * boxHeight);
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;

        var half = eyeDistance * Math.Cos(yaw) / 2;
        var noseX = centreX + (NoseDepthOverEyeDistance * eyeDistance * Math.Sin(yaw));
        var noseY = eyesY + (eyesToMouth * ((FrontalNoseRatio * Math.Cos(pitch)) - (NoseDepthOverEyesToMouth * Math.Sin(pitch))));
        var mouthY = eyesY + (eyesToMouth * Math.Cos(pitch));
        var mouthHalf = 0.3 * boxWidth;
        float[] landmarks =
        [
            (float)(centreX - half), (float)eyesY,
            (float)(centreX + half), (float)eyesY,
            (float)noseX, (float)noseY,
            (float)(centreX - mouthHalf), (float)mouthY,
            (float)(centreX + mouthHalf), (float)mouthY,
        ];
        return new DetectedFace((float)left, (float)top, (float)boxWidth, (float)boxHeight, 0.99f, landmarks, boxWidth * boxHeight / ((double)imageWidth * imageHeight));
    }

    public static float[] Person(int person)
    {
        var vector = new float[8];
        vector[person] = 1;
        return vector;
    }

    internal static (int Width, int Height) Shown(int width, int height, int maxSide)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxSide)
            return (width, height);

        var scale = (double)maxSide / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    internal static DetectedFace Scaled(DetectedFace face, double scaleX, double scaleY)
    {
        var landmarks = new float[face.Landmarks.Length];
        for (var index = 0; index < landmarks.Length; index++)
            landmarks[index] = (float)(face.Landmarks[index] * (index % 2 == 0 ? scaleX : scaleY));

        return new DetectedFace(
            (float)(face.X * scaleX), (float)(face.Y * scaleY), (float)(face.Width * scaleX), (float)(face.Height * scaleY), face.Score, landmarks, face.AreaRatio);
    }

    private static (double Yaw, double Pitch) Pose(IdentityDecisionPolicy policy, IReadOnlyList<string> performed, double amplitude, long time)
    {
        var rules = policy.Presence.Challenge;
        var ramp = rules.StepMs / 4.0;
        for (var step = 0; step < performed.Count; step++)
        {
            var start = rules.LeadInMs + (step * (double)rules.StepMs);
            var end = start + rules.StepMs;
            if (time < start || time >= end)
                continue;

            var envelope = time < start + ramp ? Smooth((time - start) / ramp) : time > end - ramp ? Smooth((end - time) / ramp) : 1.0;
            var turn = amplitude * envelope * policy.Presence.Pose.TurnMinYawDegrees;
            var look = amplitude * envelope * policy.Presence.Pose.LookMinPitchDegrees;
            return performed[step] switch
            {
                PresenceActions.TurnLeft => (-turn, 0),
                PresenceActions.TurnRight => (turn, 0),
                PresenceActions.LookUp => (0, look),
                PresenceActions.LookDown => (0, -look),
                _ => (0, 0),
            };
        }

        return (0, 0);
    }

    private static double Smooth(double share) => share * share * (3 - (2 * share));
}
