using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The turn of a head about the vertical axis (yaw) and the horizontal one (pitch), in degrees, as <see cref="FacePose.Estimate"/> reads them from the five landmarks of
/// the face (E72/F01/US01/T03, NF-192). <see cref="double.NaN"/> where the landmarks do not allow an estimate (see <see cref="FacePose"/>). Numbers of a person: kept in
/// memory for the request, never in a log, a column or a job.
/// </summary>
/// <param name="YawDegrees">Positive when the nose moves to the right of the picture as the server received it, negative to the left.</param>
/// <param name="PitchDegrees">Positive when the face looks up, negative when it looks down.</param>
public readonly record struct FacePoseAngles(double YawDegrees, double PitchDegrees)
{
    /// <summary>Whether the yaw could be estimated.</summary>
    public bool HasYaw => double.IsFinite(YawDegrees);

    /// <summary>Whether the pitch could be estimated.</summary>
    public bool HasPitch => double.IsFinite(PitchDegrees);
}

/// <summary>
/// The pose of a head from the five landmarks YuNet finds (the right eye, the left eye, the nose tip and the two corners of the mouth, "right" being the right of the
/// person, so the left of the picture): a weak-perspective estimate that serves to tell a deliberate turn from a still face, not an anatomical measurement. The yaw is
/// the one the legacy pose proof of the check-in always used, moved here unchanged (E72/F01/US01/T03); the pitch is its counterpart. Landmark error and the anatomy of the
/// person move both a few degrees, so a move is always measured against the pose of the same person at rest (the baseline of the challenge), never against zero.
/// <para>
/// <b>Yaw.</b> Under a turn the nose, which stands out of the plane of the eyes, moves along the eye line by <c>depth x sin(yaw)</c> while the eyes come closer by
/// <c>cos(yaw)</c>: the offset of the nose from the middle of the eyes over the distance between the eyes is <c>0.636267 x tan(yaw)</c>, where 0.636267 is the depth of
/// the nose tip over the eye distance in MediaPipe's canonical face model. The roll of the head is taken out by measuring the offset along the line of the eyes.
/// </para>
/// <para>
/// <b>Pitch.</b> The same model about the horizontal axis. Measured on the vertical axis of the face (the perpendicular of the eye line, down the picture) from the middle
/// of the eyes, the mouth is at a distance <c>D</c> and the nose tip at <c>r x D</c>. Under a pitch of <c>p</c> (positive looking up) the eyes and the mouth, in the plane
/// of the face, keep their ratio and the nose, which stands out of that plane by <c>k x D</c>, moves: <c>r = r0 - k x tan(p)</c>. So the nose goes towards the eyes when the
/// face looks up and towards the mouth when it looks down, and <c>p = atan((r0 - r) / k)</c>. <c>r0</c> and <c>k</c> are geometric constants, <b>not decision thresholds</b>:
/// they are the proportions of a face, from the alignment template of the recognition models (<c>FaceAlignment</c>, in a square of 112 pixels: eyes 35.24 pixels apart at
/// 51.6, nose tip at 71.7, mouth at 92.3, so the nose tip is at 0.495 of the way from the eyes to the mouth and the eyes to the mouth is 40.69 pixels) and the depth of the
/// nose tip that the yaw already uses (0.636267 eye distances, which is 0.551 times the distance from the eyes to the mouth). How far a head must move for a move of
/// the challenge to count is the policy's (<c>presence.pose</c>), never written here.
/// </para>
/// </summary>
public static class FacePose
{
    /// <summary>The eye distance in the alignment template of the recognition models (<c>FaceAlignment.Template112</c>): the x of the two eyes, 73.5318 and 38.2946.</summary>
    private const double TemplateEyeDistance = 73.5318 - 38.2946;

    /// <summary>The distance from the line of the eyes to the line of the mouth in that template: the mean y of the corners of the mouth, 92.3655 and 92.2041, over that of the eyes, 51.6963 and 51.5014.</summary>
    private const double TemplateEyesToMouth = ((92.3655 + 92.2041) / 2) - ((51.6963 + 51.5014) / 2);

    /// <summary>The depth of the nose tip over the eye distance in MediaPipe's canonical face model: the constant of the yaw.</summary>
    private const double NoseDepthOverEyeDistance = 0.636267;

    /// <summary>
    /// Where the nose tip stands between the line of the eyes (0) and the line of the mouth (1) in a frontal face: the y of the nose tip of the template, 71.7366, from
    /// the line of the eyes, over the distance from the eyes to the mouth (about 0.495). Geometric, not a threshold.
    /// </summary>
    private const double FrontalNoseRatio = (71.7366 - ((51.6963 + 51.5014) / 2)) / TemplateEyesToMouth;

