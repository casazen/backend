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

    /// <summary>The values of the rules, in the order of <see cref="DocumentGeometryRules"/>.</summary>
    private sealed record RuleValues(
        int EdgeFloor,
        double EdgeRelative,
        double PercentileShare,
        int DominanceFactor,
        int BandRadius,
        double SideSupportMin,
        int MaxLineCandidates,
        int MinWorkingSide,
        int SmallestCardSideDivisor);

    /// <summary>Every version of the rules that shipped, with the values it had. A row is never edited: a new version is a new row.</summary>
    private static readonly IReadOnlyDictionary<string, RuleValues> Shipped = new Dictionary<string, RuleValues>
    {
        ["geometry-2026.11-1"] = new(EdgeFloor: 40, EdgeRelative: 0.2, PercentileShare: 0.99, DominanceFactor: 2, BandRadius: 2, SideSupportMin: 0.75, MaxLineCandidates: 8, MinWorkingSide: 32, SmallestCardSideDivisor: 8),
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
        DocumentGeometryRules.SmallestCardSideDivisor);

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
}
