namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// A synthetic document in the format of the real one that the guided service delivers (CF-DOCS-REAL): the signed contract, the attestation of
/// conformity and the calculation sheet of the rent in one file, as the text of its fourteen pages (an OCR gives it this way). The people, the
/// fiscal codes (the two of the simulator, which pass the check character), the address and the figures are invented; the headings, the order
/// of the sections, the wording of the sentences the readers look for and the traps of the real text (the dates of expiry of the passport and of
/// the energy certificate on the first page, the attestation that repeats the rent, the mention of the attestation in the last page of the
/// contract, the numbering that starts again in each section) are those of the real document. No real document is copied here.
/// </summary>
public static class RealFormatDocument
{
    public const string LandlordFiscalCode = "RSSMRA80A01H501U";
    public const string TenantFiscalCode = "VRDGLI85B42F205E";

    /// <summary>Contract: pages 1 to 6. Attestation: pages 7 to 10. Calculation sheet: pages 11 to 14.</summary>
    public static string[] Pages() =>
    [
        // ---- the contract ------------------------------------------------------------------------------------------------
        """
        Contratto di Locazione Abitativa Agevolata
        LOCAZIONE ABITATIVA
        (Legge 9 dicembre 1998, n. 431, articolo 2, comma 3)
        Contratto tipo locale previsto dall'art. 4.2 dell'Accordo locale per i Comuni dell'ambito di Garbagnate Milanese, sottoscritto in Milano il 20 maggio 2025, conforme all'Allegato A del D.M. 16 gennaio 2017
        Il Sig. Mario Rossi nato in Roma il 01/01/1980 residente in Roma (RM), Via dei Mille n. 5
        C.F. RSSMRA80A01H501U proprietario dell'unità immobiliare per la quota di 1/1, di seguito
        denominato locatore, concede in locazione
        alla Sig.ra Giulia Verdi nata in Milano il 02/02/1985 residente in Milano (MI), Via Argiro n. 7
        C.F. VRDGLI85B42F205E cittadinanza italiana di seguito denominata conduttrice, identificata
        mediante passaporto n. AA1234567 rilasciato il 03/03/2020 dalle autorità di Italia con scadenza 03/03/2030
        che accetta, per sé e suoi aventi causa,
        A) l'unità immobiliare posta in Bollate (MI), via Verdi n. 12 composta di n. 2 vani, oltre cucina abitabile e servizi, e dotata altresì dei seguenti elementi accessori: cantina; balcone; area verde condominiale in godimento.
        a) estremi catastali identificativi dell'unità immobiliare: Catasto Fabbricati del Comune di Bollate, foglio 5 particella 120 sub. 3 categoria A/3, classe 3, consistenza 4 vani, rendita catastale EUR 350,00
        b) prestazione energetica: classe E, attestato di prestazione energetica cod. 1234567890 emesso il 01/02/2025 dal geom. Bianchi e con scadenza 01/02/2035. Il conduttore dichiara di essere a conoscenza delle caratteristiche energetiche dell'unità immobiliare locata.
        La locazione è regolata dalle pattuizioni seguenti.
        Articolo 1 (Durata)
        Il contratto è stipulato per la durata di 3 (tre) anni, dal 19/09/2026 al 18/09/2029, e alla prima scadenza, ove le parti non concordino sul rinnovo del medesimo, il contratto è prorogato di diritto di due anni, fatta salva la facoltà di disdetta da parte del locatore. Alla scadenza del periodo di proroga biennale (18/09/2031) ciascuna parte ha diritto di attivare
        Pag. 1
        """,
        """
        la procedura per il rinnovo a nuove condizioni ovvero per la rinuncia al rinnovo del contratto, comunicando la propria intenzione con lettera raccomandata da inviare all'altra parte almeno sei mesi prima della scadenza.
        Articolo 2 (Canone)
        Il canone annuo di locazione, secondo quanto stabilito dall'Accordo locale per i Comuni dell'ambito di Garbagnate Milanese sottoscritto in Milano il 20/05/2025 e depositato presso il Comune di Bollate, è convenuto in EUR 7.140,00 (settemilacentoquaranta/00), che il conduttore si obbliga a corrispondere nel domicilio della locatrice ovvero a mezzo di bonifico bancario sul conto corrente intestato al locatore, IBAN IT00A0000000000000000000000 in n. 12 (dodici) rate eguali anticipate di EUR 595,00 (cinquecentonovantacinque/00) ciascuna, da versare entro il giorno 5 (cinque) di ogni mese.
        Nel caso in cui l'Accordo territoriale di cui al presente punto lo preveda, il canone viene aggiornato ogni anno nella misura contrattata del 75% della variazione Istat, ed esclusivamente nel caso in cui il locatore non abbia optato per la «cedolare secca» per la durata dell'opzione. Avendo la parte locatrice optato per la cedolare secca, è sospesa, per un periodo di tempo corrispondente alla durata dell'opzione, la facoltà di richiedere l'aggiornamento del canone a qualsiasi titolo, inclusa la variazione dell'indice Istat.
        Articolo 3 (Deposito cauzionale e altre forme di garanzia)
        A garanzia delle obbligazioni assunte col presente contratto, il conduttore versa al locatore (che con la firma del contratto ne rilascia quietanza) una somma di EUR 1.785,00 (millesettecentottantacinque/00) pari a n. 3 (tre) mensilità del canone, non imputabile in conto canoni e produttiva di interessi legali. Il deposito cauzionale così costituito viene reso al termine della locazione.
        Eventuali altre forme di garanzia: nessuna.
        Articolo 4 (Oneri accessori)
        Per gli oneri accessori le parti fanno applicazione della Tabella oneri accessori, allegato D al decreto del 16 gennaio 2017. Il conduttore versa una quota di EUR 225,00 (duecentoventicinque/00) con periodicità mensile, salvo conguaglio.
        Pag. 2
        """,
        """
        Articolo 5 (Spese di bollo e di registrazione)
        Le spese di bollo per il presente contratto e per le ricevute conseguenti sono a carico del conduttore. Il locatore provvede alla registrazione del contratto. Avendo la parte locatrice optato per l'applicazione della cedolare secca, per la durata dell'opzione non sono dovute imposte di bollo e di registro.
        Articolo 6 (Pagamento)
        Il pagamento del canone o di quant'altro dovuto anche per oneri accessori non può venire sospeso o ritardato da pretese o eccezioni del conduttore, quale ne sia il titolo.
        Articolo 7 (Uso)
        L'immobile deve essere destinato esclusivamente a civile abitazione del conduttore.
        Articolo 8 (Recesso del conduttore)
        È facoltà del conduttore recedere dal contratto per gravi motivi, previo avviso da recapitarsi tramite lettera raccomandata almeno sei mesi prima.
        Articolo 9 (Consegna)
        Il conduttore dichiara di aver visitato l'unità immobiliare locatagli e di averla trovata adatta all'uso convenuto.
        Pag. 3
        """,
        """
        Articolo 10 (Modifiche e danni)
        Il conduttore non può apportare alcuna modifica, innovazione, miglioria o addizione ai locali locati.
        Articolo 11 (Assemblee)
        Il conduttore ha diritto di voto, in luogo del proprietario dell'unità immobiliare locata, nelle deliberazioni dell'assemblea condominiale relative alle spese.
        Articolo 12 (Impianti)
        Il conduttore, in caso d'installazione sullo stabile di antenna televisiva centralizzata, si obbliga a servirsi unicamente dell'impianto relativo.
        Articolo 13 (Accesso)
        Il conduttore deve consentire l'accesso all'unità immobiliare al locatore.
        Articolo 14 (Commissione di negoziazione paritetica e conciliazione stragiudiziale)
        La Commissione di cui all'articolo 6 del decreto del Ministro delle infrastrutture è composta da due membri.
        Pag. 4
        """,
        """
        Articolo 15 (Varie)
        A tutti gli effetti del presente contratto, compresa la notifica degli atti esecutivi, il conduttore elegge domicilio nei locali a lui locati.
        Altre clausole:
        a) Clausola risolutiva espressa. Il mancato pagamento, anche parziale, del canone protratto oltre venti giorni dalla scadenza, costituisce motivo di risoluzione ai sensi dell'articolo 5 della legge n. 392/1978.
        b) Foro competente. Per ogni controversia relativa al presente contratto è competente in via esclusiva il Foro di Milano.
        Letto, approvato e sottoscritto
        Bollate, li 19-9-26
        La Parte Locatrice La Parte Conduttrice
        Pag. 5
        """,
        """
        A mente degli articoli 1341 e 1342 del codice civile, le parti specificamente approvano i patti di cui agli articoli 3 (Deposito cauzionale e altre forme di garanzia), 4 (Oneri accessori), 6 (Pagamento, risoluzione) e 15 (Varie) del presente contratto.
        La Parte Locatrice La Parte Conduttrice
        Il presente contratto non è assistito; la rispondenza all'Accordo Territoriale è oggetto di Attestazione di Rispondenza separata, rilasciata da Confproprietà — Confederazione della Proprietà Edilizia, con sede in Via Armando Diaz n. 8, 80134 Napoli (NA), C.F. 80123450583 e da FEDERCASA.CONFSAL.
        Pag. 6
        """,

        // ---- the attestation of conformity ---------------------------------------------------------------------------------
        """
        Attestazione di Rispondenza
        (D.M. 16 gennaio 2017)
        Accordo locale per i Comuni dell'ambito di Garbagnate Milanese, sottoscritto in Milano il 20 maggio 2025 — Comune di Bollate (MI)
        L'Organizzazione della proprietà edilizia Confproprietà — Confederazione della Proprietà Edilizia, con sede in Via Armando Diaz n. 8, 80134 Napoli (NA), C.F. 80123450583 in persona del proprio legale rappresentante pro tempore, aderente all'Accordo locale per i Comuni dell'ambito di Garbagnate Milanese del 20/05/2025 ai sensi dell'art. 14 dell'Accordo medesimo, e l'Organizzazione sindacale dei conduttori FEDERCASA.CONFSAL, aderente al medesimo Accordo ai sensi dell'art. 14 dell'Accordo stesso, in persona del proprio legale rappresentante pro tempore, rilasciano congiuntamente la presente attestazione ai sensi dell'art. 12 dell'Accordo e dell'art. 1, comma 8, del D.M. 16 gennaio 2017, secondo il modello di cui all'Allegato 6 dell'Accordo stesso.
        Parti e immobile
        Dato Identificazione
        Parte locatrice Mario Rossi nato in Roma il 01/01/1980 residente in Roma (RM), Via dei Mille n. 5 C.F. RSSMRA80A01H501U proprietario dell'unità immobiliare per la quota di 1/1
        Parte conduttrice Giulia Verdi nata in Milano il 02/02/1985 C.F. VRDGLI85B42F205E cittadinanza italiana residente in Milano (MI), Via Argiro n. 7
        Immobile Bollate (MI), Via Verdi n. 12 CAP 20021
        Tipologia di contratto Abitativo ordinario [X] · Transitorio [ ] · Studenti universitari [ ]
        Durata del contratto 3 anni, prorogabili di 2 (36 mesi di durata iniziale, 60 mesi complessivi) — dal 19/09/2026 al 18/09/2029
        Elementi oggettivi dell'unità immobiliare
        Elemento Caratteristica Sì NO Punti
        1 Cucina abitabile con almeno 1 finestra [X] [ ] 2
        2 Presenza di ascensore per unità abitative situate al secondo piano o superiore [X] [ ] 3
        Pag. 1
        """,
        """
        Elemento Caratteristica Sì NO Punti
        7 Alloggio dotato di impianto solare/termico, fotovoltaico o cappotto termico [ ] [X] 3
        8 Presenza di cantina o soffitta [X] [ ] 2
        25 Installazione Amazon locker/Key o servizio di portineria [ ] [X] 2
        Punteggio totale: 25 punti (elementi 1, 2, 3, 5, 6, 8, 10, 11, 13, 15, 16, 17, 21, 22, 23).
        Gli immobili che non superano i 13 punti sono collocati nella sub-fascia 1; quelli da 14 a 21 punti nella sub-fascia 2; quelli con almeno 22 punti nella sub-fascia 3. L'unità immobiliare è pertanto collocata nella sub-fascia 3.
        Superficie convenzionale
        Ai sensi dell'art. 4.1.c) dell'Accordo la superficie cui applicare il canone unitario è quella prevista dal D.P.R. 138/98, ovvero la superficie catastale.
        Pag. 2
        """,
        """
        Tipologia superficie Mq. da visura Coefficiente Mq. ricalcolati
        Superficie totale o porzione di appartamento locata 66,00 1 66,00
        Totale mq 66,00
        La superficie catastale dell'unità immobiliare è di mq 66,00, sia come superficie totale sia come superficie esclusa delle aree scoperte.
        Altri parametri per la determinazione del canone
        Alloggio ammobiliato (SÌ/NO): NO (maggiorazione del canone fino al 15% non applicata)
        Classe energetica: E — la riduzione del 2% prevista per la classe G non trova applicazione
        Canone mensile convenuto: EUR 595,00
        Maggiore durata contrattuale: non pattuita — durata 3+2
        Accordo e contratto
        L'unità immobiliare ricade nella Zona 1 del Comune di Bollate, corrispondente alla zona OMI B1 aggiornata al secondo semestre 2024 (art. 4.1.a) dell'Accordo. Per la Zona 1 del Comune di Bollate l'Allegato 2 dell'Accordo fissa, per la sub-fascia 3, valori di riferimento da EUR 9,00 a EUR 11,00 al mq per mese.
        Applicati alla superficie convenzionale di mq 66,00 e al coefficiente 1,00, i valori di riferimento della sub-fascia 3 corrispondono a un canone da EUR 594,00 a EUR 726,00 mensili (da EUR 7.128,00 a EUR 8.712,00 annui).
        Il canone convenuto fra le parti è di EUR 595,00 mensili, pari a EUR 7.140,00 annui e a EUR 9,02 al mq per mese (EUR 108,18 al mq per anno), oltre oneri accessori. Il contratto è stipulato ai sensi dell'art. 2, comma 3, della legge 9 dicembre 1998 n. 431.
        Dichiarazione delle parti
        La Sig.ra Maria Bianchi locatrice, e il Sig. Luca Neri conduttore, dichiarano che i dati sopra comunicati sono stati dagli stessi rilevati e verificati. Pertanto, riconoscendo che l'Attestazione sarà rilasciata,
        Pag. 3
        """,
        """
        secondo quanto previsto dal D.M. 16/01/2017, sulla base di quanto dichiarato dagli stessi, sollevano espressamente l'Organizzazione Confproprietà — Confederazione della Proprietà Edilizia e l'Organizzazione FEDERCASA.CONFSAL da qualsiasi responsabilità derivante da errori, omissioni o asserzioni non veritiere relative ai dati dichiarati nel presente atto.
        La Parte Locatrice La Parte Conduttrice
        Attestazione
        Tutto ciò premesso, l'Organizzazione Confproprietà — Confederazione della Proprietà Edilizia e l'Organizzazione FEDERCASA.CONFSAL, entrambe aderenti all'Accordo locale per i Comuni dell'ambito di Garbagnate Milanese ai sensi dell'art. 14 dell'Accordo medesimo, sulla base degli elementi oggettivi sopra dichiarati, anche ai fini dell'ottenimento di eventuali agevolazioni fiscali,
        ATTESTANO
        che i contenuti economici del contratto di locazione sopra descritto, relativo all'unità immobiliare sita in Bollate (MI), Via Verdi n. 12 distinta al Catasto Fabbricati del Comune di Bollate al foglio 5 particella 120 sub. 3 categoria A/3, classe 3, consistenza 4 vani, rendita catastale EUR 350,00 rientrano nei valori previsti dall'Accordo territoriale vigente per il Comune di Bollate, depositato ai sensi dell'art. 7, comma 2, del D.M. 16 gennaio 2017.
        Sottoscrizioni
        Luogo e data Bollate 19-9-26
        Confproprietà — Confederazione della Proprietà Edilizia Organizzazione della proprietà edilizia attestante
        FEDERCASA.CONFSAL Organizzazione sindacale dei conduttori attestante Il legale rappresentante pro tempore Timbro e firma
        Pag. 4
        """,

        // ---- the calculation sheet --------------------------------------------------------------------------------------------
        """
        Scheda Riepilogativa Canone
        Accordo locale per i Comuni dell'ambito di Garbagnate Milanese, sottoscritto in Milano il 20 maggio 2025 — Comune di Bollate (MI)
        Contratto di locazione ad uso abitativo a canone concordato (agevolato 3+2), ai sensi dell'art. 2, comma 3, della legge 9 dicembre 1998 n. 431
        Parti e immobile
        Dato Identificazione
        Parte locatrice Mario Rossi nato in Roma il 01/01/1980 C.F. RSSMRA80A01H501U proprietario dell'unità immobiliare per la quota di 1/1
        Parte conduttrice Giulia Verdi nata in Milano il 02/02/1985 cittadinanza italiana C.F. VRDGLI85B42F205E
        Immobile Bollate (MI), Via Verdi n. 12 CAP 20021
        Durata e decorrenza 3 anni prorogabili di 2 — 36 mesi di durata iniziale, 60 mesi complessivi; decorrenza dal 19/09/2026, prima scadenza al 18/09/2029, scadenza del rinnovo al 18/09/2031
        Regime fiscale Cedolare secca, scelta dalla parte locatrice
        Pag. 1
        """,
        """
        Accordo e parametri
        Parametro Valore Riferimento
        Zona urbana omogenea Zona 1 del Comune di Bollate, corrispondente alla zona OMI B1 aggiornata al secondo semestre 2024 art. 4.1.a) dell'Accordo
        Superficie convenzionale mq 66,00, pari alla superficie catastale art. 4.1.c) dell'Accordo; D.P.R. 138/98
        Elementi oggettivi presenti 25 punti (elementi 1, 2, 3, 5, 6, 8, 10, 11, 13, 15, 16, 17, 21, 22, 23)
        Sub-fascia Sub-fascia 3 — soglia di almeno 22 punti, superata di 3 punti
        Valori di riferimento della sub-fascia 3 da EUR 9,00 a EUR 11,00 al mq per mese
        Superficie convenzionale
        Componente Superficie Criterio Superficie computata
        Superficie convenzionale mq 66,00
        Pag. 2
        """,
        """
        Determinazione del canone
        Procedimento applicato
        L'Accordo divide il territorio di Bollate in zone e, per ciascuna zona, fissa una fascia di valori al metro quadrato entro cui il canone deve stare. L'immobile si trova in Zona 1. All'immobile si applica quindi la sub-fascia 3, i cui valori per la Zona 1 di Bollate vanno da EUR 9,00 a EUR 11,00 al metro quadrato al mese.
        Riepilogo degli importi
        Voce Valore Applicazione e riferimento
        Valore minimo di sub-fascia EUR 9,00 al mq per mese Allegato 2 dell'Accordo
        Superficie convenzionale mq 66,00 art. 4.1.c) dell'Accordo
        Canone minimo di sub-fascia EUR 594,00 mensili — EUR 7.128,00 annui valore minimo per superficie convenzionale
        Canone massimo teorico EUR 726,00 mensili — EUR 8.712,00 annui valore massimo per superficie convenzionale
        Canone convenuto EUR 595,00 mensili — EUR 7.140,00 annui art. 4.1.d) dell'Accordo
        Canone convenuto al metro quadrato EUR 9,02 al mq per mese — EUR 108,18 al mq per anno canone convenuto per superficie convenzionale
        Deposito cauzionale EUR 1.785,00, pari a 3 mensilità art. 10 dell'Accordo; art. 3 del contratto
        Pag. 3
        """,
        """
        Voce Valore Applicazione e riferimento
        Aggiornamento ISTAT non applicato art. 4.1.d) dell'Accordo — opzione per la cedolare secca
        Esito
        Il canone convenuto di EUR 595,00 mensili (EUR 7.140,00 annui) è compreso fra il minimo di EUR 594,00 e il massimo di EUR 726,00 mensili della sub-fascia 3 per la Zona 1 del Comune di Bollate. Il canone è pertanto conforme all'Accordo locale per i Comuni dell'ambito di Garbagnate Milanese del 20/05/2025.
        Sottoscrizioni
        Le parti dichiarano che i valori e le caratteristiche sopra riportati corrispondono all'effettivo stato dei luoghi. Il calcolo del canone è stato effettuato sulla base dei dati dalle stesse forniti.
        Luogo e data Bollate 19-9-26
        La Parte Locatrice La Parte Conduttrice
        Confproprietà — Confederazione della Proprietà Edilizia FEDERCASA.CONFSAL Organizzazione sindacale dei conduttori attestante Il legale rappresentante pro tempore Timbro e firma
        Pag. 4
        """,
    ];

