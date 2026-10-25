using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases.Reading;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// NL-DOCS: the data an imported draft is filled with come from the text of the contract, and a wrong value costs more than a missing one
/// (a fiscal code is the key of a person in a registration, a rent is a number the tenant pays). Three contracts as the three types
/// are usually written, each read field by field (the people are invented: their fiscal codes pass the check character, no more), and what
/// the text does not state, or states in two ways, is not read.
/// </summary>
public sealed class LeaseContractTextParserTests
{
    // A canone libero 4+4 with the cedolare secca, a standard template: the people introduced in running text, the rent by the year and by the month.
    internal const string Libero = """
        CONTRATTO DI LOCAZIONE AD USO ABITATIVO
        (Legge 9 dicembre 1998, n. 431, art. 2, comma 1 - 4+4)

        TRA
        Il Sig. Mario Rossi, nato a Roma (RM) il 01/01/1980, residente in Roma (RM),
        Via dei Mille n. 5, codice fiscale RSSMRA80A01H501U, di seguito denominato "Locatore"
        E
        La Sig.ra Giulia Verdi, nata a Milano (MI) il 02/02/1985, residente in Milano (MI),
        Via Argiro n. 7, codice fiscale VRDGLI85B42F205E, cittadinanza italiana, di seguito
        denominata "Conduttore"

        PREMESSO CHE
        il Locatore è proprietario dell'unità immobiliare ad uso abitativo sita in Bari,
        Via Sparano n. 12, piano 2, interno 5, che concede in locazione al Conduttore.

        SI CONVIENE E SI STIPULA QUANTO SEGUE

        Art. 1 - Durata
        Il contratto ha durata di anni 4 (quattro) con decorrenza dal 01/11/2026 e scadenza il
        31/10/2030. Alla prima scadenza il contratto è rinnovato per ulteriori 4 anni.

        Art. 2 - Canone
        Il canone annuo di locazione è convenuto in Euro 9.600,00 (novemilaseicento/00), da
        pagarsi in n. 12 rate mensili anticipate di Euro 800,00 ciascuna entro il giorno 5 di ogni mese.

        Art. 3 - Deposito cauzionale
        A garanzia delle obbligazioni il Conduttore versa a titolo di deposito cauzionale la somma di
        Euro 1.600,00 (milleseicento/00), pari a due mensilità del canone.

        Art. 4 - Regime fiscale
        Il Locatore dichiara di aver optato per il regime della cedolare secca (art. 3 D.Lgs. 23/2011)
        e rinuncia a richiedere l'aggiornamento del canone.

        Letto, confermato e sottoscritto.
        Bari, li 15/10/2026
        Il Locatore                              Il Conduttore
        """;

    // A canone concordato 3+2 as a form prints it: the people in labelled lines (surname first, in capitals), the rent by the month.
    internal const string Concordato = """
        CONTRATTO DI LOCAZIONE AD USO ABITATIVO A CANONE CONCORDATO
        (art. 2, comma 3, legge 9 dicembre 1998 n. 431 - Accordo territoriale del Comune di Monza)

        LOCATORE
        Cognome e Nome: BIANCHI PAOLO          Codice fiscale: BNCPLA68L10F704T
        Residente in: MONZA (MB), Via Italia n. 5

        CONDUTTORE
        Cognome e Nome: NERI ANNA              Codice fiscale: NRENNA92D45F205T
        Residente in: MILANO (MI), Via Torino n. 9

        IMMOBILE
        L'unità immobiliare ad uso abitativo è sita in Monza, Via Manzoni n. 3, piano terzo.

        Art. 1 - Durata. Il contratto ha durata di 3 (tre) anni a decorrere dal 15/11/2026
        fino al 14/11/2029 ed è prorogato di diritto per due anni, salvo disdetta.

        Art. 2 - Canone. Il canone mensile di locazione, determinato ai sensi dell'Accordo
        territoriale, è di Euro 650,00 (seicentocinquanta/00), da versare entro il giorno 5 di ogni mese.

        Art. 3 - Deposito cauzionale. Il Conduttore versa la somma di Euro 1.300,00 pari a due mensilità del canone.

        Art. 4 - Regime fiscale. Il Locatore non ha optato per la cedolare secca.

        Letto, confermato e sottoscritto.
        Monza, li 10/11/2026
        """;

