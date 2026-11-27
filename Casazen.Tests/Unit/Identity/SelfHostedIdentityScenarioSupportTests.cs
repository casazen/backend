using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F02/US01/T02: the parts of the builder of the acceptance scenario that need no host, proved where they can run on any machine (the HTTP scenario itself runs in CI, over a
/// database): the policy the host is configured with is usable, pinned by its canonical hash and calibrated; the store of the models gives a fingerprint that a new weight changes; and the
/// doubles of the models take the generated card for a document, only when they are given the stand-in of the portrait to answer for what the script does not know.
/// </summary>
public sealed class SelfHostedIdentityScenarioSupportTests : IDisposable
{
    private const string Version = "identity-policy-scenario-2026.11-1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-scenario-support-" + Guid.NewGuid().ToString("N"));

    public SelfHostedIdentityScenarioSupportTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ScenarioSupport_TheCalibratedPolicy_IsUsable_PinnedByItsCanonicalHash_AndNotProvisional()
    {
        var (path, hash, policy) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);

        var snapshot = Provider(path, hash).Snapshot;

        Assert.True(snapshot.IsReady);
        Assert.False(snapshot.Provisional);
        Assert.Equal((Version, hash), (snapshot.Policy!.Version, snapshot.Hash));
        Assert.Equal(policy.Version, snapshot.Policy.Version);
        Assert.False(policy.Provisional);

        // The committed file is not the one the scenario runs with: it stays provisional, as it is committed.
        Assert.True(IdentityTestPolicy.Shipped().Provisional);
    }

    [Fact]
    public void ScenarioSupport_AnotherVersion_IsAnotherFileWithAnotherHash_AndAPinIsOfOneFileOnly()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        var first = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);
        var second = SelfHostedIdentityScenario.WriteCalibratedPolicy(other, "identity-policy-scenario-2026.11-2");

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(Provider(second.Path, second.Hash).Snapshot.IsReady);
        Assert.False(Provider(second.Path, first.Hash).Snapshot.IsReady);
    }

    [Fact]
    public void ScenarioSupport_TheStoreOfTheModels_GivesAFingerprint_AndANewWeightChangesIt()
    {
        var (path, hash, _) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);
        var store = new ScenarioModelStore();
        var fingerprints = new IdentityConfigurationFingerprint(Provider(path, hash), store);

        var before = fingerprints.Current();
        store.ChangeWeights(BiometricModelRoles.FaceRecognition);
        var after = fingerprints.Current();

        Assert.True(store.IsAvailable);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(hash, before.PolicyHash, ignoreCase: true);
        Assert.Equal(before.PolicyHash, after.PolicyHash);
        Assert.NotEqual(before.WeightsHash, after.WeightsHash);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void ScenarioSupport_TheModelsDouble_TakesTheGeneratedCardForADocument_OnlyWithAFallback()
    {
        using var picture = DocumentImageFactory.Render(DocumentStepSelfHostedTests.Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);

        // The card as the geometry inspector hands it to the detector: cut out of the picture, never the picture as it was sent.
        var place = DocumentImageFactory.WholeCard;
        using var card = picture.Cropped(place.Left, place.Top, place.Width, place.Height);
        var plain = new ScriptedModels();
        var withFallback = new ScriptedModels { Fallback = new PortraitStandInDetector(), Similarity = 0.9 };

        Assert.Empty(plain.Detect(card, 0.6, CancellationToken.None));
        var face = Assert.Single(withFallback.Detect(card, 0.6, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => plain.Embed(card, face, CancellationToken.None));
        var portrait = withFallback.Embed(card, face, CancellationToken.None);

        // The portrait of a card is the one the similarity of the script says, against the first person of the script.
        Assert.Equal(0.9, portrait[0], precision: 5);
        Assert.Equal(1, withFallback.PortraitEmbeddings);
    }

    private IdentityPolicyProvider Provider(string path, string hash) =>
        new(
            Options.Create(new IdentityVerificationOptions { PolicyFile = path, PolicySha256 = hash }),
            NullLogger<IdentityPolicyProvider>.Instance,
            _directory);
}
