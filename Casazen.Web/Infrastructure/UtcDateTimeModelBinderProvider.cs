using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Binds <see cref="DateTime"/> and <see cref="Nullable{DateTime}"/> from query string, route
/// and form values as UTC (FD-06), with the same rule as <see cref="UtcDateTimeJsonConverter"/>:
/// a value without offset ("2026-09-01") is taken as UTC (midnight UTC for a date without time);
/// a value with "Z" or an offset is converted to the UTC instant. Result: always
/// <see cref="DateTimeKind.Utc"/>. Replaces MVC's default <see cref="DateTimeModelBinderProvider"/>,
/// which leaves offset-less values as <see cref="DateTimeKind.Unspecified"/>.
/// </summary>
public sealed class UtcDateTimeModelBinderProvider : IModelBinderProvider
{
    internal const DateTimeStyles SupportedStyles =
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Metadata.UnderlyingOrModelType != typeof(DateTime))
            return null;

        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        return CreateBinder(loggerFactory);
    }

    public static IModelBinder CreateBinder(ILoggerFactory loggerFactory) =>
        new DateTimeModelBinder(SupportedStyles, loggerFactory);
}
