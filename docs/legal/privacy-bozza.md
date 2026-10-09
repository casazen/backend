<!-- CO-15/D14 — PO 2026-10-08 — Bozza operativa agente AI, richiede revisione legale -->

# ⚠️ BOZZA OPERATIVA – Informativa Privacy

> **Non è un parere legale. Sostituire con i testi definitivi del legale prima della pubblicazione.**
>
> Redatta da un agente AI su incarico del PO (CO-15/D14, 2026-10-08). Il testo completo, in formato HTML con
> segnaposto di configurazione, si trova in
> `Casazen.Web/LegalDocuments/privacy/2026-10-v1.it.html` (italiano, testo di riferimento) e
> `2026-10-v1.en.html` (inglese). Per la struttura del sistema di pubblicazione e le istruzioni di attivazione,
> vedere `docs/runbooks/legal-documents.md`.

---

## Struttura del documento (13 sezioni, GDPR artt. 13 e 14)

| § | Contenuto |
|---|---|
| 1 | Titolare del trattamento e contatti (ragione sociale, sede, P.IVA, email privacy, PEC; DPO se designato) |
| 2 | Ruoli: CasaZen è titolare per i dati di account, abbonamento e sicurezza; responsabile per i dati degli ospiti e dei conduttori del Cliente |
| 3 | Dati trattati come titolare: account e identità, organizzazione e dati fiscali, abbonamento e pagamenti, profilo Fornitore, accettazioni e consensi, attribuzione iscrizioni, dati tecnici e di sicurezza, comunicazioni |
| 4 | Dati degli ospiti/conduttori (CasaZen come responsabile): finalità e basi giuridiche decise dal Cliente; periodi di conservazione applicati dal Servizio (tabella, sezione 4.3) |
| 5 | Finalità, basi giuridiche e obbligatorietà del conferimento (tabella) |
| 6 | Destinatari: sub-responsabili (rimanda alla lista dinamica `/legale/sub-responsabili`), categorie di soggetti autorizzati |
| 7 | Trasferimenti extra-SEE: rimanda alla lista dinamica dei sub-responsabili con meccanismo di trasferimento |
| 8 | Conservazione: periodi per ciascuna categoria dei dati di titolari (tabella); per i dati di account inattivi, rimando alle condizioni del Contratto |
| 9 | Diritti degli interessati (artt. 15–22 GDPR): accesso, rettifica, cancellazione, limitazione, portabilità, opposizione, reclamo al Garante; riferimento agli strumenti `/api/gdpr/*` |
| 10 | Decisioni automatizzate: nessuna decisione basata esclusivamente su trattamento automatizzato che produca effetti giuridici |
| 11 | Cookie e archiviazione locale: nessun cookie dall'API; il sito di prenotazione usa `localStorage` per l'attribuzione delle iscrizioni |
| 12 | Modifiche all'informativa |
| 13 | Versione e data di entrata in vigore |

---

## Periodi di conservazione configurati (CO-15/D14, 2026-10-08)

Questi periodi sono stati decisi dal PO e sono attivi in produzione tramite `Gdpr:Retention` in `appsettings.json`.
Il job notturno `GdprDataRetentionJob` li applica; se un periodo non ha `Source`, non viene applicato.

| Categoria (`Gdpr:Retention:*`) | Periodo | Fonte della decisione |
|---|---|---|
| `DocumentScans` — copie dei documenti di identità caricati | **5 anni** dall'ultimo check-out dell'ospite | PO decision CO-15/D14, 2026-10-08; art. 5.1.e GDPR; art. 109 TULPS |
| `AlloggiatiData` — dati Alloggiati Web (nascita, cittadinanza, sesso, documento) e accompagnatori | **5 anni** dal check-out del soggiorno | PO decision CO-15/D14, 2026-10-08; art. 5.1.e GDPR; art. 109 TULPS |
| `Marketing` — log di sicurezza, dati analytics e dati cookie | **12 mesi** | PO decision CO-15/D14, 2026-10-08; art. 5.1.e GDPR; Garante provv. cookie 10.06.2021 |
| `FiscalData` — dati fiscali e contabili (account, prenotazioni, pagamenti, fatture, report) | **10 anni** dalla fine del rapporto o dalla data del documento | PO decision CO-15/D14, 2026-10-08; art. 22 D.P.R. 600/1973; art. 2220 c.c. |
| `LeaseParties` — dati anagrafici delle parti di contratti di locazione a lungo termine | **10 anni** dalla data di fine del contratto | PO decision CO-15/D14, 2026-10-08; art. 2220 c.c. |

**Lista ospiti (prenotante):** fino a cancellazione da parte dell'host; se l'account è chiuso e il record è collegato a una prenotazione, si applicano i periodi fiscali (10 anni). Questa logica è gestita dall'operatore, non automaticamente dal job.

---

## Punti aperti per il legale

- Confermare i periodi di conservazione (soprattutto `Marketing`: il job usa questa categoria per log di sicurezza e analytics, non solo per il consenso marketing).
- Verificare se serve il DPO e fornire il contatto (`Legal:Controller:DpoEmail`).
- Compilare i dati del titolare: `Legal:Controller:Name`, `Address`, `VatId`, `Pec`, `PrivacyEmail`, `GoverningCourt`.
- Confermare la base giuridica per l'attribuzione delle iscrizioni (archiviazione locale, UTM).
- Decisione su DeepSeek: nessun meccanismo di trasferimento GDPR dichiarato — non attivare `Ai:Provider=DeepSeek` in produzione senza parere legale.

---

*Versione bozza: `2026-10-v1` — PO 2026-10-08. Attivazione: vedere `docs/runbooks/legal-documents.md` § 5.3.*
