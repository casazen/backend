using System.Text.RegularExpressions;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US01/T01 (NF-189, review of S006): the parameters of the edge detector that decide whether a card is whole or found at all are versioned
/// (<see cref="DocumentGeometryRules.Version"/>) and every report carries the version it was made with. This class holds each value next to its version: a value that is
/// changed under a version that already shipped fails it, so whoever retouches one must give the rules a new version and add its row to <see cref="Shipped"/>.
/// </summary>
public class DocumentGeometryRulesTests
{
    private static readonly DocumentFormat Cie = new DocumentFormatRegistry(
        Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, AppContext.BaseDirectory).Get("it-cie-3.0")!;

    private sealed record RuleValues(
        int EdgeFloor,
        double EdgeRelative,
        double PercentileShare,
        int DominanceFactor,
        int BandRadius,
        double SideSupportMin,
        int MaxLineCandidates,
        int MinWorkingSide,
        int SmallestCardSideDivisor,
        double MaxTiltDegrees = 0,
        double TiltSearchDegrees = 0,
        double TiltCoarseStepDegrees = 0,
        double TiltFineStepDegrees = 0,
        double TiltRoundingStepDegrees = 0,
        int TiltCandidates = 0,
        double TiltPeakFactor = 0,
        double TiltPeakSeparationDegrees = 0,
        double TiltPlateauShare = 0);

    private static readonly IReadOnlyDictionary<string, RuleValues> Shipped = new Dictionary<string, RuleValues>
    {
        ["geometry-2026.11-1"] = new(EdgeFloor: 40, EdgeRelative: 0.2, PercentileShare: 0.99, DominanceFactor: 2, BandRadius: 2, SideSupportMin: 0.75, MaxLineCandidates: 8, MinWorkingSide: 32, SmallestCardSideDivisor: 8),

        ["geometry-2026.11-2"] = new(
            EdgeFloor: 40, EdgeRelative: 0.2, PercentileShare: 0.99, DominanceFactor: 2, BandRadius: 2, SideSupportMin: 0.75, MaxLineCandidates: 8, MinWorkingSide: 32, SmallestCardSideDivisor: 8,
            MaxTiltDegrees: 10, TiltSearchDegrees: 20, TiltCoarseStepDegrees: 0.5, TiltFineStepDegrees: 0.05, TiltRoundingStepDegrees: 0.25, TiltCandidates: 3, TiltPeakFactor: 2, TiltPeakSeparationDegrees: 2, TiltPlateauShare: 0.95),
    };

    private static RuleValues Current() => new(
        DocumentGeometryRules.EdgeFloor,
        DocumentGeometryRules.EdgeRelative,
        DocumentGeometryRules.PercentileShare,
        DocumentGeometryRules.DominanceFactor,
        DocumentGeometryRules.BandRadius,
        DocumentGeometryRules.SideSupportMin,
        DocumentGeometryRules.MaxLineCandidates,
        DocumentGeometryRules.MinWorkingSide,
        DocumentGeometryRules.SmallestCardSideDivisor,
        DocumentGeometryRules.MaxTiltDegrees,
        DocumentGeometryRules.TiltSearchDegrees,
        DocumentGeometryRules.TiltCoarseStepDegrees,
        DocumentGeometryRules.TiltFineStepDegrees,
        DocumentGeometryRules.TiltRoundingStepDegrees,
        DocumentGeometryRules.TiltCandidates,
        DocumentGeometryRules.TiltPeakFactor,
        DocumentGeometryRules.TiltPeakSeparationDegrees,
        DocumentGeometryRules.TiltPlateauShare);

    [Fact]
    public void TheRules_AreTheValuesOfTheirVersion_SoNobodyCanRetouchOneWithoutANewVersion()
    {
        Assert.True(
            Shipped.TryGetValue(DocumentGeometryRules.Version, out var expected),
            $"The version {DocumentGeometryRules.Version} has no row in Shipped: add one with the values of the rules.");

        Assert.True(
            expected == Current(),
            $"A value of DocumentGeometryRules changed under the version {DocumentGeometryRules.Version}: give the rules a new version and add its row to Shipped (the old row stays).");
    }

