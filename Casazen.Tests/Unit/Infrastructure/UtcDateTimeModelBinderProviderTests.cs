using System.Globalization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class UtcDateTimeModelBinderProviderTests
{
    [Theory]
    [InlineData("2026-09-01", "2026-09-01T00:00:00")]
    [InlineData("2026-09-01T10:30:00", "2026-09-01T10:30:00")]
    [InlineData("2026-09-01T10:30:00Z", "2026-09-01T10:30:00")]
    [InlineData("2026-09-01T00:00:00+02:00", "2026-08-31T22:00:00")]
    public async Task BindModelAsync_QueryValue_ReturnsUtc(string raw, string expectedUtc)
    {
        var context = CreateContext(typeof(DateTime?), raw);

        await UtcDateTimeModelBinderProvider.CreateBinder(NullLoggerFactory.Instance).BindModelAsync(context);

        Assert.True(context.Result.IsModelSet);
        var value = Assert.IsType<DateTime>(context.Result.Model);
        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture), value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Fact]
    public async Task BindModelAsync_EmptyNullableValue_BindsNull()
    {
        var context = CreateContext(typeof(DateTime?), string.Empty);

        await UtcDateTimeModelBinderProvider.CreateBinder(NullLoggerFactory.Instance).BindModelAsync(context);

        Assert.True(context.Result.IsModelSet);
        Assert.Null(context.Result.Model);
    }

    [Fact]
    public async Task BindModelAsync_InvalidValue_AddsModelStateError()
    {
        var context = CreateContext(typeof(DateTime), "not-a-date");

        await UtcDateTimeModelBinderProvider.CreateBinder(NullLoggerFactory.Instance).BindModelAsync(context);

        Assert.False(context.Result.IsModelSet);
        Assert.False(context.ModelState.IsValid);
    }

    private static DefaultModelBindingContext CreateContext(Type modelType, string raw)
    {
        var query = new QueryCollection(new Dictionary<string, StringValues> { ["from"] = raw });
        return new DefaultModelBindingContext
        {
            ModelMetadata = new EmptyModelMetadataProvider().GetMetadataForType(modelType),
            ModelName = "from",
            ModelState = new ModelStateDictionary(),
            ValueProvider = new QueryStringValueProvider(BindingSource.Query, query, CultureInfo.InvariantCulture),
        };
    }
}
