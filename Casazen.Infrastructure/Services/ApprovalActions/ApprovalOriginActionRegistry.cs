using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services.ApprovalActions;

/// <summary>
/// The registry of the actions of the approval queue (E05/F04/US01/T02): every <see cref="IApprovalOriginAction"/> the container holds, found by
/// <see cref="IApprovalOriginAction.ItemType"/> (the names of the types are compared as they are written, in ordinal order). The actions are registered by the
/// extension class of the feature that owns the type (<c>AddApprovalQueueActions</c>, and the later features of the epic from theirs); a type with
/// <b>two</b> actions is a mistake of the registration, and the registry refuses to answer rather than choose one of them.
/// </summary>
/// <remarks>
/// <b>The actions are asked for when the first answer is needed, not when the registry is built</b>: the action of the campaigns needs the campaign service, which
/// needs the queue to create its items, which needs this registry, so building the actions with the registry would be a circle in the container. By the time an
/// item is approved, the scope holds every one of them.
/// </remarks>
public sealed class ApprovalOriginActionRegistry : IApprovalOriginActionRegistry
{
    private readonly Lazy<Dictionary<string, IApprovalOriginAction>> _byType;

    /// <param name="actions">Where the actions are read from, once, at the first <see cref="Find"/> (the container's <c>GetServices&lt;IApprovalOriginAction&gt;</c>).</param>
    public ApprovalOriginActionRegistry(Func<IEnumerable<IApprovalOriginAction>> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _byType = new Lazy<Dictionary<string, IApprovalOriginAction>>(() => Index(actions()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IApprovalOriginAction? Find(string itemType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
        return _byType.Value.GetValueOrDefault(itemType);
    }

    private static Dictionary<string, IApprovalOriginAction> Index(IEnumerable<IApprovalOriginAction> actions)
    {
        var byType = new Dictionary<string, IApprovalOriginAction>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.ItemType))
                throw new InvalidOperationException($"{action.GetType().Name} names no item type.");

            if (!byType.TryAdd(action.ItemType, action))
            {
                throw new InvalidOperationException(
                    $"The item type '{action.ItemType}' has two actions ({byType[action.ItemType].GetType().Name} and {action.GetType().Name}): a type has one.");
            }
        }

        return byType;
    }
}
