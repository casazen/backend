using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>FD-06: the API pipeline actually uses the UTC JSON converter, model binder and clock.</summary>
public class UtcDateTimeRegistrationTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public void MvcOptions_DateTimeBinding_UsesUtcBinderInsteadOfDefault()
    {
        var providers = factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value.ModelBinderProviders;

        Assert.Contains(providers, p => p is UtcDateTimeModelBinderProvider);
        Assert.DoesNotContain(providers, p => p is DateTimeModelBinderProvider);
        Assert.True(
            providers.ToList().FindIndex(p => p is UtcDateTimeModelBinderProvider)
            < providers.ToList().FindIndex(p => p is SimpleTypeModelBinderProvider));
    }

    [Fact]
    public void JsonOptions_RequestBodies_UseUtcDateTimeConverter()
    {
        var json = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        Assert.Contains(json.Converters, c => c is UtcDateTimeJsonConverter);
    }

    [Fact]
    public void Services_TimeProvider_IsRegistered()
    {
        Assert.Same(TimeProvider.System, factory.Services.GetRequiredService<TimeProvider>());
    }
}
