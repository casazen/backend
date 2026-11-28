using System.Reflection;
using System.Text.Json;
using Casazen.Core.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Biometrics;

public sealed class ModelManifestTests
{
    private const long GitHubFileLimitBytes = 100_000_000;

    private static readonly string[] Roles =
        ["face_detector", "anti_spoofing", "face_recognition", "mrz_ocr", "anti_spoofing_candidate", "face_recognition_candidate"];

    private static readonly string[] Deliveries = ["commit", "external"];

    [Fact]
    public void Manifest_EveryModelTheApplicationKnows_HasAnEntryThatAgreesWithItsOptions()
    {
        var entries = Entries();
        var biometrics = new BiometricModelOptions();
        var mrz = new MrzOcrOptions();

        (string Role, string Key, ModelFileOptions Spec)[] expected =
        [
            ("face_detector", "Biometrics:FaceDetector", biometrics.FaceDetector),
            ("anti_spoofing", "Biometrics:AntiSpoofing", biometrics.AntiSpoofing),
            ("face_recognition", "Biometrics:FaceRecognition", biometrics.FaceRecognition),
            ("mrz_ocr", "MrzOcr:Mrz", mrz.Mrz),
        ];

        foreach (var (role, key, spec) in expected)
        {
            var entry = Assert.Single(entries, e => Text(e, "usedBy") == key && !Bool(e, "alternative"));
            Assert.Equal(role, Text(entry, "role"));
            Assert.Equal(spec.Name, Text(entry, "name"));
            Assert.Equal(spec.Version, Text(entry, "version"));
            Assert.Equal(spec.License, entry.GetProperty("license").GetProperty("id").GetString());
            Assert.Equal(System.IO.Path.GetFileName(spec.Path), Text(entry, "file"));

            Assert.Equal(spec.Path, "MlModels/" + Text(entry, "targetPath"));
        }
    }

    [Fact]
    public void Manifest_SFace_IsTheBundledRecognitionModel()
    {
        var sface = Assert.Single(Entries(), e => Text(e, "id") == "sface");

        Assert.False(Bool(sface, "alternative"));
        Assert.Equal("face_recognition", Text(sface, "role"));
        Assert.Equal("Apache-2.0", sface.GetProperty("license").GetProperty("id").GetString());

        var set = sface.GetProperty("config").GetProperty("set");
        Assert.Equal("SFace", set.GetProperty("Biometrics__FaceRecognition__Name").GetString());
        Assert.Equal("0", set.GetProperty("Biometrics__RecognitionMean").GetString());
        Assert.Equal("1", set.GetProperty("Biometrics__RecognitionStd").GetString());
        Assert.Equal(
            Text(sface, "targetPath"),
            set.GetProperty("Biometrics__FaceRecognition__Path").GetString()!["MlModels/".Length..]);
    }

    [Fact]
    public void Manifest_EveryEntry_HasTheShapeTheScriptReads()
    {
        var entries = Entries();
        Assert.Equal(entries.Count, entries.Select(e => Text(e, "id")).Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in entries)
        {
            var id = Text(entry, "id");
            Assert.Contains(Text(entry, "role"), Roles);
            Assert.Contains(Text(entry, "delivery"), Deliveries);
            Assert.False(string.IsNullOrWhiteSpace(Text(entry, "name")), id);
            Assert.False(string.IsNullOrWhiteSpace(Text(entry, "version")), id);

            var target = Text(entry, "targetPath");
            Assert.False(System.IO.Path.IsPathRooted(target), $"{id}: targetPath must be relative");
            Assert.DoesNotContain("..", target);
            Assert.DoesNotContain("\\", target);
            Assert.EndsWith(Text(entry, "file"), target, StringComparison.Ordinal);

            var source = entry.GetProperty("source");
            AssertHttps(source.GetProperty("repository").GetString(), $"{id}: source.repository");
            var template = source.GetProperty("urlTemplate");
            if (template.ValueKind != JsonValueKind.Null)
            {
                AssertHttps(template.GetString(), $"{id}: source.urlTemplate");
                Assert.Contains("{revision}", template.GetString());
                Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("defaultRevision").GetString()), $"{id}: a null revision needs a default");
            }

            var revision = source.GetProperty("revision");
            Assert.True(revision.ValueKind is JsonValueKind.Null || !string.IsNullOrWhiteSpace(revision.GetString()), $"{id}: revision is null or a value");

            var license = entry.GetProperty("license");
            Assert.False(string.IsNullOrWhiteSpace(license.GetProperty("id").GetString()), id);
            AssertHttps(license.GetProperty("url").GetString(), $"{id}: license.url");

