namespace Casazen.Core.Services;

/// <summary>
/// Whether this installation may run the demo environment. Production never may: the endpoints answer as if the
/// feature was not there.
/// </summary>
public interface IDemoEnvironmentGate
{
    bool IsAvailable { get; }
}
