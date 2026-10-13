using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// Applied by convention to every <see cref="DateTime"/> and <see cref="Nullable{DateTime}"/>
/// property of <see cref="AppDbContext"/> (FD-06). Writes (including query parameters compared
/// with a mapped column) are normalized with <see cref="UtcDateTime.Normalize"/>, so a value with
/// <see cref="DateTimeKind.Unspecified"/> no longer makes Npgsql reject the <c>timestamptz</c>
/// parameter; reads always come back as <see cref="DateTimeKind.Utc"/>.
/// </summary>
public sealed class UtcDateTimeValueConverter() : ValueConverter<DateTime, DateTime>(
    value => UtcDateTime.Normalize(value),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