            Assert.True(entry.GetProperty("approxSizeBytes").GetInt64() > 0, id);

            var sha256 = entry.GetProperty("sha256");
            Assert.True(
                sha256.ValueKind is JsonValueKind.Null || (sha256.GetString()!.Length == 64 && sha256.GetString()!.All(Uri.IsHexDigit)),
                $"{id}: sha256 is null or 64 hexadecimal characters");
        }
    }

    [Fact]
    public void Manifest_AModelOverTheGitHubLimit_IsExternal_AndASmallOneIsCommitted()
    {
        foreach (var entry in Entries())
        {
            var size = entry.GetProperty("approxSizeBytes").GetInt64();
            var expected = size >= GitHubFileLimitBytes ? "external" : "commit";

            Assert.True(Text(entry, "delivery") == expected, $"{Text(entry, "id")}: {size} bytes is delivered as '{expected}' (a GitHub file is at most 100 MB)");
        }
    }

    [Fact]
    public void Manifest_TheCandidateRoles_HaveNoEntryWhileTheyAreNotConfigured_AndNothingCommittedConfiguresOne()
    {
        var defaults = new BiometricModelOptions();
        Assert.False(BiometricModelOptions.IsCandidateConfigured(defaults.AntiSpoofingCandidate));
        Assert.False(BiometricModelOptions.IsCandidateConfigured(defaults.FaceRecognitionCandidate));
        Assert.Empty(defaults.ValidateAntiSpoofingCandidate());
        Assert.Empty(defaults.ValidateFaceRecognitionCandidate());

        var published = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build()
            .GetSection(BiometricModelOptions.SectionName)
            .Get<BiometricModelOptions>()!;
        Assert.False(BiometricModelOptions.IsCandidateConfigured(published.AntiSpoofingCandidate));
        Assert.False(BiometricModelOptions.IsCandidateConfigured(published.FaceRecognitionCandidate));
        Assert.Empty(published.Validate());

        Assert.DoesNotContain(Entries(), entry => Text(entry, "role").EndsWith("_candidate", StringComparison.Ordinal) || Text(entry, "usedBy").Contains("Candidate", StringComparison.Ordinal));
        var models = System.IO.Path.Combine(SolutionRoot(), "Casazen.Web", "MlModels");
        Assert.DoesNotContain(
            Directory.EnumerateFiles(models, "*", SearchOption.AllDirectories).Select(System.IO.Path.GetFileName),
            file => file is not null && file.Contains("candidate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Manifest_TheRecipeAndTheScriptItNames_ExistInTheRepository()
    {
        var root = SolutionRoot();
        Assert.True(File.Exists(System.IO.Path.Combine(root, "scripts", "biometrics", "fetch-models.mjs")));

        var recipes = Entries().Where(e => e.TryGetProperty("export", out _)).ToList();
        Assert.NotEmpty(recipes);
        foreach (var entry in recipes)
        {
            var script = entry.GetProperty("export").GetProperty("script").GetString()!;
            Assert.True(File.Exists(System.IO.Path.Combine(root, script)), $"{Text(entry, "id")}: {script} does not exist");

            Assert.Equal(JsonValueKind.Null, entry.GetProperty("source").GetProperty("urlTemplate").ValueKind);
        }
    }

    private static void AssertOption(string variable, string leaf, string id)
    {
        var property = ResolveOption(variable);
        Assert.True(property is not null, $"{id}: {variable} is not a setting of the options");
        Assert.Equal(leaf, property!.Name);
        Assert.Equal(typeof(ModelFileOptions), property.DeclaringType);
    }

    private static PropertyInfo? ResolveOption(string variable)
    {
        var segments = variable.Split("__");
        var type = segments[0] switch
        {
            "Biometrics" => typeof(BiometricModelOptions),
            "MrzOcr" => typeof(MrzOcrOptions),
            _ => null,
        };

        PropertyInfo? property = null;
        foreach (var segment in segments.Skip(1))
        {
            if (type is null)
                return null;

            property = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
                return null;

            type = property.PropertyType;
        }

        return property;
    }

    private static void AssertHttps(string? value, string what) =>
        Assert.True(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, $"{what} must be an absolute https address");

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static bool Bool(JsonElement element, string name) => element.GetProperty(name).GetBoolean();

    private static string ManifestPath() => System.IO.Path.Combine(SolutionRoot(), "Casazen.Web", "MlModels", "models.manifest.json");

    private static List<JsonElement> Entries()
    {
        var path = ManifestPath();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        return document.RootElement.GetProperty("models").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
