# Business Analysis — Funzionalità "Canone Concordato Assistito" (pilota Seveso / Cesano Maderno)

> Preparato per il team prodotto CasaZen sulla base della ricerca normativa in [`research-canone-concordato-mb.md`](./research-canone-concordato-mb.md), incrociata con lo stato attuale del codebase (`Sessions/specs/spec-ltr-rli-registration.md`, `spec-ltr-frontend.md`, `.claude/context/regulations/fiscale.md`, `Sessions/PLANNING.md`, `.claude/rules/compliance.md`). Vedi anche la spec tecnica proposta: [`Sessions/specs/spec-ltr-canone-concordato-calculator.md`](./specs/spec-ltr-canone-concordato-calculator.md).

---

## 1. Executive Summary

Il canone concordato (L. 431/1998 art. 2 c.3) è un'opportunità di prodotto concreta per la linea Long-Term Rent di CasaZen: un regime che offre vantaggi fiscali reali (IMU −25% ovunque in Italia; cedolare secca al 10% e IRPEF/registro ridotti nei comuni "ad alta tensione abitativa" — Seveso e Cesano Maderno risultano entrambi inclusi, con riserva di verifica formale) ma che oggi richiede al proprietario di orientarsi tra normativa nazionale, accordo territoriale locale, associazioni di categoria, portale RLI dell'Agenzia delle Entrate e comunicazione IMU comunale **separata** — quattro canali burocratici non integrati tra loro. È lo stesso tipo di complessità che CasaZen già monetizza sul lato affitti brevi (CIN, Alloggiati Web, imposta di soggiorno).

Il codebase ha **già** una spec dedicata (`spec-ltr-rli-registration.md`) che modella `CanoneConcordato` come valore dell'enum `FiscalRegime` e integra Openapi.it come canale di filing — con un impianto di guardrail legali (delega per-filing, ToS, template counsel-reviewed, disclaimer non vincolante) nato da un **rescope esplicito da "automatico" ad "assistito"** dopo revisione legale (Legal C4). Questo precedente interno convalida esattamente la cautela richiesta: qualunque nuova funzionalità di calcolo/generazione contratto/avvio RLI per il canone concordato deve incastrarsi in questo stesso framework "assistito, mai automatico", non introdurne uno nuovo.

