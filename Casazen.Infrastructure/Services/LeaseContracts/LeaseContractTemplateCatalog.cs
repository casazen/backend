using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.LeaseContracts;

public enum LeaseContractTemplateStatus
{
    Missing,

    Incomplete,

    NotApproved,

    Approved,
}

public sealed record LeaseContractTemplateState(
    LeaseTemplateVariant Variant,
    string? VersionId,
    LeaseContractTemplateStatus Status,
    string? Title,
    IReadOnlyList<LeaseContractTemplateSection> Sections,
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<string> Issues,
    bool DeclaredApproved,
    IReadOnlyList<string> InvalidApprovalReasons)
{
    public bool IsApproved => Status == LeaseContractTemplateStatus.Approved;

    public IEnumerable<string> Problems =>
        MissingSections.Select(id => $"section '{id}' has no text")
            .Concat(Issues)
            .Concat(InvalidApprovalReasons);
}

public interface ILeaseContractTemplateCatalog
{
    LeaseContractTemplateState Get(LeaseTemplateVariant variant);

    LeaseContractTemplateState Get(FiscalRegime regime) => Get(LeaseTemplateVariants.Of(regime));
}

public sealed class LeaseContractTemplateCatalog(
    IOptions<LeaseTemplateOptions> options,
    IHostEnvironment environment,
    ILogger<LeaseContractTemplateCatalog> logger) : ILeaseContractTemplateCatalog
{
    public const string FileExtension = ".md";

    private readonly Lazy<IReadOnlyDictionary<LeaseTemplateVariant, LeaseContractTemplateState>> _states =
        new(() => LoadAll(options.Value, environment.ContentRootPath, logger));

    public LeaseContractTemplateState Get(LeaseTemplateVariant variant) => _states.Value[variant];

    public static string ResolveDirectory(LeaseTemplateOptions options, string contentRootPath)
    {
        var directory = string.IsNullOrWhiteSpace(options.TemplatesDirectory)
            ? LeaseTemplateOptions.DefaultTemplatesDirectory
            : options.TemplatesDirectory.Trim();
        return Path.IsPathRooted(directory) ? directory : Path.Combine(contentRootPath, directory);
    }

    public static LeaseContractTemplateState Load(FiscalRegime regime, LeaseTemplateOptions options, string contentRootPath) =>
        Load(LeaseTemplateVariants.Of(regime), options, contentRootPath);

    public static LeaseContractTemplateState Load(LeaseTemplateVariant templateVariant, LeaseTemplateOptions options, string contentRootPath)
    {
        var variant = options.Variants.TryGetValue(templateVariant.ToString(), out var configured)
            ? configured
            : new LeaseTemplateVariantOptions();
        var invalidApproval = LeaseTemplateApproval.GetInvalidApprovalReasons(variant);
        var versionId = variant.VersionId?.Trim();

        LeaseContractTemplateState Missing(string issue) => new(
            templateVariant, string.IsNullOrEmpty(versionId) ? null : versionId, LeaseContractTemplateStatus.Missing, null, [],
            LeaseContractTemplateStructure.RequiredSections(templateVariant).Select(s => s.Id).ToList(), [issue],
            variant.Approved, invalidApproval);

        if (string.IsNullOrEmpty(versionId))
            return Missing("no VersionId configured");
        if (!LeaseTemplateApproval.IsValidVersionId(versionId))
            return Missing("VersionId is not a valid file name");

        var relativePath = $"{templateVariant}/{versionId}{FileExtension}";
        var path = Path.Combine(ResolveDirectory(options, contentRootPath), templateVariant.ToString(), versionId + FileExtension);
        string content;
        try
        {
            if (!File.Exists(path))
                return Missing($"template file {relativePath} not found");
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Missing($"template file {relativePath} cannot be read ({ex.GetType().Name})");
        }

        var parsed = LeaseContractTemplateParser.Parse(content);
        var issues = new List<string>(parsed.Errors);
        if (parsed.Title is null)
            issues.Add("the title ('# ' line) is missing");

        var requiredSections = LeaseContractTemplateStructure.RequiredSections(templateVariant);
        var missingSections = new List<string>();
        foreach (var definition in requiredSections)
        {
            var section = parsed.Sections.FirstOrDefault(s => s.Id == definition.Id);
            if (section is null || section.Text.Length == 0)
            {
                missingSections.Add(definition.Id);
                continue;
            }

            foreach (var group in definition.RequiredPlaceholders.Where(g => !g.Any(section.Placeholders.Contains)))
                issues.Add($"section '{definition.Id}' must use {string.Join(" or ", group.Select(p => $"{{{{{p}}}}}"))}");
        }

        foreach (var extra in parsed.Sections.Where(s => s.Text.Length == 0 && requiredSections.All(d => d.Id != s.Id)))
            issues.Add($"section '{extra.Id}' has no text");

        var status = missingSections.Count > 0 || issues.Count > 0
            ? LeaseContractTemplateStatus.Incomplete
            : variant.Approved && invalidApproval.Count == 0
                ? LeaseContractTemplateStatus.Approved
                : LeaseContractTemplateStatus.NotApproved;

        return new LeaseContractTemplateState(
            templateVariant, versionId, status, parsed.Title, parsed.Sections, missingSections, issues, variant.Approved, invalidApproval);
    }

    private static IReadOnlyDictionary<LeaseTemplateVariant, LeaseContractTemplateState> LoadAll(
        LeaseTemplateOptions options, string contentRootPath, ILogger logger)
    {
        var states = Enum.GetValues<LeaseTemplateVariant>().ToDictionary(v => v, v => Load(v, options, contentRootPath));
        foreach (var state in states.Values)
        {
            if (state.DeclaredApproved && !state.IsApproved)
            {
                logger.LogWarning(
                    "Lease contract template {Variant} is declared approved but is {Status} (version {VersionId}): {Problems}. " +
                    "Final contracts are blocked; see docs/runbooks/lease-contract-templates.md",
                    state.Variant, state.Status, state.VersionId, string.Join("; ", state.Problems));
            }
            else
            {
                logger.LogInformation(
                    "Lease contract template {Variant}: {Status} (version {VersionId}). Missing sections: {MissingSections}",
                    state.Variant, state.Status, state.VersionId, string.Join(", ", state.MissingSections));
            }
        }

        return states;
    }
}
