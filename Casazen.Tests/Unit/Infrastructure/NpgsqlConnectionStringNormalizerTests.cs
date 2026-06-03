using Casazen.Infrastructure.Data;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class NpgsqlConnectionStringNormalizerTests
{
    [Fact]
    public void Normalize_LeavesNpgsqlFormatUnchanged()
    {
        const string npgsql =
            "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=dev;SearchPath=casazen_test";

        var result = NpgsqlConnectionStringNormalizer.Normalize(npgsql);

        Assert.Equal(npgsql, result);
    }

    [Fact]
    public void Normalize_ConvertsSupabaseUriWithOptionsSearchPath()
    {
        const string uri =
            "postgresql://postgres:secret@db.example.supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_test";

        var result = NpgsqlConnectionStringNormalizer.Normalize(uri);

        Assert.NotNull(result);
        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal("db.example.supabase.co", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("postgres", builder.Database);
        Assert.Equal("postgres", builder.Username);
        Assert.Equal("secret", builder.Password);
        Assert.Equal("casazen_test", builder.SearchPath);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void Normalize_ConvertsUriWithoutSearchPath()
    {
        const string uri =
            "postgresql://postgres:secret@db.example.supabase.co:5432/postgres?options";

        var result = NpgsqlConnectionStringNormalizer.Normalize(uri);

        Assert.NotNull(result);
        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal("db.example.supabase.co", builder.Host);
        Assert.Null(builder.SearchPath);
    }
}
