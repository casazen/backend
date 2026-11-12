using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02, T03 and E05/F05/US02/T01: what <c>AddMessageAssistant</c> registers, and that the application calls it once. The exact list is photographed here: a service that a later task
/// of the feature (the lessons, the auto-send gate) adds to <c>AddMessageAssistant</c> is added to this test.
/// </summary>
public class MessageAssistantRegistrationTests
{
    [Fact]
    public void AddMessageAssistant_RegistersTheServicesOfTheFeature_ScopedEachOnce_AsTheWholeListOfServices()
    {
        var services = new ServiceCollection();

        services.AddMessageAssistant();

        foreach (var (service, implementation) in new (Type, Type)[]
                 {
                     (typeof(IAssistantRulesService), typeof(AssistantRulesService)),
                     (typeof(IAssistantKnowledgeService), typeof(AssistantKnowledgeService)),
                     (typeof(IAssistantSituationService), typeof(AssistantSituationService)),
                     (typeof(IAssistantDraftService), typeof(AssistantDraftService)),
                     (typeof(IAutoSendGate), typeof(NeverAutoSendGate)),
                     (typeof(IThreadReplyObserver), typeof(AssistantEscalationCloser)),
                     (typeof(IEventHandler<DomainEvent>), typeof(AssistantMessageHandler)),
                     (typeof(AssistantDraftJob), typeof(AssistantDraftJob)),
                 })
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, descriptor.ImplementationType);

            // Scoped: they hold the database of the request (or of the job).
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }

        // Of the types of the application, these and no other (the options of the section Assistant are the framework's).
        Assert.Equal(
            [
                nameof(AssistantDraftJob), nameof(IAssistantDraftService), nameof(IAssistantKnowledgeService), nameof(IAssistantRulesService),
                nameof(IAssistantSituationService), nameof(IAutoSendGate), "IEventHandler`1", nameof(IThreadReplyObserver),
            ],
            services.Select(d => d.ServiceType).Where(t => t.Namespace?.StartsWith("Casazen.", StringComparison.Ordinal) == true).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheOptionsOfTheSectionAssistant_HaveTheDecidedDefaults_AndAreBoundToTheSection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{AssistantOptions.SectionName}:Escalation:UrgentHours"] = "2" })
            .Build());
        services.AddMessageAssistant();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AssistantOptions>>().Value;

        Assert.Equal((6, 2, 4, 24), (options.MaxMessageAgeHours, options.Escalation.UrgentHours, options.Escalation.HighHours, options.Escalation.NormalHours));
        Assert.Equal((14, 10, 90), (options.Trust.MinDays, options.Trust.MinDrafts, options.Trust.MinApprovedWithoutEditPercent));
        Assert.Equal(TimeSpan.FromHours(6), options.MaxMessageAge());
        Assert.Equal(TimeSpan.FromHours(168), new AssistantOptions { MaxMessageAgeHours = 100_000 }.MaxMessageAge());
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
        Assert.DoesNotContain("AssistantDraftService", shared, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
