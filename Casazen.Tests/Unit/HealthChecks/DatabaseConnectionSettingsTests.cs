using Casazen.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.HealthChecks;

/// <summary>
/// HOSTING: the process must not open more PostgreSQL connections than the free Supabase session pooler grants
/// (about 15), and must refuse the transaction pooler (port 6543), which silently breaks advisory locks and Hangfire.
/// </summary>
public class DatabaseConnectionSettingsTests
{
    private const string SessionPooler =
        "Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.abcdefgh;Password=x";

    private const string TransactionPooler =
        "Host=aws-0-eu-central-1.pooler.supabase.com;Port=6543;Database=postgres;Username=postgres.abcdefgh;Password=x";

    private const string Direct =
        "Host=db.abcdefgh.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=x";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void Resolve_Defaults_FitTheSupabaseSessionPoolerBudget()
    {
        var settings = DatabaseConnectionSettings.Resolve(Config(), SessionPooler, hangfireRegistered: true);

        Assert.Equal(4, settings.WorkerCount);
        Assert.Equal(15, settings.ConnectionBudget);
        Assert.True(settings.TheoreticalMaxConnections < settings.ConnectionBudget);
        Assert.Empty(settings.Evaluate());
    }

    [Fact]
    public void Resolve_DirectSupabaseHost_UsesTheDirectBudget()
    {
        var settings = DatabaseConnectionSettings.Resolve(Config(), Direct, hangfireRegistered: true);

        Assert.Equal(60, settings.ConnectionBudget);
    }

    [Fact]
    public void Evaluate_TransactionPooler_IsAnErrorAndEnsureUsableThrows()
    {
        var settings = DatabaseConnectionSettings.Resolve(Config(), TransactionPooler, hangfireRegistered: true);

        Assert.True(settings.IsTransactionPooler);
        Assert.Contains(settings.Evaluate(), p => p.Severity == DatabaseConnectionSettings.Severity.Error);
        var ex = Assert.Throws<InvalidOperationException>(settings.EnsureUsable);
        Assert.DoesNotContain("abcdefgh", ex.Message);
        Assert.DoesNotContain("Password", ex.Message);
    }

    [Fact]
    public void Evaluate_PoolsAboveTheBudget_WarnsWithoutRevealingTheConnectionString()
    {
        var settings = DatabaseConnectionSettings.Resolve(
            Config(("Hangfire:WorkerCount", "20"), ("Database:MaxPoolSize", "20")), SessionPooler, hangfireRegistered: true);

        var problem = Assert.Single(settings.Evaluate(), p => p.Message.Contains("EMAXCONNSESSION"));
        Assert.Equal(DatabaseConnectionSettings.Severity.Warning, problem.Severity);
        Assert.DoesNotContain("pooler.supabase.com", problem.Message);
        Assert.DoesNotContain("abcdefgh", problem.Message);
    }

    [Fact]
    public void Resolve_MaximumPoolSizeInTheConnectionString_WinsOverTheSetting()
    {
        var settings = DatabaseConnectionSettings.Resolve(
            Config(("Database:MaxPoolSize", "6")), Direct + ";Maximum Pool Size=30", hangfireRegistered: false);

        Assert.Equal(30, settings.MaxPoolSizeEffective);
        Assert.Equal(30, new NpgsqlConnectionStringBuilder(settings.ForEntityFramework()).MaxPoolSize);
    }

    [Fact]
    public void ForHangfire_UsesItsOwnPoolAndApplicationName()
    {
        var settings = DatabaseConnectionSettings.Resolve(Config(), SessionPooler, hangfireRegistered: true);

        var ef = new NpgsqlConnectionStringBuilder(settings.ForEntityFramework());
        var hangfire = new NpgsqlConnectionStringBuilder(settings.ForHangfire());

        Assert.NotEqual(ef.ApplicationName, hangfire.ApplicationName);
        Assert.NotEqual(ef.ToString(), hangfire.ToString());
        Assert.Equal(settings.HangfireMaxPoolSize, hangfire.MaxPoolSize);
        Assert.Equal(settings.MaxPoolSize, ef.MaxPoolSize);
    }

    [Theory]
    [InlineData("Hangfire:WorkerCount", "0")]
    [InlineData("Hangfire:WorkerCount", "abc")]
    [InlineData("Database:MaxPoolSize", "-1")]
    [InlineData("Database:MinPoolSize", "99")]
    public void Resolve_InvalidValue_Throws(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseConnectionSettings.Resolve(Config((key, value)), SessionPooler, hangfireRegistered: true));
    }

    [Theory]
    [InlineData(SessionPooler, false)]
    [InlineData(TransactionPooler, true)]
    [InlineData(Direct, false)]
    [InlineData("", false)]
    [InlineData("not a connection string", false)]
    public void IsTransactionPoolerEndpoint_DetectsPort6543(string connectionString, bool expected)
    {
        Assert.Equal(expected, DatabaseConnectionSettings.IsTransactionPoolerEndpoint(connectionString));
    }
}