    [Fact]
    public void TheVersion_HasTheShapeOfTheOtherRules_AndEveryShippedVersionHasOne()
    {
        Assert.Matches(new Regex(@"^geometry-\d{4}\.\d{2}-\d+$"), DocumentGeometryRules.Version);
        Assert.All(Shipped.Keys, version => Assert.Matches(new Regex(@"^geometry-\d{4}\.\d{2}-\d+$"), version));
        Assert.Contains(DocumentGeometryRules.Version, Shipped.Keys);
    }

    [Fact]
    public void TheReportOfACardThatWasFound_CarriesTheVersionOfTheRules()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var inspector = new DocumentGeometryInspector(new PortraitStandInDetector(), Options.Create(new BiometricModelOptions()));

        var report = inspector.Inspect(picture, Cie, DocumentSide.Front, CancellationToken.None);

        Assert.True(report.CardFound);
        Assert.Equal(DocumentGeometryRules.Version, report.RulesVersion);
    }

    [Fact]
    public void TheReportOfAPictureWithNoCard_CarriesTheVersionOfTheRules()
    {
        using var picture = DocumentImageFactory.Plain(120);
        var inspector = new DocumentGeometryInspector(new ScriptedFaceDetector(), Options.Create(new BiometricModelOptions()));

        var report = inspector.Inspect(picture, Cie, DocumentSide.Front, CancellationToken.None);

        Assert.False(report.CardFound);
        Assert.Equal(DocumentGeometryRules.Version, report.RulesVersion);
    }

    [Fact]
    public void TheReportOfACardHeldCrooked_CarriesTheVersionOfTheRules_AndTheLimitOfTheTurnItWasMadeWith()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, 7);
        var inspector = new DocumentGeometryInspector(new PortraitStandInDetector(), Options.Create(new BiometricModelOptions()));

        using var report = inspector.Inspect(crooked, Cie, DocumentSide.Front, CancellationToken.None);

        Assert.True(report.EdgesVisible);
        Assert.InRange(report.TiltDegrees, 6.5, 7.5);
        Assert.Equal(DocumentGeometryRules.Version, report.RulesVersion);
        Assert.True(Shipped[report.RulesVersion].MaxTiltDegrees >= Math.Abs(report.TiltDegrees));
        Assert.Equal(0, Shipped["geometry-2026.11-1"].MaxTiltDegrees);
    }

    [Fact]
    public void TheLimitsOfTheTilt_AreCoherent_AndTheSearchStaysWithinWhatTheEdgesOfTheDetectorCanTellApart()
    {
        Assert.True(DocumentGeometryRules.MaxTiltDegrees > 0);
        Assert.True(DocumentGeometryRules.TiltSearchDegrees > DocumentGeometryRules.MaxTiltDegrees);
        Assert.True(DocumentGeometryRules.TiltCoarseStepDegrees > DocumentGeometryRules.TiltFineStepDegrees);
        Assert.True(DocumentGeometryRules.TiltRoundingStepDegrees >= DocumentGeometryRules.TiltFineStepDegrees);
        Assert.True(DocumentGeometryRules.TiltPeakSeparationDegrees >= DocumentGeometryRules.TiltCoarseStepDegrees);
        Assert.InRange(DocumentGeometryRules.TiltCandidates, 1, 8);
        Assert.True(DocumentGeometryRules.TiltPeakFactor > 1);
        Assert.InRange(DocumentGeometryRules.TiltPlateauShare, 0.5, 1);

        Assert.True(DocumentGeometryRules.TiltSearchDegrees < Math.Atan(1.0 / DocumentGeometryRules.DominanceFactor) * 180 / Math.PI);
    }
}
