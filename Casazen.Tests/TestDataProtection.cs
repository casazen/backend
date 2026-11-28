using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Tests;

/// <summary>
/// The one in-memory key ring of the test process. EF caches the model of <c>AppDbContext</c> per Data Protection provider
/// (<c>DataProtectionModelCacheKeyFactory</c>: context type + provider instance), and building it is the slow part of a test
/// (about 450 tables, seconds): a test that creates its own provider makes EF build the whole model again, a test that takes
/// <see cref="Shared"/> finds it already built. Use it whenever the test does not need the key ring to be its own; create a
/// separate <c>new EphemeralDataProtectionProvider()</c> only where the test proves that another key ring cannot read what
/// this one protected (isolation, lost or rotated keys).
/// </summary>
public static class TestDataProtection
{
    public static EphemeralDataProtectionProvider Shared { get; } = new();
}
