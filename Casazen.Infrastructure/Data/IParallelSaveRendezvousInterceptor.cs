using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// Marker for the parallel-transition test harness: when an interceptor of this type is on the
/// <see cref="AppDbContext"/>, <see cref="PostgresAdvisoryLocks"/> skips taking locks.
/// Holding a calendar lock across the harness's save barrier would deadlock the second party
/// (take always locks after timed service requests).
/// </summary>
internal interface IParallelSaveRendezvousInterceptor : IInterceptor;
