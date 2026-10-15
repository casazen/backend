using System.Text;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AlloggiatiCodeTableSeedTests
{
    [Fact]
    public async Task ImportAsync_OfficialTipoAlloggiato_LoadsTheFiveKinds()
    {
        await using var db = CreateDb();
        var service = new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Codice,Descrizione\n16,OSPITE SINGOLO\n17,CAPO FAMIGLIA\n18,CAPO GRUPPO\n19,FAMILIARE\n20,MEMBRO GRUPPO\n"));

        var result = await service.ImportAsync(
            AlloggiatiCodeTable.TipiAlloggiato,
            stream,
            "tipo_alloggiato.csv",
            "Polizia di Stato, Portale Alloggiati, scaricato il 2026-10-09",
            "system",
            sourceUrl: "https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/ashx/Download.ashx?ID=3&N=TIPO_ALLOGGIATO",
            authority: AlloggiatiCodeTableService.PoliziaDiStatoAuthority);

        Assert.True(result.Success);
        Assert.Equal(5, result.RowCount);
        var names = await db.AlloggiatiCodeEntries.AsNoTracking()
            .Where(e => e.Table == AlloggiatiCodeTable.TipiAlloggiato)
            .OrderBy(e => e.Code)
            .Select(e => e.Description)
            .ToListAsync();
        Assert.Equal(["OSPITE SINGOLO", "CAPO FAMIGLIA", "CAPO GRUPPO", "FAMILIARE", "MEMBRO GRUPPO"], names);
        var import = await db.AlloggiatiCodeTableImports.SingleAsync();
        Assert.Equal(AlloggiatiCodeTableService.PoliziaDiStatoAuthority, import.Authority);
        Assert.StartsWith("https://alloggiatiweb.poliziadistato.it/", import.SourceUrl);
    }

    [Fact]
    public async Task ImportAsync_SameSha_SkipIfUnchanged_DoesNotReplace()
    {
        await using var db = CreateDb();
        var service = new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance);
        var bytes = Encoding.UTF8.GetBytes("Codice,Descrizione\n16,OSPITE SINGOLO\n");
        await service.ImportAsync(AlloggiatiCodeTable.TipiAlloggiato, new MemoryStream(bytes), "a.csv", "v1", "system");
        var firstId = (await db.AlloggiatiCodeTableImports.SingleAsync()).Id;

        var again = await service.ImportAsync(
            AlloggiatiCodeTable.TipiAlloggiato,
            new MemoryStream(bytes),
            "a.csv",
            "v1",
            "system",
            skipIfUnchanged: true);

        Assert.True(again.Unchanged);
        Assert.Equal(firstId, again.ImportId);
        Assert.Equal(1, await db.AlloggiatiCodeTableImports.CountAsync());
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
}
