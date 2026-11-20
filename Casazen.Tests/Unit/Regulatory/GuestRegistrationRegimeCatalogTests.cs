using System.Text.RegularExpressions;
using System.Xml.Linq;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Infrastructure.Data.Seeds;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// E29/F02/US01/T01 (NF-125, decisions D3, D5, D6, D12): the catalog of the regimes of the registration of the guests, Italy and the 26 cantons. It is
/// data copied from the research of 20 November 2026, so the tests are about its shape and its promises: every canton of the municipality data is there
/// with an obligation, a channel, a certainty, a date and a source; Ticino is the one regime verified as a duty to report; Appenzell Innerrhoden is a
/// faculty; Italy is the existing flows; no entry is more certain than its facts; no sentence is missing in a language or names a supplier.
/// </summary>
public class GuestRegistrationRegimeCatalogTests
{
    private static readonly DateOnly ResearchDay = new(2026, 11, 20);

    /// <summary>Names of software suppliers and of the systems they run for the cantons: none may reach a user (the authorities and their portals may).</summary>
    private static readonly Regex[] SupplierNames =
    [
        new(@"hoko", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"unisys", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"u-?stay", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\bavs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\belm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"feratel", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"chekin", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"check-?in", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"stellar", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"chargeautomation", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"lodgify", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"smoobu", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"arhon", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"tourx", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"softec", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"octorate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"concordato", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"airbnb", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    public static TheoryData<string> RegimeCodes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var regime in GuestRegistrationRegimeCatalog.All)
                data.Add(regime.Code);
            return data;
        }
    }

    /// <summary>
    /// Acceptance criterion 1 of the catalog: the 26 cantons of the municipality data are the 26 of the catalog, none missing and none more (a canton
    /// the data has and the catalog has not would be the unknown Swiss place for every property in it).
    /// </summary>
    [Fact]
    public void Catalog_EveryCantonOfTheMunicipalityData_IsInTheCatalog_Test()
    {
        var data = SwissMunicipalitySeed.Parse(SwissMunicipalitySeed.ReadEmbedded()!);
        var cantonsOfTheData = data.Municipalities.Select(m => m.CantonCode).Distinct().Order(StringComparer.Ordinal).ToList();
        var cantonsOfTheCatalog = GuestRegistrationRegimeCatalog.SwissCantons.Select(c => c.CantonCode!).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(26, cantonsOfTheData.Count);
        Assert.Equal(cantonsOfTheData, cantonsOfTheCatalog);
        Assert.Equal(SwissMunicipalitySeed.Cantons.Order(StringComparer.Ordinal).ToList(), cantonsOfTheCatalog);
    }

    [Theory]
    [MemberData(nameof(RegimeCodes))]
    public void Entry_HasAnObligationAChannelACertaintyADateAndASource_Test(string code)
    {
        var regime = GuestRegistrationRegimeCatalog.Find(code)!;

        Assert.True(Enum.IsDefined(regime.Obligation), code);
        Assert.True(Enum.IsDefined(regime.Channel), code);
        Assert.True(Enum.IsDefined(regime.DeadlineKind), code);
        Assert.True(Enum.IsDefined(regime.PrivateRentals), code);
        Assert.True(Enum.IsDefined(regime.Certainty), code);
        Assert.True(Enum.IsDefined(regime.CasaZenSupport), code);
        Assert.NotEmpty(regime.Sources);
        Assert.All(regime.Sources, source =>
        {
            Assert.StartsWith("https://", source.Url, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(source.Title), $"{code}: a source without a title");
        });
        Assert.Equal(regime.Sources.Count, regime.Sources.Select(s => s.Url).Distinct().Count());
        Assert.InRange(regime.VerifiedOn, new DateOnly(2026, 10, 1), ResearchDay);
        if (regime.PortalUrl is not null)
            Assert.StartsWith("https://", regime.PortalUrl, StringComparison.Ordinal);
        if (regime.RetentionYears is { } years)
            Assert.InRange(years, 1, 10);
        Assert.Equal(GuestRegistrationRegimeCatalog.SummaryKeyOf(code), regime.SummaryKey);
    }

