using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02 and T03: what <c>AddMessageAssistant</c> registers, and that the application calls it once. The exact list is photographed here: a service that a later task of the
/// feature (the drafts, the lessons, the auto-send gate) adds to <c>AddMessageAssistant</c> is added to this test.
/// </summary>
public class MessageAssistantRegistrationTests
{
    [Fact]
    public void AddMessageAssistant_RegistersTheKnowledgeTheRulesAndTheSituations_ScopedEachOnce_AsTheWholeListOfServices()
    {
        var services = new ServiceCollection();

        services.AddMessageAssistant();

        Assert.Equal(typeof(AssistantRulesService), Assert.Single(services, d => d.ServiceType == typeof(IAssistantRulesService)).ImplementationType);
        Assert.Equal(typeof(AssistantKnowledgeService), Assert.Single(services, d => d.ServiceType == typeof(IAssistantKnowledgeService)).ImplementationType);
        Assert.Equal(typeof(AssistantSituationService), Assert.Single(services, d => d.ServiceType == typeof(IAssistantSituationService)).ImplementationType);

        // Scoped: they hold the database of the request.
        Assert.All(services, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));

        // Of the types of the application, these three and no other.
        Assert.Equal(
            [nameof(IAssistantKnowledgeService), nameof(IAssistantRulesService), nameof(IAssistantSituationService)],
            services.Select(d => d.ServiceType).Where(t => t.Namespace?.StartsWith("Casazen.", StringComparison.Ordinal) == true).Select(t => t.Name).Order());
    }

    [Fact]
    public void Program_CallsAddMessageAssistantOnce_AndTheExtensionIsNotAddedToTheSharedOne()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "Casazen.Web", "Program.cs"));
        var shared = File.ReadAllText(Path.Combine(root, "Casazen.Web", "Extensions", "ServiceCollectionExtensions.cs"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"builder\.Services\.AddMessageAssistant\(\);"));
        Assert.DoesNotContain("AssistantKnowledgeService", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("AssistantRulesService", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("AssistantSituationService", shared, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
