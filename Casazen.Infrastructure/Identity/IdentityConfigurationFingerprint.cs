using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// What a decision of the self-hosted verification is bound to: the policy and the weights of the models that decided (NF-194, ADR-013-R19). A segment is
/// qualified for one policy version, one policy hash and one weights hash; a change of any of them gives another value here and the segment is back in
/// observation. Hashes only: never a score, a path or a value of a person.
/// </summary>
/// <param name="PolicyVersion">The version of the policy file in force, for example <c>identity-policy-2026.11-1</c>.</param>
/// <param name="PolicyHash">The canonical SHA-256 of the policy file in force (64 lower case hexadecimal characters).</param>
/// <param name="WeightsHash">
/// The SHA-256 of the weight hashes of the three models, joined in role order (64 lower case hexadecimal characters); see
/// <see cref="IdentityConfigurationFingerprint.ComputeWeightsHash"/>.
/// </param>
/// <param name="Fingerprint">The SHA-256 of the policy hash and the weights hash (64 lower case hexadecimal characters); see <see cref="IdentityConfigurationFingerprint.ComputeFingerprint"/>.</param>
public sealed record IdentityFingerprint(string PolicyVersion, string PolicyHash, string WeightsHash, string Fingerprint);

/// <summary>
/// The fingerprint of the configuration a self-hosted verification runs with (E70/F01/US02/T01, NF-194, ADR-013-R19): the canonical hash of the policy in force and
/// the weight hashes of the three models the store has loaded (<see cref="IBiometricModelStore.Snapshot"/>, the hash that was proved for each file).
/// <para>
/// <b>Recipe</b> (every hash in lower case hexadecimal, texts as UTF-8): <c>WeightsHash</c> = SHA-256 of <c>detector:antiSpoofing:recognition</c>, the weight
/// hashes in the role order <c>face_detector</c>, <c>anti_spoofing</c>, <c>face_recognition</c>; <c>Fingerprint</c> = SHA-256 of <c>policyHash:weightsHash</c>.
/// Changing the policy (another hash) or any weight (another hash, or the roles swapped) therefore changes the fingerprint.
/// </para>
/// <para>
/// <b>Null is a stop.</b> <see cref="Current"/> is null when the policy is unavailable, when the store is not ready, or when any of the three models is not loaded
/// or has no proved hash: there is no fingerprint, so no segment can be qualified for it and nothing is approved automatically. It never throws.
/// </para>
/// </summary>
public sealed class IdentityConfigurationFingerprint(IdentityPolicyProvider policy, IBiometricModelStore models)
{
    private static readonly string[] RoleOrder = [BiometricModelRoles.FaceDetector, BiometricModelRoles.AntiSpoofing, BiometricModelRoles.FaceRecognition];

    public IdentityFingerprint? Current()
    {
        var snapshot = policy.Snapshot;
        if (!snapshot.IsReady)
            return null;

        var weightsHash = ComputeWeightsHash(models.Snapshot);
        if (weightsHash is null)
            return null;

        return new IdentityFingerprint(snapshot.Policy.Version, snapshot.Hash, weightsHash, ComputeFingerprint(snapshot.Hash, weightsHash));
    }

    /// <summary>
    /// The weights hash of a reading of the store: the SHA-256 of the three weight hashes joined with <c>:</c> in role order; null unless the store is ready and
    /// every one of the three models is loaded with a proved SHA-256 (64 hexadecimal characters).
    /// </summary>
    public static string? ComputeWeightsHash(BiometricModelsSnapshot snapshot)
    {
        if (!snapshot.IsReady)
            return null;

        var hashes = new List<string>(RoleOrder.Length);
        foreach (var role in RoleOrder)
        {
            var info = snapshot.Models.FirstOrDefault(model => string.Equals(model.Role, role, StringComparison.Ordinal));
            if (info is not { Loaded: true, WeightHash: { } weightHash } || !IsSha256(weightHash))
                return null;

            hashes.Add(weightHash.ToLowerInvariant());
        }

        return IdentityConfigurationJson.Sha256Hex(string.Join(':', hashes));
    }

    public static string ComputeFingerprint(string policyHash, string weightsHash)
    {
        return IdentityConfigurationJson.Sha256Hex(string.Concat(policyHash.ToLowerInvariant(), ":", weightsHash.ToLowerInvariant()));
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(char.IsAsciiHexDigit);
    }
}
