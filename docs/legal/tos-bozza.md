<!-- CO-15/D14 — PO 2026-10-08 — Bozza operativa agente AI, richiede revisione legale -->

# ⚠️ BOZZA OPERATIVA – Termini di Servizio

> **Non è un parere legale. Sostituire con i testi definitivi del legale prima della pubblicazione.**
>
> Redatta da un agente AI su incarico del PO (CO-15/D14, 2026-10-08). Il testo completo, in formato HTML con
> segnaposto di configurazione, si trova in
> `Casazen.Web/LegalDocuments/tos/2026-10-v1.it.html` (italiano, testo di riferimento) e
> `2026-10-v1.en.html` (inglese). Per la struttura del sistema di pubblicazione e le istruzioni di attivazione,
> vedere `docs/runbooks/legal-documents.md`.

---

## Struttura del documento (17 sezioni)

| § | Contenuto |
|---|---|
| 1 | Chi siamo, a chi si rivolge il Servizio e quali documenti si applicano |
| 2 | Il Servizio: funzionalità, modifiche, obblighi dell'utente |
| 3 | Piani e abbonamento: tabella piani (da configurazione), ciclo di fatturazione, grazia per mancato rinnovo |
| 4 | Pagamenti degli Ospiti: addebiti diretti sull'account Stripe Connect del Cliente; CasaZen non detiene fondi e non applica commissioni oggi |
| 5 | Dati del Cliente: proprietà, licenza a CasaZen, obblighi di conformità, contenuti illeciti |
| 6 | Marketplace Fornitori: contratto diretto host–fornitore; visibilità del profilo e della richiesta; "pagato" è un'annotazione manuale |
| 7 | Proprietà intellettuale di CasaZen |
| 8 | Fornitori: obblighi aggiuntivi, esclusione di rapporto di lavoro |
| 9 | Sincronizzazione iCal: periodicità, rischio di doppie prenotazioni a carico del Cliente |
| 10 | Strumenti di conformità: supporto, non consulenza; formati CIN verificati ma non garantiti; upload Alloggiati preparato per caricamento manuale; RLI depositato dal locatore |
| 11 | AI: abbozza solo testi pubblici e informativi; nessun dato personale; soggetto a revisione |
| 12 | Sospensione dell'immobile per mancata conformità |
| 13 | Durata, recesso, sospensione del Cliente; termini di preavviso; finestra di restituzione dati |
| 14 | Limitazioni di responsabilità: esclusioni, massimale (canoni degli ultimi N mesi), forza maggiore |
| 15 | Modifiche ai Termini: preavviso configurabile (proposta: 30 giorni, min. 15 ex Reg. 2019/1150) |
| 16 | Legge applicabile e foro competente (da configurare: `Legal:Terms:GoverningCourt`) |
| 17 | Clausole finali: contratto completo, nullità parziale, rinuncia, clausole onerose (artt. 1341–1342 c.c.) |

---

## Valori configurabili principali

| Chiave configurazione | Valore proposto in `appsettings.json` | Note |
|---|---|---|
| `Legal:Controller:Name` | — (obbligatorio, nessun default) | Ragione sociale del gestore di CasaZen |
| `Legal:Controller:GoverningCourt` | — (obbligatorio, nessun default) | Foro esclusivo (es. `Milano`) |
| `Legal:Terms:ChangeNoticeDays` | 30 | Min. 15 ex Reg. (UE) 2019/1150 art. 3(2) — confermare con legale |
| `Legal:Terms:TerminationNoticeDays` | 30 | 30 giorni ex Reg. 2019/1150 art. 4 — confermare con legale |
| `Legal:Terms:LiabilityCapMonths` | 12 | Massimale proposto — confermare con legale |
| `Legal:Terms:DataReturnDays` | 30 | Finestra di restituzione dati post-recesso — confermare con legale |
| `Billing:PastDueGraceDays` | 7 | Grazia dopo mancato rinnovo |

---

## Punti aperti per il legale

- **Consumer vs. business**: il § 1.6 esclude i diritti del consumatore se applicabili (art. 3 D.Lgs. 206/2005) — valutare se un host mono-proprietà è consumatore.
- **Clausole onerose** (§ 17.6, artt. 1341–1342 c.c.): l'app registra un'unica checkbox per tutti i Termini; valutare se serve approvazione separata delle clausole onerose.
- **Piano Starter**: il § 3.2 descrive i limiti del piano; valutare se Starter è gratuito prima dell'abbonamento.
- **IVA** sui prezzi dei piani (§ 3.1) e flusso di fatturazione elettronica (SDI, stub PL-13).
- **Reg. (UE) 2019/1150 e DSA**: valutare se CasaZen è "servizio di intermediazione online" o hosting service con obblighi propri.
- **Nessuna commissione sulle transazioni** (§ 4.2): vero nel codice oggi; una futura commissione richiede preavviso ex ToS § 15.1.

---

*Versione bozza: `2026-10-v1` — PO 2026-10-08. Attivazione: vedere `docs/runbooks/legal-documents.md` § 5.3.*