    [Fact]
    public void Codes_AreUniqueAndFollowTheirCountryAndCanton()
    {
        var all = GuestRegistrationRegimeCatalog.All;

        // Italy, the 26 cantons by code, the unknown Swiss place: 28 entries.
        Assert.Equal(28, all.Count);
        Assert.Equal(all.Count, all.Select(r => r.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(GuestRegistrationRegimeCatalog.ItalyCode, all[0].Code);
        Assert.Equal(GuestRegistrationRegimeCatalog.UnknownSwissCode, all[^1].Code);
        Assert.Equal(GuestRegistrationRegimeCatalog.SwissCantons.Select(c => c.Code).Order(StringComparer.Ordinal), GuestRegistrationRegimeCatalog.SwissCantons.Select(c => c.Code));

        Assert.Equal(("IT", (string?)null, false), (all[0].CountryCode, all[0].CantonCode, all[0].IsSwiss));
        Assert.All(GuestRegistrationRegimeCatalog.SwissCantons, canton =>
        {
            Assert.Equal($"CH-{canton.CantonCode}", canton.Code);
            Assert.Equal("CH", canton.CountryCode);
            Assert.True(canton.IsSwiss);
        });
        Assert.Equal(("CH", (string?)null), (all[^1].CountryCode, all[^1].CantonCode));
    }

    /// <summary>Proof (data): Ticino is the one regime that raises a legal deadline by default, with the data the research verified.</summary>
    [Fact]
    public void Catalog_Ticino_IsReportToAuthorityVerified_Test()
    {
        var ticino = GuestRegistrationRegimeCatalog.ForCanton("TI");

        Assert.Equal(GuestRegistrationRegimeCatalog.TicinoCode, ticino.Code);
        Assert.Equal(GuestRegistrationObligation.ReportToAuthority, ticino.Obligation);
        Assert.Equal(GuestRegistrationChannel.PortalFileUpload, ticino.Channel);
        Assert.Equal(GuestRegistrationDeadlineKind.Hours24FromArrival, ticino.DeadlineKind);
        Assert.Equal(GuestRegistrationCertainty.Verified, ticino.Certainty);
        Assert.Equal("https://www.notifiche.ti.ch", ticino.PortalUrl);
        Assert.Equal(GuestRegistrationSupport.FileExport, ticino.CasaZenSupport);
        Assert.Equal(GuestRegistrationPrivateRentals.Yes, ticino.PrivateRentals);
        Assert.False(ticino.RetainDocumentImages);
    }

    [Fact]
    public void Catalog_AppenzellInnerrhoden_IsOptional_AndAFacultyIsNeverADuty()
    {
        var innerrhoden = GuestRegistrationRegimeCatalog.ForCanton("ai");

        Assert.Equal("CH-AI", innerrhoden.Code);
        Assert.Equal(GuestRegistrationObligation.Optional, innerrhoden.Obligation);
        Assert.Equal(GuestRegistrationChannel.None, innerrhoden.Channel);
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, GuestRegistrationDutyRules.DefaultDuty(innerrhoden));
        Assert.Equal(GuestRegistrationSupport.GuestRegister, innerrhoden.CasaZenSupport);
    }

    /// <summary>Italy is the existing Alloggiati flows, unchanged: the portal of the police, 24 hours, the documents kept as before.</summary>
    [Fact]
    public void Catalog_Italy_IsTheExistingAlloggiatiFlows_Unchanged()
    {
        var italy = GuestRegistrationRegimeCatalog.Italian;

        Assert.Equal(("IT", "IT", (string?)null), (italy.Code, italy.CountryCode, italy.CantonCode));
        Assert.Equal(GuestRegistrationObligation.ReportToAuthority, italy.Obligation);
        Assert.Equal(GuestRegistrationChannel.PortalFileUpload, italy.Channel);
        Assert.Equal(GuestRegistrationDeadlineKind.Hours24FromArrival, italy.DeadlineKind);
        Assert.Equal(GuestRegistrationCertainty.Verified, italy.Certainty);
        Assert.Equal("https://alloggiatiweb.poliziadistato.it", italy.PortalUrl);
        Assert.True(italy.RetainDocumentImages);
        Assert.Same(italy, GuestRegistrationRegimeCatalog.Find("it"));
    }

    /// <summary>
    /// «Uncertain entries say so»: a regime is verified only when the research gives none of its facts as partial, which for a regime means that it
    /// says outright whether it covers private rentals; the Swiss place nobody knows is the one entry that is not verified at all.
    /// </summary>
    [Fact]
    public void Catalog_NoEntryIsMoreCertainThanItsFacts_Test()
    {
        foreach (var regime in GuestRegistrationRegimeCatalog.All)
        {
            if (regime.Certainty == GuestRegistrationCertainty.Verified)
            {
                Assert.True(
                    regime.PrivateRentals is GuestRegistrationPrivateRentals.Yes or GuestRegistrationPrivateRentals.No,
                    $"{regime.Code} is verified but does not say whether it covers private rentals");
                Assert.NotEqual(GuestRegistrationObligation.Unknown, regime.Obligation);
            }

            if (regime.Obligation == GuestRegistrationObligation.Unknown)
                Assert.Equal(GuestRegistrationCertainty.Unverified, regime.Certainty);
        }

        Assert.Equal(
            [GuestRegistrationRegimeCatalog.UnknownSwissCode],
            GuestRegistrationRegimeCatalog.All.Where(r => r.Certainty == GuestRegistrationCertainty.Unverified).Select(r => r.Code));

        // The verified ones are the ones the research read on the authority's own source: Italy, Ticino, Schaffhausen, Zug, Vaud, Appenzell Innerrhoden.
        Assert.Equal(
            ["CH-AI", "CH-SH", "CH-TI", "CH-VD", "CH-ZG", "IT"],
            GuestRegistrationRegimeCatalog.All.Where(r => r.Certainty == GuestRegistrationCertainty.Verified).Select(r => r.Code).Order(StringComparer.Ordinal));
    }

    /// <summary>What CasaZen does follows what the law asks: assisted notification only where the canton wants a transmission, the register where it wants a register.</summary>
    [Fact]
    public void Catalog_Support_FollowsTheObligation()
    {
        foreach (var canton in GuestRegistrationRegimeCatalog.SwissCantons)
        {
            var expected = canton.Code == GuestRegistrationRegimeCatalog.TicinoCode
                ? GuestRegistrationSupport.FileExport
                : canton.Obligation == GuestRegistrationObligation.ReportToAuthority
                    ? GuestRegistrationSupport.AssistedNotification
                    : GuestRegistrationSupport.GuestRegister;
            Assert.True(expected == canton.CasaZenSupport, $"{canton.Code}: {canton.Obligation} should be {expected}, is {canton.CasaZenSupport}");
        }

        Assert.Equal(GuestRegistrationSupport.InformationOnly, GuestRegistrationRegimeCatalog.UnknownSwissPlace.CasaZenSupport);
        Assert.Equal(GuestRegistrationObligation.Unknown, GuestRegistrationRegimeCatalog.UnknownSwissPlace.Obligation);

        // The cantons the research gives with a vendor interface, a portal or an e-mail file (SH, ZH, BS, SG, TG, FR, NE, GE) say which, as the research says it.
        Assert.Equal(
            (GuestRegistrationChannel.PortalForm, GuestRegistrationChannel.VendorInterface, GuestRegistrationChannel.PortalForm, GuestRegistrationChannel.EmailFile),
            (
                GuestRegistrationRegimeCatalog.ForCanton("SH").Channel,
                GuestRegistrationRegimeCatalog.ForCanton("ZH").Channel,
                GuestRegistrationRegimeCatalog.ForCanton("NE").Channel,
                GuestRegistrationRegimeCatalog.ForCanton("TG").Channel));
    }

    /// <summary>Decision D6: no image of an identity document is kept for a Swiss regime, whatever the canton asks of the host.</summary>
    [Fact]
    public void Catalog_EverySwissRegime_KeepsNoDocumentImage_Test()
    {
        Assert.All(GuestRegistrationRegimeCatalog.All.Where(r => r.IsSwiss), regime => Assert.False(regime.RetainDocumentImages, regime.Code));
    }

    [Fact]
    public void ForCanton_AnyCase_AndAnUnknownCantonIsTheUnknownSwissPlace()
    {
        Assert.Same(GuestRegistrationRegimeCatalog.ForCanton("ZH"), GuestRegistrationRegimeCatalog.ForCanton(" zh "));
        Assert.Equal("CH-ZH", GuestRegistrationRegimeCatalog.ForCanton("ZH").Code);
        Assert.Same(GuestRegistrationRegimeCatalog.UnknownSwissPlace, GuestRegistrationRegimeCatalog.ForCanton(null));
        Assert.Same(GuestRegistrationRegimeCatalog.UnknownSwissPlace, GuestRegistrationRegimeCatalog.ForCanton(""));
        Assert.Same(GuestRegistrationRegimeCatalog.UnknownSwissPlace, GuestRegistrationRegimeCatalog.ForCanton("XX"));
        Assert.Null(GuestRegistrationRegimeCatalog.Find("CH-XX"));
        Assert.Null(GuestRegistrationRegimeCatalog.Find(null));
    }

    /// <summary>Every sentence a regime points at exists in Italian and in English, and the two differ (a key that falls back to its own name, or to the other language, is a hole).</summary>
    [Fact]
    public void EverySentence_ExistsInItalianAndEnglish_AndTheTwoDiffer()
    {
        var italian = Resx("SharedResources.resx");
        var english = Resx("SharedResources.en.resx");
        var keys = GuestRegistrationRegimeCatalog.All
            .SelectMany(r => new[] { r.SummaryKey, r.DeadlineNoteKey })
            .OfType<string>()
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"{key} is missing in Italian");
            Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"{key} is missing in English");
            Assert.NotEqual(it, en);
        }

        // The notes of the deadline that a regime has of its own are the ones its code names, and no other note stands unused.
        var own = GuestRegistrationRegimeCatalog.All.Where(r => r.DeadlineNoteKey is not null).Select(r => r.DeadlineNoteKey!).ToHashSet();
        var shared = new[] { GuestRegistrationRegimeCatalog.DeadlineNotVerifiedKey, GuestRegistrationRegimeCatalog.DeadlineNoSubmissionKey };
        foreach (var key in italian.Keys.Where(k => k.StartsWith("GuestRegime_", StringComparison.Ordinal) && k.EndsWith("_Deadline", StringComparison.Ordinal)))
            Assert.Contains(key, own);
        Assert.All(shared, key => Assert.Contains(key, own));
        foreach (var regime in GuestRegistrationRegimeCatalog.All.Where(r => r.DeadlineNoteKey is not null && !shared.Contains(r.DeadlineNoteKey)))
            Assert.Equal(GuestRegistrationRegimeCatalog.DeadlineKeyOf(regime.Code), regime.DeadlineNoteKey);
    }

    /// <summary>
    /// «Uncertain says so» (D5), as the review of the slice asked: what the research leaves open is written as open. The register of Vaud is for the authority (the page names no
    /// police) and the recipient of the one of Aargau is not verified, so each has a note of its own; St. Gallen does not say whether it covers private rentals; the years the
    /// research gives for Thurgau and Zurich (and the maximum of Uri) are how long a police system keeps the data, not the host's retention; Obwalden and Jura claim no more than
    /// the sources do.
    /// </summary>
    [Fact]
    public void Catalog_WhatTheResearchLeavesOpen_IsWrittenAsOpen_Test()
    {
        Assert.Equal(GuestRegistrationRegimeCatalog.DeadlineKeyOf("CH-VD"), GuestRegistrationRegimeCatalog.ForCanton("VD").DeadlineNoteKey);
        Assert.Equal(GuestRegistrationRegimeCatalog.DeadlineKeyOf("CH-AG"), GuestRegistrationRegimeCatalog.ForCanton("AG").DeadlineNoteKey);
        Assert.Equal(GuestRegistrationPrivateRentals.Unknown, GuestRegistrationRegimeCatalog.ForCanton("SG").PrivateRentals);
        Assert.All(new[] { "TG", "ZH", "UR" }, canton => Assert.Null(GuestRegistrationRegimeCatalog.ForCanton(canton).RetentionYears));

        var italian = Resx("SharedResources.resx");
        var english = Resx("SharedResources.en.resx");
        Assert.DoesNotContain("polizia", italian["GuestRegime_CH_VD_Deadline"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("police", english["GuestRegime_CH_VD_Deadline"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non è verificato", italian["GuestRegime_CH_AG_Deadline"], StringComparison.Ordinal);
        Assert.Contains("not verified", english["GuestRegime_CH_AG_Deadline"], StringComparison.Ordinal);
        Assert.DoesNotContain("non ancora in vigore", italian["GuestRegime_CH_OW_Summary"], StringComparison.Ordinal);
        Assert.DoesNotContain("not yet in force", english["GuestRegime_CH_OW_Summary"], StringComparison.Ordinal);
        Assert.Contains("già in vigore", italian["GuestRegime_CH_OW_Summary"], StringComparison.Ordinal);
        Assert.Contains("already in force", english["GuestRegime_CH_OW_Summary"], StringComparison.Ordinal);
        Assert.Contains("a chi arrivi", italian["GuestRegime_CH_JU_Summary"], StringComparison.Ordinal);
        Assert.Contains("who receives it", english["GuestRegime_CH_JU_Summary"], StringComparison.Ordinal);
    }

    /// <summary>The authorities and their portals may be named; the suppliers of software and the systems they run for the cantons may not (no supplier name in a user-facing text).</summary>
    [Fact]
    public void NoTextOfTheUser_NamesASupplier_Test()
    {
        var texts = new List<(string Where, string Text)>();
        foreach (var file in new[] { "SharedResources.resx", "SharedResources.en.resx" })
        {
            foreach (var (key, value) in Resx(file).Where(entry => entry.Key.StartsWith("GuestRegime", StringComparison.Ordinal)))
                texts.Add(($"{file}:{key}", value));
        }

        foreach (var regime in GuestRegistrationRegimeCatalog.All)
        {
            texts.AddRange(regime.Sources.Select(source => ($"{regime.Code} source", source.Title)));
            texts.AddRange(regime.LegalReferences.Select(reference => ($"{regime.Code} reference", reference)));
        }

        Assert.True(texts.Count > 100);
        foreach (var (where, text) in texts)
        {
            foreach (var name in SupplierNames)
                Assert.False(name.IsMatch(text), $"{where} names a supplier ({name}): {text}");
        }
    }

    /// <summary>The compliance page lists every regime of the catalog (the table it holds is the one the code has).</summary>
    [Fact]
    public void TheCompliancePage_NamesEveryRegime()
    {
        var page = File.ReadAllText(Path.Combine(SolutionRoot(), "docs", "compliance", "guest-registration-switzerland.md"));

        foreach (var regime in GuestRegistrationRegimeCatalog.All)
            Assert.Contains($"`{regime.Code}`", page, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Resx(string fileName)
    {
        var path = Path.Combine(SolutionRoot(), "Casazen.Web", "Resources", fileName);
        return XDocument.Load(path).Root!
            .Elements("data")
            .GroupBy(data => (string)data.Attribute("name")!)
            .ToDictionary(group => group.Key, group => (string?)group.Last().Element("value") ?? string.Empty);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
