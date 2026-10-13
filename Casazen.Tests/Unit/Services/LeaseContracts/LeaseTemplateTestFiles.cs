using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services.LeaseContracts;

/// <summary>
/// Template files written in a temporary folder for the LT-03 tests. Their texts are placeholders for tests, not
/// contract clauses.
/// </summary>
internal sealed class LeaseTemplateTestFiles : IDisposable
{
    public const string ApprovedVersion = "test-v1";

    public LeaseTemplateTestFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), $"casazen-lease-templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Write(FiscalRegime regime, string versionId, string content)
    {
        var directory = Path.Combine(Root, regime.ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, versionId + LeaseContractTemplateCatalog.FileExtension), content, Encoding.UTF8);
    }

    public LeaseTemplateOptions Options(FiscalRegime regime, LeaseTemplateVariantOptions variant) => new()
    {
        TemplatesDirectory = Root,
        Variants = new Dictionary<string, LeaseTemplateVariantOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [regime.ToString()] = variant,
        },
    };

    public static LeaseTemplateVariantOptions Approved(string versionId = ApprovedVersion) => new()
    {
        VersionId = versionId,
        Approved = true,
        ApprovalReference = "test approval",
    };

    public static ILeaseContractTemplateCatalog Catalog(LeaseTemplateOptions options)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return new LeaseContractTemplateCatalog(
            Microsoft.Extensions.Options.Options.Create(options),
            environment.Object,
            NullLogger<LeaseContractTemplateCatalog>.Instance);
    }

    /// <summary>
    /// A template with every required section of <paramref name="regime"/> and its required placeholders.
    /// <paramref name="texts"/> replaces the text of a section (empty string = section without text);
    /// <paramref name="omit"/> leaves sections out.
    /// </summary>
    public static string CompleteTemplate(
        FiscalRegime regime,
        IReadOnlyDictionary<string, string>? texts = null,
        IEnumerable<string>? omit = null,
        string? title = "Titolo di prova")
    {
        var omitted = omit?.ToHashSet(StringComparer.Ordinal) ?? [];
        var sb = new StringBuilder();
        if (title is not null)
            sb.Append("# ").Append(title).Append("\n\n");

        foreach (var definition in LeaseContractTemplateStructure.RequiredSections(regime).Where(d => !omitted.Contains(d.Id)))
        {
            sb.Append("## ").Append(definition.Id).Append('\n');
            var text = texts is not null && texts.TryGetValue(definition.Id, out var custom)
                ? custom
                : $"Testo di prova {definition.Id}. " +
                  string.Join(" ", definition.RequiredPlaceholders.Select(group => $"{{{{{group[0]}}}}}"));
            sb.Append(text).Append("\n\n");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temporary folder: a leftover is harmless.
        }
    }
}
