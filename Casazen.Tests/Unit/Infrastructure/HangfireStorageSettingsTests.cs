using Casazen.Infrastructure.Data;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>FD-11 (A9-03): test and production share the database but never the Hangfire schema.</summary>
public class HangfireStorageSettingsTests
{
    private const string SupabaseHost = "Host=db.example.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=secret";
    private const string TestConnection = SupabaseHost + ";SearchPath=casazen_test;SSL Mode=Require";
    private const string ProdConnection = SupabaseHost + ";SearchPath=casazen_prod;SSL Mode=Require";

    [Fact]
    public void Resolve_TestAndProdSearchPaths_ProduceDifferentDedicatedSchemas()
    {
        // Railway runs both environments with ASPNETCORE_ENVIRONMENT=Production: only the SearchPath differs.
        var test = HangfireStorageSettings.Resolve(Config(), TestConnection, Environment(Environments.Production));
        var prod = HangfireStorageSettings.Resolve(Config(), ProdConnection, Environment(Environments.Production));

        Assert.Equal("hangfire_casazen_test", test.Schema);
        Assert.Equal("hangfire_casazen_prod", prod.Schema);
        Assert.NotEqual(test.Schema, prod.Schema);
        Assert.NotEqual(HangfireStorageSettings.SharedLegacySchema, test.Schema);
        Assert.NotEqual(HangfireStorageSettings.SharedLegacySchema, prod.Schema);
    }

    [Fact]
    public void Resolve_ExplicitSchemas_AreUsedForEachEnvironment()
    {
        var test = HangfireStorageSettings.Resolve(
            Config((HangfireStorageSettings.SchemaKey, "hangfire_test")), TestConnection, Environment(Environments.Production));
        var prod = HangfireStorageSettings.Resolve(
            Config((HangfireStorageSettings.SchemaKey, " hangfire_prod ")), ProdConnection, Environment(Environments.Production));

        Assert.Equal("hangfire_test", test.Schema);
        Assert.Equal("hangfire_prod", prod.Schema);
    }

    [Fact]
    public void Resolve_SupabaseUriWithOptionsSearchPath_DerivesSchemaFromSearchPath()
    {
        var connection = NpgsqlConnectionStringNormalizer.Normalize(
            "postgresql://postgres:secret@db.example.supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_prod")!;

        var settings = HangfireStorageSettings.Resolve(Config(), connection, Environment(Environments.Production));

        Assert.Equal("hangfire_casazen_prod", settings.Schema);
    }

    [Theory]
    [InlineData(SupabaseHost + ";SearchPath=casazen_test,public", "hangfire_casazen_test")]
    [InlineData(SupabaseHost + ";SearchPath='\"$user\", casazen_prod'", "hangfire_casazen_prod")]
    [InlineData(SupabaseHost + ";SearchPath=Casazen_Prod", "hangfire_casazen_prod")]
    [InlineData(SupabaseHost + ";Options=-c search_path=casazen_test", "hangfire_casazen_test")]
    public void ResolveSchema_SearchPathVariants_UseFirstSchema(string connection, string expected)
    {
        Assert.Equal(expected, HangfireStorageSettings.ResolveSchema(null, connection, Environments.Production));
    }

    [Fact]
    public void ResolveSchema_SharedHangfireSchemaWithSearchPath_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HangfireStorageSettings.ResolveSchema("hangfire", ProdConnection, Environments.Production));

        Assert.Contains("hangfire_casazen_prod", ex.Message);
    }

    [Fact]
    public void ResolveSchema_ProductionWithoutSearchPathOrExplicitSchema_ThrowsAmbiguous()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HangfireStorageSettings.ResolveSchema(null, SupabaseHost, Environments.Production));

        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("Hangfire__Schema", ex.Message);
    }

    [Fact]
    public void ResolveSchema_ProductionWithoutSearchPath_ExplicitSchemaIsAccepted()
    {
        // A dedicated database per environment (e.g. two Supabase projects) may keep the classic name.
        Assert.Equal("hangfire", HangfireStorageSettings.ResolveSchema("hangfire", SupabaseHost, Environments.Production));
    }

    [Theory]
    [InlineData("Development", "hangfire_development")]
    [InlineData("Staging", "hangfire_staging")]
    [InlineData("Local-QA", "hangfire_local_qa")]
    public void ResolveSchema_NonProductionWithoutSearchPath_DerivesFromEnvironment(string environment, string expected)
    {
        Assert.Equal(expected, HangfireStorageSettings.ResolveSchema(null, "Host=localhost;Database=casazen_dev", environment));
    }

    [Theory]
    [InlineData("hangfire;drop schema x")]
    [InlineData("Hangfire_Prod")]
    [InlineData("1hangfire")]
    [InlineData("hangfire-prod")]
    public void ResolveSchema_InvalidExplicitSchema_Throws(string schema)
    {
        Assert.Throws<InvalidOperationException>(() =>
            HangfireStorageSettings.ResolveSchema(schema, ProdConnection, Environments.Production));
    }

    [Fact]
    public void ResolveSchema_DerivedNameTooLong_ThrowsAskingForExplicitSchema()
    {
        var connection = SupabaseHost + ";SearchPath=" + new string('s', 60);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            HangfireStorageSettings.ResolveSchema(null, connection, Environments.Production));

        Assert.Contains(HangfireStorageSettings.SchemaKey, ex.Message);
    }

    [Fact]
    public void Resolve_DistributedLockTimeout_DefaultsTo30MinutesAndReadsConfiguration()
    {
        var byDefault = HangfireStorageSettings.Resolve(Config(), ProdConnection, Environment(Environments.Production));
        var configured = HangfireStorageSettings.Resolve(
            Config((HangfireStorageSettings.DistributedLockTimeoutKey, "45")), ProdConnection, Environment(Environments.Production));

        Assert.Equal(TimeSpan.FromMinutes(30), byDefault.DistributedLockTimeout);
        Assert.Equal(TimeSpan.FromMinutes(45), configured.DistributedLockTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten")]
    public void ResolveDistributedLockTimeout_InvalidValue_Throws(string value)
    {
        Assert.Throws<InvalidOperationException>(() => HangfireStorageSettings.ResolveDistributedLockTimeout(value));
    }

    [Fact]
    public void CreateStorageOptions_UsesResolvedSchemaAndLockTimeout()
    {
        var settings = HangfireStorageSettings.Resolve(Config(), TestConnection, Environment(Environments.Production));

        var options = settings.CreateStorageOptions();

        Assert.Equal("hangfire_casazen_test", options.SchemaName);
        Assert.Equal(settings.DistributedLockTimeout, options.DistributedLockTimeout);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static IHostEnvironment Environment(string name) => new TestHostEnvironment { EnvironmentName = name };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Casazen.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
