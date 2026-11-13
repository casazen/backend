using System.Text;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F03/US05/T01 (NF-101, brief AC3): the CSV of every list report is, byte for byte, the one the host reports wrote before the refactor into <c>ReportTable</c>.
/// The expected files were not written by the code under test: they were written by the documents of <c>origin/develop</c> at <c>08f955e9</c> (the code before the
/// refactor), run on the fixed inputs of <see cref="ReportCsvGoldenInputs"/>, and pasted here as the rows of each file. A file is the BOM, <c>;</c> between the cells,
/// a decimal comma, a percentage as the percentage, a day as <c>dd/MM/yyyy</c>, a text a spreadsheet would run with a leading apostrophe, and a <c>CRLF</c> after every
/// row, the last included.
/// </summary>
public class ReportCsvGoldenTests
{
    // One entry for each list report, one string for each row of its file, as origin/develop (08f955e9) wrote them.
    private static readonly Dictionary<string, string[]> Expected = new()
    {
        ["revenue-by-property"] =
        [
            "Ricavo per immobile, per competenza, dal 01/10/2026 al 31/10/2026",
            "Immobile;Ricavo per competenza (€);Soggiorni;Notti",
            "Casa Bianca;1234,56;3;12",
            "'=SUM(A1) Villa;0,07;1;1",
            "\"Trullo; il \"\"Nuovo\"\"\";2,68;2;5",
            "Rimborsata;-0,01;0;0",
            "Vuota;0,00;0;0",
            "Totale;1237,31;6;18",
        ],
        ["occupancy-by-property"] =
        [
            "Occupazione per immobile, notti occupate su notti disponibili, dal 01/10/2026 al 31/10/2026",
            "Immobile;Notti occupate;Notti disponibili;Notti chiuse dall'host;Occupazione (%)",
            "Casa Bianca;10;31;2;32,26",
            "Trullo;0;0;0;",
            "Lunga attesa;1;20000;0;0,01",
            "'+Chiusa;0;7;24;0,00",
            "Totale;11;20038;26;0,05",
        ],
        ["bookings-by-channel"] =
        [
            "Prenotazioni per canale, per competenza, dal 01/10/2026 al 31/10/2026",
            "Canale;Prenotazioni;Notti;Ricavo per competenza (€);Quota delle prenotazioni (%)",
            "Sito di prenotazione;1;3;100,01;14,29",
            "Airbnb;2;4;99,99;28,57",
            "Booking.com;4;20;1500,50;57,14",
            "Totale;7;27;1700,50;100,00",
        ],
        ["performance-total"] =
        [
            "Prestazioni complessive, per competenza, dal 01/10/2026 al 31/10/2026",
            "Perimetro;Notti occupate;Notti disponibili;Notti chiuse dall'host;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€)",
            "Totale;10;31;2;32,26;1234,56;3;12;102,88;39,82",
        ],
        ["performance-by-property-compared"] =
        [
            "Prestazioni per immobile, per competenza, dal 01/10/2026 al 31/10/2026, confrontato con dal 01/10/2025 al 31/10/2025",
            "Immobile;Notti occupate;Notti disponibili;Notti chiuse dall'host;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€);Occupazione anno precedente (%);Ricavo anno precedente (€);ADR anno precedente (€);RevPAR anno precedente (€);Variazione ricavo (%);Variazione occupazione (punti);Variazione ADR (%);Variazione RevPAR (%)",
            "Casa Bianca;10;31;2;32,26;1234,56;3;12;102,88;39,82;25,81;1000,00;125,00;32,26;23,50;6,50;-17,70;23,40",
            "'=Villa;1;20000;3;0,01;2,68;3;7;0,38;0,00;0,01;0,50;0,17;0,00;435,00;0,00;123,50;",
            "Trullo;0;0;0;;0,00;0;0;;;;0,00;;;;;;",
            "Totale;10;31;2;32,26;1234,56;3;12;102,88;39,82;25,81;1000,00;125,00;32,26;23,50;6,50;-17,70;23,40",
        ],
        ["performance-by-channel"] =
        [
            "Prestazioni per canale, per competenza, dal 01/10/2026 al 31/10/2026",
            "Canale;Notti occupate;Notti disponibili;Notti chiuse dall'host;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€)",
            "Sito di prenotazione;;;;;1234,56;3;12;102,88;",
            "Airbnb;;;;;2,68;3;7;0,38;",
            "Booking.com;;;;;0,00;0;0;;",
            "Totale;;;;;1234,56;3;12;102,88;",
        ],
        ["trend"] =
        [
            "Andamento, per competenza, dal 01/10/2026 al 31/10/2026",
            "Perimetro;Periodo;Dal;Al;Notti occupate;Notti disponibili;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€)",
            "Totale;2026-W40;28/09/2026;04/10/2026;4;7;57,14;312,50;1;4;78,13;44,64",
            "Totale;2026-W41;05/10/2026;11/10/2026;0;7;0,00;0,00;0;0;;0,00",
        ],
        ["trend-compared"] =
        [
            "Andamento, per competenza, dal 01/10/2026 al 31/10/2026, confrontato con dal 01/10/2025 al 31/10/2025",
            "Perimetro;Periodo;Dal;Al;Notti occupate;Notti disponibili;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€);Occupazione anno precedente (%);Ricavo anno precedente (€);ADR anno precedente (€);RevPAR anno precedente (€)",
            "Totale;2026-W40;28/09/2026;04/10/2026;4;7;57,14;312,50;1;4;78,13;44,64;42,86;200,00;66,67;28,57",
            "Totale;2026-W41;05/10/2026;11/10/2026;0;7;0,00;0,00;0;0;;0,00;;;;",
        ],
        ["trend-by-property-compared"] =
        [
            "Andamento per immobile, per competenza, dal 01/10/2026 al 31/10/2026, confrontato con dal 01/10/2025 al 31/10/2025",
            "Immobile;Periodo;Dal;Al;Notti occupate;Notti disponibili;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€);Occupazione anno precedente (%);Ricavo anno precedente (€);ADR anno precedente (€);RevPAR anno precedente (€)",
            "Casa Bianca;2026-W40;28/09/2026;04/10/2026;4;7;57,14;200,13;1;4;50,03;28,59;42,86;200,00;66,67;28,57",
            "Casa Bianca;2026-W41;05/10/2026;11/10/2026;0;7;0,00;0,00;0;0;;0,00;;;;",
            "Altri immobili;2026-W40;28/09/2026;04/10/2026;4;7;57,14;112,38;1;4;28,09;16,05;42,86;200,00;66,67;28,57",
            "Altri immobili;2026-W41;05/10/2026;11/10/2026;0;7;0,00;0,00;0;0;;0,00;;;;",
        ],
        ["collections"] =
        [
            "Incassato per data, per cassa, dal 01/10/2026 al 31/10/2026",
            "Dal;Al;Incassato (€);Pagamenti",
            "01/10/2026;01/10/2026;1000,25;3",
            "02/10/2026;02/10/2026;500,26;1",
            "Totale;;1500,51;4",
            "",
            "Incassato per metodo di pagamento, per cassa, dal 01/10/2026 al 31/10/2026",
            "Metodo di pagamento;Incassato (€);Pagamenti;Quota (%)",
            "Carta;1000,25;3;66,66",
            "Contanti all'arrivo;500,26;1;33,34",
            "Totale;1500,51;4;100,00",
            "",
            "Incassato per canale, per cassa, dal 01/10/2026 al 31/10/2026",
            "Canale;Incassato (€);Pagamenti;Quota (%)",
            "Sito di prenotazione;1500,51;4;100,00",
            "Totale;1500,51;4;100,00",
        ],
        ["balances-to-collect"] =
        [
            "Saldi da incassare, al 09/10/2026",
            "Codice prenotazione;Immobile;Canale;Check-in;Check-out;Valuta;Totale;Incassato;Da incassare;Scaduto;Importi indicati da",
            "ABCDE-FGHJK;Casa Bianca;Sito di prenotazione;01/10/2026;05/10/2026;EUR;500,00;200,50;299,50;Sì;CasaZen",
            "KLMNP-QRSTV;Chalet Lugano;Airbnb;20/10/2026;25/10/2026;CHF;;;120,00;No;Channel manager",
            "WXYZ2-34567;'=Casa;Booking.com;02/11/2026;04/11/2026;EUR;80,01;0,00;80,01;No;CasaZen",
            "Totale;;;;;EUR;;;379,51;;",
            "Totale;;;;;CHF;;;120,00;;",
        ],
        ["channel-costs"] =
        [
            "Costi dei canali, per competenza, dal 01/10/2026 al 31/10/2026",
            "Canale;Tipo di costo;Soggiorni;Lordo per competenza (€);Costo (€);Netto (€);Costo sul lordo (%);Come si paga la commissione;Soggiorni senza costo indicato;Il più costoso",
            "Airbnb;Commissione del canale;3;1500,50;225,08;1275,42;15,00;Trattenuta dal pagamento;0;Sì",
            "Sito di prenotazione;Nessun costo;1;99,99;;;;;1;",
            "Booking.com;Commissioni sui pagamenti con carta;2;300,01;12,35;287,66;4,12;Fatturata a parte;0;",
            "Totale;;6;1900,50;237,43;1663,07;12,50;;1;",
        ],
        ["by-owner"] =
        [
            "Prestazioni per proprietario, per competenza, dal 01/10/2026 al 31/10/2026",
            "Proprietario;Immobili;Notti occupate;Notti disponibili;Notti chiuse dall'host;Occupazione (%);Ricavo per competenza (€);Soggiorni;Notti dei soggiorni;ADR (€);RevPAR (€)",
            "'=Mario Rossi;2;10,50;31,00;0,00;33,87;1234,57;1,50;10,50;117,58;39,82",
            "Proprietario anonimizzato;1;3,00;31,00;2,00;9,68;100,00;1,00;3,00;33,33;3,23",
            "Senza proprietario;1;0,00;0,00;0,00;;0,00;0,00;0,00;;",
            "Totale;;13,50;62,00;2,00;21,77;1334,57;2,50;13,50;98,86;21,53",
        ],
        ["tourist-tax"] =
        [
            "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, versata con periodo compreso nel periodo, dal 01/01/2026 al 31/12/2026",
            "Comune;Soggiorni;Notti;Ospiti;Imposta dovuta (€);Soggiorni senza importo;Imposta versata (€);Saldo (€);Stato;Nota",
            "Roma;10;25;40;123,45;1;100,00;23,45;Da versare;Importo incompleto",
            "Firenze;2;4;5;10,00;0;20,00;-10,00;Versata in più;",
            "Lecco;2;5;4;0,00;2;0,00;0,00;Nessun importo registrato;Importo incompleto",
            "Totale;14;34;49;133,45;3;120,00;13,45;;Importo incompleto",
            "",
            "Versamenti registrati",
            "Comune;Periodo dal;Periodo al;Importo versato (€);Data del versamento;Riferimento;Note;Ricevuta",
            "Roma;01/01/2026;31/03/2026;100,00;10/04/2026;Bonifico 123;'=nota del commercialista;Sì",
            "Roma;01/04/2026;30/06/2026;0,01;10/07/2026;;\"Nota; con \"\"virgolette\"\"\";No",
        ],
        ["tourist-tax-owed-only"] =
        [
            "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, solo degli immobili dell'ambito di accesso (versamenti non mostrati), dal 01/01/2026 al 31/12/2026",
            "Comune;Soggiorni;Notti;Ospiti;Imposta dovuta (€);Soggiorni senza importo;Nota",
            "Roma;10;25;40;123,45;1;Importo incompleto",
            "Firenze;2;4;5;10,00;0;",
            "Lecco;2;5;4;0,00;2;Importo incompleto",
            "Totale;14;34;49;133,45;3;Importo incompleto",
        ],
    };

    public static IEnumerable<object[]> Reports() => ReportCsvGoldenInputs.Reports.Select(report => new object[] { report });

    [Fact]
    public void Golden_HoldsAFileForEveryListReport_AndNothingElse()
    {
        Assert.Equal(ReportCsvGoldenInputs.Reports.Order(StringComparer.Ordinal), Expected.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void Csv_OfEveryListReport_IsByteForByteTheOneDevelopWroteBeforeTheRefactor_Test(string report)
    {
        var expected = Expected[report];
        var file = ReportCsvGoldenInputs.Csv(report);

        Assert.Equal(Encoding.UTF8.GetPreamble(), file.Take(3));
        var text = Encoding.UTF8.GetString(file, 3, file.Length - 3);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.Equal(expected, text[..^2].Split("\r\n"));

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Concat(expected.Select(row => row + "\r\n")))).ToArray();
        Assert.Equal(bytes, file);
    }
}
