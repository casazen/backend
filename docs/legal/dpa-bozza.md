<!-- CO-15/D14 — PO 2026-10-08 — Bozza operativa agente AI, richiede revisione legale -->

# ⚠️ BOZZA OPERATIVA – DPA (Accordo sul Trattamento dei Dati Personali)

> **Non è un parere legale. Sostituire con i testi definitivi del legale prima della pubblicazione.**
>
> Redatta da un agente AI su incarico del PO (CO-15/D14, 2026-10-08). Il testo completo, in formato HTML con
> segnaposto di configurazione, si trova in
> `Casazen.Web/LegalDocuments/dpa/2026-10-v1.it.html` (italiano, testo di riferimento) e
> `2026-10-v1.en.html` (inglese). Per la struttura del sistema di pubblicazione e le istruzioni di attivazione,
> vedere `docs/runbooks/legal-documents.md`.

---

## Struttura del documento (16 sezioni, GDPR art. 28)

| § | Contenuto |
|---|---|
| 1 | Parti, ruoli e ambito: Cliente = Titolare, CasaZen = Responsabile (art. 28); se il Cliente agisce per conto di un altro titolare, CasaZen è sub-responsabile (art. 28(2)(4)) |
| 2 | Oggetto e durata: fornitura del Servizio; durata del contratto + restituzione/cancellazione dati |
| 3 | Natura e finalità del trattamento: raccolta, conservazione, elaborazione, comunicazione, cancellazione e anonimizzazione per conto del Titolare |
| 4 | Tipi di dati personali: anagrafici, documento di identità, soggiorno, pagamenti, consensi, dati di check-in, parti dei contratti di locazione |
| 5 | Categorie di interessati: ospiti, accompagnatori, conduttori e altri soggetti i cui dati il Titolare inserisce nel Servizio |
| 6 | Istruzioni documentate: Termini di Servizio e configurazione del Titolare; CasaZen notifica se un'istruzione viola il GDPR |
| 7 | Riservatezza e sicurezza: misure tecniche e organizzative elencate (solo quelle realmente implementate nel codice — verificare checklist in `docs/runbooks/legal-documents.md` § 5.6) |
| 8 | Sub-responsabili: autorizzazione generale con preavviso configurabile (`Legal:Dpa:SubprocessorNoticeDays`, proposta: 30 gg); elenco dinamico `/legale/sub-responsabili`; stesse obbligazioni (art. 28(4)) |
| 9 | Trasferimenti extra-SEE: come da lista sub-responsabili; nessun trasferimento a paesi privi di adeguatezza senza meccanismo ex artt. 46–49 GDPR |
| 10 | Assistenza ai diritti degli interessati: CasaZen mette a disposizione `/api/gdpr/*` (export, cancellazione, anonimizzazione) e `/api/leases/{id}/erasure-request`; descrive cosa rimane dopo un'erasure |
| 11 | Assistenza artt. 32–36 GDPR: supporto a DPIA, valutazioni di rischio, misure di sicurezza |
| 12 | Violazione dei dati: CasaZen notifica il Titolare entro il termine configurabile (`Legal:Dpa:BreachNotificationHours`, proposta: 48 ore) dal momento di conoscenza, come da art. 33(2) GDPR |
| 13 | Conservazione e cancellazione: periodi applicati dal Servizio (tabella identica alla sezione 4.3 dell'Informativa Privacy, da `Gdpr:Retention`); restituzione o cancellazione entro `Legal:Terms:DataReturnDays` giorni dal recesso |
| 14 | Audit: diritto del Titolare a effettuare o commissionare audit, con preavviso di `Legal:Dpa:AuditNoticeDays` giorni (proposta: 30 gg); una volta l'anno salvo violazione |
| 15 | Responsabilità e indennizzo: come da Termini di Servizio, con riserva delle ipotesi di cui all'art. 82 GDPR |
| 16 | Disposizioni finali: aggiornamenti del DPA, legge applicabile, foro (`Legal:Terms:GoverningCourt`) |

---

## Periodi di conservazione nel DPA (CO-15/D14, 2026-10-08)

Identici alla sezione 4.3 dell'Informativa Privacy; applicati dal job notturno `GdprDataRetentionJob`.

| Categoria | Periodo | Fonte |
|---|---|---|
| Copie documenti di identità | **5 anni** dall'ultimo check-out | PO CO-15/D14; art. 5.1.e GDPR; art. 109 TULPS |
| Dati Alloggiati Web e accompagnatori | **5 anni** dal check-out del soggiorno | PO CO-15/D14; art. 5.1.e GDPR; art. 109 TULPS |
| Log di sicurezza, analytics, cookie | **12 mesi** | PO CO-15/D14; art. 5.1.e GDPR; Garante provv. cookie 10.06.2021 |
| Dati fiscali e contabili (prenotazioni, pagamenti, fatture) | **10 anni** dalla fine del rapporto o dalla data del documento | PO CO-15/D14; art. 22 D.P.R. 600/1973; art. 2220 c.c. |
| Dati anagrafici parti dei contratti di locazione | **10 anni** dalla data di fine del contratto | PO CO-15/D14; art. 2220 c.c. |

---

## Sub-responsabili e trasferimenti extra-SEE

L'elenco è generato dinamicamente dalla configurazione in uso (non scritto nel testo). Vedi:
- API: `GET /api/legal/subprocessors`
- Pagina pubblica: `/legale/sub-responsabili`
- Runbook: `docs/runbooks/legal-documents.md` § 3

**DeepSeek (AI provider):** nessun meccanismo di trasferimento GDPR dichiarato — `TransferMechanism` lasciato vuoto su decisione del PO in attesa di parere legale. **Non attivare `Ai:Provider=DeepSeek` in produzione senza parere legale.**

---

## Valori configurabili principali del DPA

| Chiave configurazione | Valore proposto | Note |
|---|---|---|
| `Legal:Dpa:BreachNotificationHours` | 48 | Target di notifica violazione al Titolare (art. 33(2) GDPR) — confermare con legale |
| `Legal:Dpa:SubprocessorNoticeDays` | 30 | Preavviso nuovo sub-responsabile (art. 28(2)) — confermare con legale |
| `Legal:Dpa:AuditNoticeDays` | 30 | Preavviso audit — confermare con legale |
| `Legal:Terms:DataReturnDays` | 30 | Giorni per restituzione/cancellazione dati post-recesso — confermare con legale |

---

## Punti aperti per il legale

- **Preavviso sub-responsabili** (§ 8.3): la notifica avviene via email o in-app a ogni host; la pagina `/legale/sub-responsabili` non costituisce notifica. Nessun invio automatico attualmente implementato.
- **Audit** (§ 14): frequenza e modalità (accesso ai log, questionari, audit fisico) — proposta una volta all'anno.
- **Registro dei trattamenti** (art. 30(2)): obbligo organizzativo di CasaZen, non implementato nel prodotto.
- **Procedure data breach** (§ 12): l'invio della notifica al Titolare è un processo manuale; valutare strumenti di supporto.
- **Impegni di riservatezza del personale** (§ 6.1): obbligo organizzativo, non implementato nel prodotto.

---

*Versione bozza: `2026-10-v1` — PO 2026-10-08. Attivazione: vedere `docs/runbooks/legal-documents.md` § 5.3.*
