using Casazen.Core.Entities;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

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

        // The premise of the calibrated copy: the committed policy is provisional, and a provisional policy is never qualified.
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
    public void ScenarioSupport_TheCardAndTheHolderOfTheScenario_PassEveryCheckTheFormatRequires()
    {
        var (_, _, policy) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);

        var report = SelfHostedIdentityScenario.CheckTheCard(policy);

        Assert.True(
            report.AllPassed(DocumentStepSelfHostedTests.Cie.RequiredChecks),
            $"Not passed: {SelfHostedIdentityScenario.NotPassed(report.Checks)}");
        Assert.Equal(IdentityDocumentCoherence.Passed, IdentityDocumentCoherences.Of(report, DocumentStepSelfHostedTests.Cie));
    }

    [Fact]
    public async Task ScenarioSupport_TheRealDocumentStep_OnTheCardAndTheHolderOfTheScenario_PassesEveryCheckTheFormatRequires()
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        var session = await world.ConsentedStayAsync(SelfHostedIdentityScenario.DescribeTheHolder);
        var back = SelfHostedIdentityScenario.BackPicture();
        world.DocumentMrz.Add(back, DocumentStepSelfHostedTests.CieMrz());
        world.DocumentZones.Fields[DocumentSide.Front] = SelfHostedIdentityScenario.PerfectFrontFields();

        await world.DocumentFlow().SubmitDocumentAsync(session, new MemoryStream(SelfHostedIdentityScenario.FrontPicture()), DocumentSide.Front, CancellationToken.None);
        var second = await world.DocumentFlow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, CancellationToken.None);

        Assert.True(second.Acceptable);
        SelfHostedIdentityScenario.AssertTheCardPassedEveryCheck(await world.ReadBackAsync(session.Id));
    }

    [Fact]
    public void ScenarioSupport_ACardWithoutItsIssueDate_IsNeverAPass()
    {
        var (_, _, policy) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);
        var format = DocumentStepSelfHostedTests.Cie;

        var report = SelfHostedIdentityScenario.CheckTheCard(policy, front: DocumentStepSelfHostedTests.FrontFields());

        Assert.False(report.AllPassed(format.RequiredChecks));
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.FieldCoherence));
        Assert.Equal(IdentityDocumentCoherence.Unknown, IdentityDocumentCoherences.Of(report, format));
    }

    [Fact]
    public void ScenarioSupport_AHolderWithNoDocumentNumberAndNoCitizenshipDeclared_PassesByNotApplicable_OnlyWhenEveryOtherCheckPasses()
    {
        var (_, _, policy) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);
        var format = DocumentStepSelfHostedTests.Cie;
        Action<Guest> declaresNeither = guest =>
        {
            guest.DocumentNumber = string.Empty;
            guest.Nationality = string.Empty;
        };

        var perfectCard = SelfHostedIdentityScenario.CheckTheCard(policy, edit: declaresNeither);
        var noIssueDate = SelfHostedIdentityScenario.CheckTheCard(policy, front: DocumentStepSelfHostedTests.FrontFields(), edit: declaresNeither);

        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.NotApplicable, ZonedFieldNames.DocumentNumber), perfectCard.Checks);
        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.NotApplicable, ZonedFieldNames.Nationality), perfectCard.Checks);
        Assert.True(perfectCard.AllPassed(format.RequiredChecks), $"Not passed: {SelfHostedIdentityScenario.NotPassed(perfectCard.Checks)}");
        Assert.Equal(IdentityDocumentCoherence.Passed, IdentityDocumentCoherences.Of(perfectCard, format));
        Assert.False(noIssueDate.AllPassed(format.RequiredChecks));
        Assert.Equal(IdentityDocumentCoherence.Unknown, IdentityDocumentCoherences.Of(noIssueDate, format));
    }

    [Fact]
    public void ScenarioSupport_AHolderWhoDeclaredNothing_IsNotComparedWithTheBooking_AndThePassSaysSo()
    {
        var (_, _, policy) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);
        var format = DocumentStepSelfHostedTests.Cie;
        Action<Guest> declaresNothing = guest =>
        {
            guest.FirstName = string.Empty;
            guest.LastName = string.Empty;
            guest.DateOfBirth = null;
            guest.DocumentNumber = string.Empty;
            guest.Nationality = string.Empty;
        };

        var perfectCard = SelfHostedIdentityScenario.CheckTheCard(policy, edit: declaresNothing);
        var noIssueDate = SelfHostedIdentityScenario.CheckTheCard(policy, front: DocumentStepSelfHostedTests.FrontFields(), edit: declaresNothing);

        Assert.Equal(DocumentCoherenceResults.NotApplicable, perfectCard.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.True(perfectCard.AllPassed(format.RequiredChecks), $"Not passed: {SelfHostedIdentityScenario.NotPassed(perfectCard.Checks)}");
        Assert.Equal(IdentityDocumentCoherence.PassedWithoutBooking, IdentityDocumentCoherences.Of(perfectCard, format));
        Assert.False(noIssueDate.AllPassed(format.RequiredChecks));
        Assert.Equal(IdentityDocumentCoherence.Unknown, IdentityDocumentCoherences.Of(noIssueDate, format));
    }

    [Fact]
    public void ScenarioSupport_ADeclaredValueThatDiffers_IsABookingConflict_NeverAPass_WhateverTheFormatRequires()
    {
        var (_, _, policy) = SelfHostedIdentityScenario.WriteCalibratedPolicy(_directory, Version);
        var format = DocumentStepSelfHostedTests.Cie;

        var otherNumber = SelfHostedIdentityScenario.CheckTheCard(policy, edit: guest => guest.DocumentNumber = "ZZ0000000");
        var otherSurname = SelfHostedIdentityScenario.CheckTheCard(policy, edit: guest => guest.LastName = "ROSSI");

        foreach (var report in new[] { otherNumber, otherSurname })
        {
            Assert.Equal(DocumentCoherenceResults.Failed, report.Summary(DocumentFormatChecks.BookingConsistency));
            Assert.False(report.AllPassed(format.RequiredChecks));
            Assert.Equal(IdentityDocumentCoherence.BookingConflict, IdentityDocumentCoherences.Of(report, format));
            Assert.Equal(IdentityDocumentCoherence.BookingConflict, IdentityDocumentCoherences.Of(report, null));
        }
    }

    [Fact]
    public void ScenarioSupport_TheModelsDouble_TakesTheGeneratedCardForADocument_OnlyWithAFallback()
    {
        using var picture = DocumentImageFactory.Render(DocumentStepSelfHostedTests.Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);

        // The geometry inspector hands the detector the card cut out of the picture, never the picture as it was sent.
        var place = DocumentImageFactory.WholeCard;
        using var card = picture.Cropped(place.Left, place.Top, place.Width, place.Height);
        var plain = new ScriptedModels();
        var withFallback = new ScriptedModels { Fallback = new PortraitStandInDetector(), Similarity = 0.9 };

        Assert.Empty(plain.Detect(card, 0.6, CancellationToken.None));
        var face = Assert.Single(withFallback.Detect(card, 0.6, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => plain.Embed(card, face, CancellationToken.None));
        var portrait = withFallback.Embed(card, face, CancellationToken.None);

        // Axis 0 is the cosine with the first person of the script.
        Assert.Equal(0.9, portrait[0], precision: 5);
        Assert.Equal(1, withFallback.PortraitEmbeddings);
    }

    private IdentityPolicyProvider Provider(string path, string hash) =>
        new(
            Options.Create(new IdentityVerificationOptions { PolicyFile = path, PolicySha256 = hash }),
            NullLogger<IdentityPolicyProvider>.Instance,
            _directory);
}