    /// <summary>
    /// The depth of the nose tip over the distance from the eyes to the mouth (about 0.551): the depth of the yaw, 0.636267 eye distances, in units of that distance.
    /// Geometric, not a threshold.
    /// </summary>
    private const double NoseDepthOverEyesToMouth = NoseDepthOverEyeDistance * TemplateEyeDistance / TemplateEyesToMouth;

    /// <summary>The yaw and the pitch of <paramref name="face"/>, <see cref="double.NaN"/> where the landmarks do not allow an estimate.</summary>
    public static FacePoseAngles Estimate(DetectedFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        return new FacePoseAngles(YawDegrees(face.Landmarks), PitchDegrees(face.Landmarks));
    }

    /// <summary>
    /// The yaw in degrees from ten numbers (x and y of the five landmarks, in the order of <see cref="DetectedFace.Landmarks"/>); positive when the nose is to the right of
    /// the middle of the eyes in the picture. <see cref="double.NaN"/> when there are not ten finite numbers, the eyes are not in their order, less than 8 pixels apart or
    /// rolled by more than about 31 degrees, or the nose is not under the eyes and the mouth under the nose.
    /// </summary>
    public static double YawDegrees(float[] points)
    {
        if (points.Length != 10 || !points.All(float.IsFinite))
            return double.NaN;

        double dx = points[2] - points[0], dy = points[3] - points[1];
        var eyeDistanceSquared = dx * dx + dy * dy;
        if (dx <= 0 || eyeDistanceSquared < 64 || Math.Abs(dy) > dx * 0.6)
            return double.NaN;

        var noseX = points[4] - (points[0] + points[2]) * 0.5;
        var noseY = points[5] - (points[1] + points[3]) * 0.5;
        var mouthX = (points[6] + points[8]) * 0.5 - points[4];
        var mouthY = (points[7] + points[9]) * 0.5 - points[5];
        if (dx * noseY <= dy * noseX || dx * mouthY <= dy * mouthX)
            return double.NaN;

        // Under yaw, nose displacement projects with sin(yaw) and eye separation with cos(yaw).
        // Nose depth / frontal eye-centre separation from MediaPipe's canonical_face_model.obj (NoseDepthOverEyeDistance):
        // nose landmark 1, eye centres from the midpoints of 33/133 and 263/362.
        var offset = (noseX * dx + noseY * dy) / eyeDistanceSquared;
        return Math.Atan(offset / NoseDepthOverEyeDistance) * (180 / Math.PI);
    }

    /// <summary>
    /// The pitch in degrees from the same ten numbers; positive when the face looks up, negative when it looks down. <see cref="double.NaN"/> under the same conditions as
    /// the yaw on the eyes, and when the nose is not below the line of the eyes or the mouth not below the nose along the vertical axis of the face. Between about -42 and
    /// +42 degrees by construction.
    /// </summary>
    public static double PitchDegrees(float[] points)
    {
        if (points.Length != 10 || !points.All(float.IsFinite))
            return double.NaN;

        double dx = points[2] - points[0], dy = points[3] - points[1];
        var eyeDistanceSquared = dx * dx + dy * dy;
        if (dx <= 0 || eyeDistanceSquared < 64 || Math.Abs(dy) > dx * 0.6)
            return double.NaN;

        // The vertical axis of the face: the unit vector perpendicular to the line of the eyes, pointing down the picture. Distances are measured on it from the
        // middle of the eyes, which takes the roll of the head out of the estimate.
        var eyeDistance = Math.Sqrt(eyeDistanceSquared);
        var downX = -dy / eyeDistance;
        var downY = dx / eyeDistance;
        var eyesX = (points[0] + points[2]) * 0.5;
        var eyesY = (points[1] + points[3]) * 0.5;
        var noseDown = ((points[4] - eyesX) * downX) + ((points[5] - eyesY) * downY);
        var mouthDown = ((((points[6] + points[8]) * 0.5) - eyesX) * downX) + ((((points[7] + points[9]) * 0.5) - eyesY) * downY);
        if (noseDown <= 0 || mouthDown <= noseDown)
            return double.NaN;

        var noseRatio = noseDown / mouthDown;
        return Math.Atan((FrontalNoseRatio - noseRatio) / NoseDepthOverEyesToMouth) * (180 / Math.PI);
    }
}
