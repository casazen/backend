using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// One process-wide view of the AI provider. The admin row is read on demand and cached; a save replaces the cache,
/// so the next generation uses it. The API key stays in the entity (encrypted at rest) and in this cache, never in a log.
/// </summary>
public sealed class AiRuntimeSettings(IOptions<AiOptions> environment, IServiceScopeFactory scopes) : IAiRuntimeSettings
{
    private readonly object _gate = new();
    private AiOptions _effective = AiOptionsMerger.Clone(environment.Value);

    public AiOptions Peek()
    {
        lock (_gate)
            return AiOptionsMerger.Clone(_effective);
    }

    public async Task<AiOptions> GetEffectiveAsync(CancellationToken cancellationToken = default)
    {
        var row = await LoadRowAsync(tracking: false, cancellationToken);
        var effective = AiOptionsMerger.Merge(environment.Value, row);
        lock (_gate)
            _effective = effective;
        return AiOptionsMerger.Clone(effective);
    }

    public async Task<AiAdminSettingsDto> GetAdminViewAsync(CancellationToken cancellationToken = default)
    {
        var row = await LoadRowAsync(tracking: false, cancellationToken);
        var view = AiOptionsMerger.ToAdmin(environment.Value, row);
        lock (_gate)
            _effective = AiOptionsMerger.Merge(environment.Value, row);
        return view;
    }

    public async Task<AiAdminSettingsDto> UpdateAsync(UpdateAiSettingsCommand command, CancellationToken cancellationToken = default)
    {
        var provider = AiOptionsMerger.NormalizeProvider(command.Provider);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PlatformAiSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new PlatformAiSettings();
            db.PlatformAiSettings.Add(row);
        }

        row.Provider = provider;
        if (command.ClearApiKey)
            row.ApiKey = null;
        else if (!string.IsNullOrWhiteSpace(command.ApiKey))
            row.ApiKey = command.ApiKey.Trim();

        if (command.Model is not null)
            row.Model = string.IsNullOrWhiteSpace(command.Model) ? null : command.Model.Trim();
        row.BaseUrl = AiOptionsMerger.NormalizeBaseUrl(command.BaseUrl);

        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = command.ActorUserId;
        await db.SaveChangesAsync(cancellationToken);

        var view = AiOptionsMerger.ToAdmin(environment.Value, row);
        lock (_gate)
            _effective = AiOptionsMerger.Merge(environment.Value, row);
        return view;
    }

    private async Task<PlatformAiSettings?> LoadRowAsync(bool tracking, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.PlatformAiSettings.AsQueryable();
        if (!tracking)
            query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(cancellationToken);
    }
}