**Raccomandazione sintetica** (dettagli §8): **procedere**, ma in fasi separate — (a) calcolatore di idoneità/vantaggio fiscale con disclaimer non vincolante, (b) generazione contratto su template counsel-reviewed, (c) innesto nel flusso RLI assistito già speccato — con priorità allo sblocco del sign-off legale che oggi condiziona l'intera GA di LTR, non solo questa feature. **Nota di governance**: l'intera fase "1.5 — LTR" risulta **frozen** nel registro spec (`Sessions/specs/README.md`, issue #269 "closed — do not resume"); questo documento e la spec associata sono materiale preparatorio, non un'autorizzazione a riprendere lo sviluppo.

---

## 2. Sintesi normativa consolidata

### 2.1 Quadro nazionale

| Elemento | Contenuto | Base normativa |
|---|---|---|
| Possibilità di stipulare canone concordato | Estesa a **tutti** i Comuni italiani dal 30/3/2017 | D.M. 16/1/2017 |
| Tipologie contrattuali | Ordinario "3+2"; Transitorio (1–18 mesi); Studenti universitari (6 mesi–3 anni) | D.M. 16/1/2017, artt. 1–3 |
| Cedolare secca ridotta | **10%**, confermata invariata dalla Legge di Bilancio 2026 (L. 199/2025) | D.Lgs. 23/2011 → L. 160/2019 |
| Riduzione IRPEF (regime ordinario) | −30% sulla base imponibile | L. 431/1998 art. 8 c.1 |
| Riduzione imposta di registro | Base al 70% del canone, aliquota 2% | L. 431/1998 art. 8 c.1 |
| Riduzione IMU | −25%, **universale, nessun vincolo territoriale** | L. 160/2019 art. 1 c.760 |
| Attestazione di conformità | Obbligatoria per contratti non assistiti; rilasciata da almeno un'organizzazione firmataria | D.M. 16/1/2017 art. 1 c.8 |

**Nodo critico**: la possibilità di *stipulare* un contratto a canone concordato è oggi universale, ma **tre dei quattro vantaggi fiscali (cedolare 10%, IRPEF −30%, registro −30%) restano condizionati** all'appartenenza del Comune all'elenco "ad alta tensione abitativa" (delibera CIPE 13/11/2003, mai formalmente aggiornata nonostante l'obbligo biennale). **Solo l'IMU −25% è nazionale e universale.** Un calcolatore CasaZen che non distingua i due piani rischia di comunicare vantaggi non spettanti.

### 2.2 Accordo Territoriale — Provincia di Monza e Brianza

Sottoscritto a Monza il 15/03/2024, in vigore dal 01/05/2024, copre 55 comuni inclusi Seveso e Cesano Maderno. Firmatari: A.P.E. Monza/Confedilizia, **A.S.P.P.I. Comprensorio Brianza** (sede a Seveso), Confabitare, Confappi, Federproprietà, U.P.P.I., Unioncasa (proprietà); CONIA, SICET, SUNIA, UNIAT (inquilini). Dettaglio completo in `research-canone-concordato-mb.md` §2.

### 2.3 Seveso e Cesano Maderno a confronto

| Elemento | Seveso | Cesano Maderno |
|---|---|---|
| Classificazione zone | 1 zona unica | 2 zone (Centrale / Semi periferica) per foglio catastale |
| Aliquota IMU canone concordato | **5,7‰** (base comunale 7,6‰ già scontata, poi −75% di legge) | Nessuna riga dedicata; **≈0,78%** derivato da "Altri fabbricati" (1,04%) × 75% |
| Comunicazione IMU | Modulo email/PEC a Ufficio Tributi, non retroattivo | Copia contratto controfirmata da un'organizzazione sindacale, a U.O. Risorse Tributarie |
| Delibera aliquote | CC n. 43 del 25/11/2025 (2026 = 2025) | CC n. 132 del 19/12/2024 (2025; 2026 non ancora deliberato, normale) |

**Punto comune rilevante**: in **entrambi** i Comuni, la registrazione RLI con opzione cedolare secca **non fa scattare automaticamente** lo sgravio IMU. Sono due adempimenti separati (Stato vs. Comune), basi giuridiche diverse, canali diversi: qualsiasi funzionalità CasaZen deve trattarli come **due step distinti e tracciati separatamente**, non un unico "invio".

---

## 3. Processo end-to-end (proposto)

1. **Input host**: indirizzo/comune, mq, dotazioni (elementi A/B/C/D), ammobiliato sì/no, durata → CasaZen verifica se esiste un accordo territoriale attivo per quel comune.
2. **Se accordo trovato**: calcolo zona/fascia/sub-fascia + coefficienti → range di canone min/max (€/anno) + stima vantaggi fiscali, distinguendo esplicitamente IMU −25% (sempre) da cedolare 10%/IRPEF−30%/registro−30% (solo se comune in lista alta tensione abitativa) — disclaimer "informativa, non consulenza fiscale" in ogni schermata.
3. **Se accordo non trovato o dato incompleto**: messaggio esplicito "dato non disponibile per questo comune", mai una stima indovinata come fallback silenzioso.
4. **Conferma host**: canone scelto nel range → generazione bozza contratto da template counsel-reviewed per `FiscalRegime = CanoneConcordato` (pattern già in `spec-ltr-rli-registration.md` AC3).
5. **Se contratto "non assistito"**: CasaZen segnala la necessità dell'attestazione di conformità e indirizza verso un'associazione firmataria locale (es. ASPPI Comprensorio Brianza). **CasaZen non rilascia mai l'attestazione.**
6. **Firma delle parti** — CasaZen traccia lo stato, non sostituisce il consenso negoziale.
7. **Pre-fill RLI**: pre-compilazione quadri A/B/C/D con checklist a scadenza 30 giorni. **Nessun invio automatico.**
8. **Autorizzazione esplicita (delega)**: solo dopo azione esplicita e per-filing dell'host, invio tramite canale terzo (Openapi.it, portale RLI, o commercialista/CAF). **CasaZen è facilitatore software, mai intermediario abilitato.**
9. **Tracciamento esito**: ricevuta di registrazione + stato opzione cedolare secca salvati sul contratto.
10. **Comunicazione IMU al Comune (step separato, non automatico)**: CasaZen fornisce moduli/dati pre-compilati specifici del comune; l'invio resta a cura dell'host.
11. **Promemoria ricorrenti**: proroghe, comunicazioni successive, eventuale revoca cedolare secca.
12. **Cockpit host**: stato consolidato — contratto registrato, opzione fiscale attiva, sgravio IMU comunicato o pendente, prossima scadenza.

---

## 4. Opportunità di business

**Perché ha senso**: un proprietario a Seveso o Cesano Maderno che non sa di poter accedere a IMU −25% (sempre) e potenzialmente cedolare 10%/IRPEF−30%/registro−30% lascia denaro sul tavolo ogni anno, dovendo oggi navigare **quattro canali scollegati**. Stesso pattern già monetizzato lato affitti brevi: normativa italiana frammentata → wizard guidato → riduzione dell'attrito.

**Target utenti**: proprietari long-term rent già nel perimetro `spec-ltr-*` — in particolare chi ha 1-2 immobili in comuni ad alta tensione abitativa e oggi si affida interamente a un commercialista/associazione, o chi sceglie il libero mercato per pigrizia burocratica pur potendo beneficiare del canone concordato.

**Valore percepito**: time-to-compliance ridotto; riduzione del rischio d'errore (i requisiti cumulativi sono complessi; un errore fa decadere retroattivamente le agevolazioni con sanzioni dal 90% al 180%); trasparenza sul vantaggio economico, oggi invisibile alla maggior parte dei proprietari.

### Rischi

| Rischio | Descrizione | Mitigazione |
|---|---|---|
| **Intermediario abilitato non autorizzato** (primario) | Il DPR 322/1998 riserva la trasmissione telematica per conto terzi a soggetti abilitati | Delega esplicita per-filing, ToS che attribuisce la responsabilità al locatore, canale terzo abilitato (Openapi.it) come effettivo trasmittente — CasaZen sempre "software facilitator" |
| **Responsabilità su guidance fiscale errata** | Danno concreto per l'host se l'idoneità comunicata risulta errata (sanzioni retroattive) | Disclaimer "informativa, non consulenza fiscale" su ogni output; richiedere sempre l'attestazione come base per l'idoneità effettiva |
| **Obsolescenza dei dati locali** | Accordi territoriali e liste comunali cambiano (lista CIPE non aggiornata dal 2003) | Processo di manutenzione dati esplicito, con data di "ultima verifica" visibile per ogni comune |
| **Scalabilità geografica** | Il dettaglio per 2 comuni ha richiesto ricerca multi-fase con gap espliciti | Espandersi **dentro** l'accordo MB (55 comuni nello stesso PDF sorgente) costa molto meno che aprire un nuovo accordo territoriale altrove |
| **Generazione contratto non conforme** | Canone fuori fascia o clausole non conformi fa decadere le agevolazioni | Template versionati e counsel-reviewed per regime fiscale; nessun regime senza template approvato può generare un contratto |
| **Sgravio IMU non ottenuto per percezione di "automatico"** | Se il prodotto lascia intendere automazione totale, l'host può non completare la comunicazione IMU | UX esplicita che tratta l'IMU come step distinto e tracciato separatamente dalla registrazione RLI |

---

## 5. Enti terzi raccomandati

**(i) Servizi di invio RLI/F24 come intermediari abilitati**

| Fornitore | Stato | Note |
|---|---|---|
| **Openapi.it / Docuengine** | **Già integrato (stub) nel codebase** | API B2B, 12,90 €+IVA a registrazione, 36h, supporta canone concordato e cedolare secca. `OpenapiLeaseRegistrationProvider` esiste già come stub, in attesa di sign-off legale — non è una raccomandazione da validare da zero |
| LocazioniWeb, VisuraUtile.it, ServizioTelematico.com | Trovati in ricerca | Portali "a servizio" (form web), non API-first — meno adatti a integrazione backend; ServizioTelematico.com include un calcolo canone competitivo |

**(ii) Fornitori di firma elettronica** — non emersi in questa ricerca (focalizzata su RLI). Il codebase ha già un endpoint `/webhooks/esign` e un flusso di firma per LTR — verificare con il team tech quale provider sia già cablato prima di proporne uno nuovo. Raccomandazioni di mercato generiche (non dal dossier): Namirial, InfoCert, Aruba, DocuSign, Yousign.

**(iii) Associazioni di categoria per l'attestazione di conformità** — trovate concretamente, con contatti reali: **A.S.P.P.I. Comprensorio Brianza** (sede a Seveso), Confabitare Monza Brianza, SUNIA-CGIL MB, SICET-CISL Monza. Una partnership/referral con ASPPI Comprensorio Brianza è un'opportunità concreta per il pilota, non una congettura.

**(iv) Commercialista-as-a-service / CAF digitali** — non trovati nel dossier (solo un range di costo generico). Raccomandazione di mercato da validare: Fiscozen (copertura per canone concordato da verificare) o CAF collegati alle stesse sigle sindacali già firmatarie dell'accordo MB (CAF CGIL, CAF ACLI).

---

## 6. Gap e rischi aperti

- **[COUNSEL_REQUIRED]** Conferma che CasaZen come "software facilitator" (pre-fill RLI + instradamento via Openapi.it su delega esplicita) non integri intermediazione abilitata ai sensi del DPR 322/1998 — già aperto in `spec-ltr-rli-registration.md`, bloccante per la GA legale di tutta LTR.
- **[COUNSEL_REQUIRED]** Verifica formale che Seveso e Cesano Maderno siano nell'elenco ufficiale Agenzia Entrate "alta tensione abitativa" (distinto dalla lista "alta densità abitativa" dell'accordo locale) — conferma oggi basata su fonti secondarie, non sul testo primario della Delibera CIPE 87/2003.
- **[COUNSEL_REQUIRED]** Validità del calcolo IMU derivato per Cesano Maderno (≈0,78%): non un'aliquota pubblicata separatamente — non presentare come "aliquota ufficiale" senza dicitura equivalente.
- **[COUNSEL_REQUIRED]** Numero di organizzazioni firmatarie richieste per l'attestazione in ambito MB (norma nazionale = "almeno una"; prassi locale descritta come bilaterale) — verificare prima di codificare la regola in prodotto.
- **Dati non reperiti**: eventuale rinnovo 2025/2026 dell'accordo MB; delibera di Giunta di Seveso che recepisce l'accordo; planimetrie grafiche delle zone; tariffa ufficiale dell'attestazione in MB; tasso di interesse legale 2026 per ravvedimento; contenuto del presunto "DM 22/06/2026". Dettaglio completo in `research-canone-concordato-mb.md` §11.
- **Gap operativo**: nessun portale regionale Lombardia unifica i quattro adempimenti (attestazione, RLI, IMU, eventuale cessione fabbricato) — la frammentazione che CasaZen vuole risolvere è reale, non semplificabile appoggiandosi a un'infrastruttura pubblica esistente.

---

## 7. Raccomandazione finale

**Procedere**, con priorità e sequenza chiare, non come rilascio monolitico:

1. **Priorità 0 — Sblocco legale trasversale**: il framework "assistito, mai automatico" per l'RLI è già disegnato in `spec-ltr-rli-registration.md` ma bloccato da più `[COUNSEL_REQUIRED]` che condizionano l'intera GA di LTR. Ottenere il sign-off legale su delega/ruolo di Openapi.it/template counsel-reviewed sblocca contemporaneamente questa feature e altre già in pipeline.
2. **Priorità 1 — Calcolatore di idoneità/vantaggio**: nuovo layer dati (accordo territoriale → comune → zona → fascia → sub-fascia → coefficienti) con disclaimer non vincolante, riusando il pattern previsto per `ICedolareAdvisoryService`. Pilota su Seveso e Cesano Maderno, ma chiudere i gap `[COUNSEL_REQUIRED]` sull'idoneità ATA prima di ogni annuncio pubblico.
3. **Priorità 2 — Generazione contratto**: riusare `LeaseContractTemplateService` e il pattern di template versionati per `FiscalRegime`. Nessun rilascio in produzione senza template approvato da un legale.
4. **Priorità 3 — Innesto nel flusso RLI assistito**: gran parte dell'infrastruttura (pre-fill, checklist 30 giorni, delega, reminder) è già speccata; il lavoro incrementale è collegare calcolo/contratto al flusso esistente, non costruirne uno nuovo.
5. **Scalabilità**: non investire in copertura nazionale prima di validare il pilota su MB — espandersi dentro l'accordo MB (55 comuni nello stesso PDF sorgente, di cui solo 2 dettagliati qui) costa molto meno di un nuovo accordo territoriale altrove.
6. **Non fare**: non presentare mai la funzionalità come "invio automatico" delle pratiche fiscali (RLI o IMU) — sia per il rischio di intermediazione abilitata non autorizzata, sia perché entrambi i comuni richiedono comunque un passo di comunicazione manuale che CasaZen può assistere ma non eliminare.