    // A transitory contract, short: the people with their code in brackets, the term as "dal ... al ...", the rent by the year only.
    internal const string Transitorio = """
        CONTRATTO DI LOCAZIONE AD USO ABITATIVO DI NATURA TRANSITORIA
        (art. 5, comma 1, legge 9 dicembre 1998, n. 431 e D.M. 16 gennaio 2017)

        Tra il locatore Sig. Marco Gialli (C.F. GLLMRC75E10L219T), residente in Torino,
        Corso Francia 21, e il conduttore Sig.ra Elena Blu (C.F. BLULNE90P55D612Y),
        residente in Firenze, Via dei Servi 4.

        Il Locatore concede in locazione al Conduttore l'appartamento sito in Torino, Via Po n. 18,
        interno 4, per esigenze abitative transitorie del Conduttore, documentate in contratto.

        Durata: 12 mesi, dal 01/01/2027 al 31/12/2027, senza rinnovo automatico.
        Canone: il canone annuo di locazione è pari a Euro 8.400,00, da corrispondere in rate mensili anticipate.
        Deposito cauzionale: pari a due mensilità del canone.
        Il Locatore ha optato per la cedolare secca.
        Torino, lì 20 dicembre 2026
        """;

    [Fact]
    public void CanoneLibero4Plus4_WithTheCedolareSecca_IsReadFieldByField()
    {
        var reading = LeaseContractTextParser.Parse(Libero, new LeaseContractParseContext("Via Sparano 12", "Bari"));

        var landlord = Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord);
        Assert.Equal("Mario", landlord.FirstName);
        Assert.Equal("Rossi", landlord.LastName);
        Assert.Equal("RSSMRA80A01H501U", landlord.FiscalCode);
        Assert.Equal("Roma (RM), Via dei Mille n. 5", landlord.Residence);
        Assert.Null(landlord.Citizenship); // the contract does not say: the wizard asks
        var tenant = Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal("Giulia", tenant.FirstName);
        Assert.Equal("Verdi", tenant.LastName);
        Assert.Equal("VRDGLI85B42F205E", tenant.FiscalCode);
        Assert.Equal("Milano (MI), Via Argiro n. 7", tenant.Residence);
        Assert.Equal("IT", tenant.Citizenship);
        Assert.All(reading.Parties, p => Assert.True(p.RoleConfidence >= LeaseContractTextParser.AcceptedConfidence && p.NameConfidence >= 90));

