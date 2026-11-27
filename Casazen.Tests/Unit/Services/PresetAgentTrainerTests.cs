using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PresetAgentTrainerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromptGeneratesDistinctCasesAndEvaluatesWithoutPublishing(bool valid)
    {
        await using var db = new RoutineRig().NewDb();
        db.PresetAgentTraining.Add(new PresetAgentTraining { Key = "cleaning" });
        await db.SaveChangesAsync();
        const string prompt = "Organizza la pulizia per soggiorni da quattro notti.";
        var examples = Enumerable.Range(1, 6)
            .Select(nights => new AgentExample("Direct", "CheckedOut", nights, 2, false, nights >= 4)).ToArray();
        var evaluation = examples.Select(e => e with { Guests = 3 }).ToArray();
        var generated = valid
            ? JsonSerializer.Serialize(new { training = examples, evaluation }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : """{"training":[{"channel":"Direct","status":"Confirmed","nights":4,"guests":2,"noShow":false}],"evaluation":[]}""";
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest _, AiModelTier tier, string key, CancellationToken _) =>
                new AiGenerationResult(key == "preset-agent-scenarios" ? generated : "[0]", 12, 2, tier, false));
        await using var services = new ServiceCollection().AddScoped(_ => ai.Object).BuildServiceProvider();
        var trainer = new PresetAgentTrainer(db, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PresetAgentTrainer>.Instance);
        if (!valid)
        {
            await Assert.ThrowsAsync<DomainRuleException>(() => trainer.TrainFromPromptAsync("cleaning", prompt, 0, CancellationToken.None));
            Assert.Equal(0, (await trainer.GetAsync("cleaning", CancellationToken.None)).Revision);
            Assert.Single(ai.Invocations);
            return;
        }
        var state = await trainer.TrainFromPromptAsync("cleaning", prompt, 0, CancellationToken.None);
        Assert.Null(state.Active);
        Assert.Equal(prompt, state.Candidate!.Guidance);
        Assert.Equal(examples, state.Draft!.Training);
        Assert.Equal(evaluation, state.Results!.Select(row => row.Example));
        Assert.Equal(13, ai.Invocations.Count);
    }

    [Fact]
    public async Task PresetIsEvaluatedBeforeItGoesLiveAndCanBeRestored()
    {
        var db = new RoutineRig().NewDb();
        await using var database = db;
        db.PresetAgentTraining.Add(new PresetAgentTraining { Key = "cleaning" });
        await db.SaveChangesAsync();
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest request, AiModelTier tier, string _, CancellationToken _) =>
                new AiGenerationResult(request.Prompt.Contains("\"Nights\":5", StringComparison.Ordinal) ? "[]" : "[0]", 12, 2, tier, false));
        await using var services = new ServiceCollection().AddScoped(_ => ai.Object).BuildServiceProvider();
        var trainer = new PresetAgentTrainer(db, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PresetAgentTrainer>.Instance);
        var reference = new AgentExample("Direct", "CheckedOut", 1, 2, false, true);
        var draft = new AgentTrainingDraft("Always check the stay facts.",
            [reference, reference with { Nights = 2, Execute = false }],
            [reference with { Nights = 4 }, reference with { Nights = 5, Execute = false }]);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            trainer.TrainAsync("cleaning", draft with { Evaluation = draft.Training }, 0, CancellationToken.None));
        var trained = await trainer.TrainAsync("cleaning", draft, 0, CancellationToken.None);
        Assert.Null(trained.Active);
        Assert.Equal(1, trained.Candidate!.Version);
        Assert.All(trained.Results!, row => Assert.Equal(row.Example.Execute, row.Candidate.Execute));
        Assert.All(trained.Results!, row => Assert.Equal(14, row.Candidate.Tokens));
        Assert.Contains(ai.Invocations, call => ((AiRequest)call.Arguments[0]).Prompt.Contains("Always check the stay facts.", StringComparison.Ordinal));
        await Assert.ThrowsAsync<DomainConflictException>(() => trainer.ActivateAsync("cleaning", 0, false, CancellationToken.None));
        var published = await trainer.ActivateAsync("cleaning", trained.Revision, false, CancellationToken.None);
        Assert.Equal(1, (await PresetAgentTrainer.ActiveAsync(db, PresetAgentCatalog.All[0], CancellationToken.None))!.Version);
        var restored = await trainer.ActivateAsync("cleaning", published.Revision, true, CancellationToken.None);
        Assert.Null(restored.Active);
    }
}
