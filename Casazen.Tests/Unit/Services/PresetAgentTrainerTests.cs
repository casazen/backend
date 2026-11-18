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