        Assert.Equal(LeaseContractType.Libero, reading.ContractType!.Value);
        Assert.Equal(LeaseTaxRegime.CedolareSecca, reading.TaxRegime!.Value);
        Assert.Equal(800m, reading.MonthlyRent!.Value);
        Assert.False(reading.MonthlyRent.Derived); // the month's rent is written, and agrees with the year's
        Assert.Equal(1600m, reading.SecurityDeposit!.Value);
        Assert.False(reading.SecurityDeposit.Derived);
        Assert.Equal(new DateOnly(2026, 11, 1), reading.StartDate!.Value);
        Assert.Equal(new DateOnly(2030, 10, 31), reading.EndDate!.Value);
        Assert.Equal(48, reading.TermMonths!.Value);
        Assert.Equal(new DateOnly(2026, 10, 15), reading.StipulaDate!.Value);
        Assert.Equal(PropertyAddressMatch.Match, reading.Property!.Value);
    }

    [Fact]
    public void CanoneConcordato3Plus2_InTheLayoutOfAForm_IsReadFieldByField_AndAnotherPropertyIsTold()
    {
        var reading = LeaseContractTextParser.Parse(Concordato, new LeaseContractParseContext("Via Cavour 8", "Monza"));

        var landlord = Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord);
        Assert.Equal("Paolo", landlord.FirstName); // "BIANCHI PAOLO" in capitals: the fiscal code says which is the surname
        Assert.Equal("Bianchi", landlord.LastName);
        Assert.Equal("BNCPLA68L10F704T", landlord.FiscalCode);
        Assert.Equal("MONZA (MB), Via Italia n. 5", landlord.Residence);
        var tenant = Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal("Anna", tenant.FirstName);
        Assert.Equal("Neri", tenant.LastName);
        Assert.Equal("NRENNA92D45F205T", tenant.FiscalCode);
        Assert.Equal("MILANO (MI), Via Torino n. 9", tenant.Residence);

        Assert.Equal(LeaseContractType.Concordato, reading.ContractType!.Value);
        Assert.Equal(LeaseTaxRegime.Ordinario, reading.TaxRegime!.Value); // "non ha optato per la cedolare secca"
        Assert.Equal(650m, reading.MonthlyRent!.Value);
        Assert.Equal(1300m, reading.SecurityDeposit!.Value);
        Assert.Equal(new DateOnly(2026, 11, 15), reading.StartDate!.Value);
        Assert.Equal(new DateOnly(2029, 11, 14), reading.EndDate!.Value);
        Assert.Equal(36, reading.TermMonths!.Value);
        Assert.Equal(new DateOnly(2026, 11, 10), reading.StipulaDate!.Value);
        Assert.Equal(PropertyAddressMatch.Mismatch, reading.Property!.Value); // the contract is for Via Manzoni, the draft for Via Cavour
    }

    [Fact]
    public void ContrattoTransitorio_WithTheAnnualRentOnly_WorksOutTheMonthlyRentAndTheDepositAndSaysSo()
    {
        var reading = LeaseContractTextParser.Parse(Transitorio);

        var landlord = Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord);
        Assert.Equal(("Marco", "Gialli", "GLLMRC75E10L219T"), (landlord.FirstName, landlord.LastName, landlord.FiscalCode));
        Assert.Equal("Torino, Corso Francia 21", landlord.Residence);
        var tenant = Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal(("Elena", "Blu", "BLULNE90P55D612Y"), (tenant.FirstName, tenant.LastName, tenant.FiscalCode));
        Assert.Equal("Firenze, Via dei Servi 4", tenant.Residence);

        Assert.Equal(LeaseContractType.Transitorio, reading.ContractType!.Value);
        Assert.Equal(LeaseTaxRegime.CedolareSecca, reading.TaxRegime!.Value);
        Assert.Equal(new DateOnly(2027, 1, 1), reading.StartDate!.Value);
        Assert.Equal(new DateOnly(2027, 12, 31), reading.EndDate!.Value); // a transitory contract has no term of its own: the draft needs the end it states
        Assert.Equal(12, reading.TermMonths!.Value);
        Assert.Equal(700m, reading.MonthlyRent!.Value); // 8,400 a year
        Assert.True(reading.MonthlyRent.Derived);
        Assert.Equal(1400m, reading.SecurityDeposit!.Value); // two months of that
        Assert.True(reading.SecurityDeposit.Derived);
        Assert.Equal(new DateOnly(2026, 12, 20), reading.StipulaDate!.Value);
        Assert.Null(reading.Property); // no property to compare with
    }

    [Fact]
    public void WhatTheContractDoesNotStateOrStatesTwice_IsNotRead()
    {
        const string text = """
            Il Sig. Mario Rossi, nato a Roma il 01/01/1980, residente in Roma, Via dei Mille n. 5, codice fiscale RSSMRA80A01H501U (di seguito il Locatore)
            e la Sig.ra Giulia Verdi, nata a Milano, codice fiscale VRDGLI85B42F205X (di seguito il Conduttore)
            Il canone mensile è di Euro 700,00 per il primo anno; il canone mensile dal secondo anno è di Euro 750,00.
            Il contratto decorre dal 01/02/2027.
            """;

        var reading = LeaseContractTextParser.Parse(text);

        // The tenant's fiscal code does not pass its check character and no OCR confusion puts it right: the tenant is not a person yet.
        var landlord = Assert.Single(reading.Parties);
        Assert.Equal(PartyRole.Landlord, landlord.Role);
        Assert.Equal("RSSMRA80A01H501U", landlord.FiscalCode);
        // A rent that changes over the years is not one rent; a deposit, a type, a regime, an end, a signature the text never states are not guessed.
        Assert.Null(reading.MonthlyRent);
        Assert.Null(reading.SecurityDeposit);
        Assert.Null(reading.ContractType);
        Assert.Null(reading.TaxRegime);
        Assert.Null(reading.EndDate);
        Assert.Null(reading.TermMonths);
        Assert.Null(reading.StipulaDate);
        Assert.Equal(new DateOnly(2027, 2, 1), reading.StartDate!.Value);
    }

    [Fact]
    public void NamesAreOrderedByTheFiscalCode_WhateverTheirOrderInTheText()
    {
        const string text = """
            TRA Il Sig. ROSSI MARIO, nato a Roma il 01/01/1980, residente in Roma, Via dei Mille n. 5, C.F. RSSMRA80A01H501U, di seguito Locatore,
            E la Sig.ra Anna Maria De Luca, nata a Bari il 21/11/1971, residente in Bari, Via Argiro n. 7, C.F. DLCNMR71S61A662D, di seguito Conduttore.
            """;

        var reading = LeaseContractTextParser.Parse(text);

        var landlord = Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord);
        Assert.Equal(("Mario", "Rossi"), (landlord.FirstName, landlord.LastName)); // surname first, in capitals
        var tenant = Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal(("Anna Maria", "De Luca"), (tenant.FirstName, tenant.LastName)); // two given names, a surname with a particle
        Assert.Equal("Bari, Via Argiro n. 7", tenant.Residence);
    }

    [Fact]
    public void AFiscalCodeWithAnOcrConfusion_IsPutRightOnlyWhenTheCheckCharacterThenPasses()
    {
        // The O of "80" is a zero the OCR read as a letter, in a place where only a digit can be: one correction, and the code passes.
        var repaired = Assert.Single(ContractFiscalCodes.Find("codice fiscale RSSMRA8OA01H501U"));
        Assert.Equal("RSSMRA80A01H501U", repaired.Code);
        Assert.True(repaired.Repaired);

        // A wrong check character, or letters that no confusion explains, is no person.
        Assert.Empty(ContractFiscalCodes.Find("codice fiscale RSSMRA80A01H501X"));
        Assert.Empty(ContractFiscalCodes.Find("codice fiscale (novemilaseicento/00)"));
    }
}