    /// <summary>The pages joined as the text reader of the server gives them: a form feed between one page and the next.</summary>
    public static string Text() => string.Join("\n\f\n", Pages());

    /// <summary>
    /// The text of the receipt of the registration of the Agenzia delle Entrate (modello RLI12), as the PDF carries it: the three pages in one
    /// text. Invented code, number, office and amounts; the wording and the order of the lines are those of the real receipt.
    /// </summary>
    public static string Receipt(bool discarded = false) => discarded
        ? """
          SERVIZIO TELEMATICO ENTRATEL DI PRESENTAZIONE DELLE DICHIARAZIONI
          COMUNICAZIONE DI AVVENUTO RICEVIMENTO (art. 3, comma 10, D.P.R. 322/1998)
          COMUNICAZIONE DI AVVENUTO RICEVIMENTO RICHIESTA DI REGISTRAZIONE CONTRATTI DI LOCAZIONE E AFFITTO DI IMMOBILI (mod.RLI12)
          In data 21/09/2026 alle ore 10:15:20 il sistema informativo dell'Agenzia delle Entrate ha acquisito con protocollo 26092112345678901 il file
          contenente 1 contratto di locazione, di cui
          registrati: 0
          scartati: 1
          Si rilascia la presente ricevuta ai sensi dell'art.22, comma 4, del Decreto Dirigenziale 31 luglio 1998 e successive modificazioni.
          Li, 21/09/2026
          ELENCO DEI DOCUMENTI ACQUISITI E/O SCARTATI
          TIPO DI DOCUMENTO : Registrazione dei contratti di locazione e di affitto
          DOCUMENTI ACQUISITI : 0
          DOCUMENTI SCARTATI : 1
          Esito Protocollo documenti Codice fiscale Denominazione
          scartato 26092112345678901 ----------- ESEMPIO
          """
        : """
          SERVIZIO TELEMATICO ENTRATEL DI PRESENTAZIONE DELLE DICHIARAZIONI
          COMUNICAZIONE DI AVVENUTO RICEVIMENTO (art. 3, comma 10, D.P.R. 322/1998)
          COMUNICAZIONE DI AVVENUTO RICEVIMENTO RICHIESTA DI REGISTRAZIONE CONTRATTI DI LOCAZIONE E AFFITTO DI IMMOBILI (mod.RLI12)
          In data 21/09/2026 alle ore 10:15:20 il sistema informativo dell'Agenzia delle Entrate ha acquisito con protocollo 26092112345678901 il file
          contenente 1 contratto di locazione, di cui
          registrati: 1
          scartati: 0
          Il file e' stato trasmesso da:
          codice fiscale : 80123450583
          denominazione : CONFEDERAZIONE DELLA PROPRIETA' EDILIZIA
          Si rilascia la presente ricevuta ai sensi dell'art.22, comma 4, del Decreto Dirigenziale 31 luglio 1998 e successive modificazioni.
          Li, 21/09/2026
          RICEVUTA DI AVVENUTA REGISTRAZIONE CONTRATTI DI LOCAZIONE (mod.RLI12)
          Ufficio territoriale di MILANO 1
          Codice Identificativo del contratto TKM26T012345000AB
          In data 21/09/2026 il sistema informativo dell'Agenzia delle Entrate ha acquisito con protocollo 26092112345678901 - 000001 la richiesta di registrazione telematica del contratto di locazione presentata da ESEMPIO e trasmessa da CONFEDERAZIONE DELLA PROPRIETA' EDILIZIA
          Il contratto e' stato registrato il 21/09/2026 al n. 012345 serie 3T e codice identificativo TKM26T012345000AB
          DATI DEL CONTRATTO (importi in euro)
          Identificativo assegnato dal richiedente : 1234567
          Durata dal 19/09/2026 al 18/09/2029 Data di stipula 19/09/2026
          Importo del canone 7.140,00 n.fogli 6 n.copie 1
          Tipologia: Locazione agevolata immobile uso abitativo
          E' presente un file allegato.
          SOGGETTI DESTINATARI DEGLI EFFETTI GIURIDICI DEL CONTRATTO
          N. CODICE FISCALE RUOLO N. CODICE FISCALE RUOLO
          001 RSSMRA80A01H501U A 001 VRDGLI85B42F205E B
          (A) locatore / (B) conduttore
          Contratto esente da imposta di bollo e registro.
          Risulta contestualmente esercitata l'opzione/revoca per la cedolare secca da almeno uno dei locatori.
          DATI DEGLI IMMOBILI (importi in euro)
          -N.progr. 001 Categoria cat. A3 Rendita cat. 350,00
          Tipologia immobile IMMOBILE Tipo catasto URBANO
          Ubicato nel comune di BOLLATE Prov. MI
          VIA VERDI N. 12
          Li, 21/09/2026
          ELENCO DEI DOCUMENTI ACQUISITI E/O SCARTATI
          PROTOCOLLO DI RICEZIONE : 26092112345678901
          TIPO DI DOCUMENTO : Registrazione dei contratti di locazione e di affitto
          DOCUMENTI ACQUISITI : 1
          DOCUMENTI SCARTATI : 0
          Esito Protocollo documenti Codice fiscale Denominazione
          acquisito 26092112345678901 ----------- ESEMPIO
          """;
}
