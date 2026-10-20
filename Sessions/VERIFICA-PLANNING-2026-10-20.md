> **Storico del primo triage:** il PO ha successivamente ristretto il planning al pilota Quokka. Le priorità proposte sotto non costituiscono il planning attivo; consultare PLANNING.md.

# Verifica analisi aperte e planning — 20 ottobre 2026

## Perimetro e risultato

Pull fast-forward di `origin/develop` eseguito su tutti i repository: già aggiornati, nessuna modifica locale preesistente. Baseline: backend `b16c5de2`, frontend `b9047e7`, mobile `4d39ccf`. Verifica documentale, codice a campione e stato GitHub; nessuna nuova esecuzione dei test e nessuna verifica di produzione. Le PR aperte non fanno parte del codice integrato.

## Analisi svolte ma non ancora integrate

| Voce | Evidenza | Prossima azione |
|---|---|---|
| Business plan v1.1.0, listino, modello e pitch | [backend PR #528](https://github.com/casazen/backend/pull/528), aperta | Revisione PO di prezzi, limiti e ipotesi; distinguere documento commerciale e successivo adeguamento del prodotto |
| Quokka v10 + amendment Agenzia | [backend PR #553](https://github.com/casazen/backend/pull/553), aperta | Consolidare con #528 prima di derivare task: develop contiene ancora v4 del 15 ottobre |
| Loading della shell | [frontend PR #220](https://github.com/casazen/frontend/pull/220), aperta, check del 18 ottobre verdi salvo staging saltato | Riesaminare contro develop corrente; è un fix UI, non un'analisi mancante |

Le due analisi commerciali esistono già: non rifarle. Le decisioni della v10 su verifica identità interna, accessi senza integrazione serrature, Alloggiati automatico e marketing cambiano presupposti della v4. Non sommare stime e costi delle versioni. La lettura del corpo e dell'elenco file delle PR non equivale a revisione completa dei contenuti o validazione delle fonti.

## Pulizia del backlog

- GitHub: backend 0 issue aperte; mobile 0; frontend 2 (#145 e #83). Zero issue aperte non significa zero lavoro residuo.
- `Sessions/risanamento/stato/status.json`: 166 done, 3 partial, 12 pending. I 166 includono 3 task aggiunti; il riepilogo storico parla di 163/166 per il piano originario. È uno snapshot del 2 ottobre, non lo stato corrente del prodotto.
- Le 3 partial dello snapshot sono RS-7 (tariffe ufficiali), CO-13 (Alloggiati WS), CO-22 (ISTAT/portali regionali). Restano da rivalutare contro il codice corrente prima di assegnare implementazioni.
- `DOMANDE-APERTE.md` contiene contraddizioni interne: RS-6 risulta bloccato all'inizio e risolto il 2 ottobre; il blocco GJ mai eseguito in CI è superato almeno per la variante web verificata qui. Conservare lo storico; usare questa verifica per il triage attuale.

| Issue aperta | Riscontro sul codice | Trattamento |
|---|---|---|
| [frontend #145](https://github.com/casazen/frontend/issues/145), client e azioni GDPR | Esistono `src/api/gdpr.api.ts`, tab Privacy in `src/features/guests/components/gdpr-tab.tsx`, test `__tests__/gdpr-tab.test.tsx`; presenti export, anonimizzazione, cancellazione e revoca marketing | Implementazione già presente a campione. Verificare tutti gli AC (404 cross-org, smoke E2E, invalidazione) e poi chiudere o restringere ai residui |
| [frontend #83](https://github.com/casazen/frontend/issues/83), lifecycle lease E2E | `e2e/leases.spec.ts` copre bozza, firma manuale, registrazione manuale con ricevuta; usa API mock | Riscrivere gli AC obsoleti sui provider. Verificare separatamente APE, ruoli e staging; non chiudere per la sola presenza del test |

## Evidenza CI attuale

- [Frontend E2E del 20 ottobre](https://github.com/casazen/frontend/actions/runs/39392386952): L2 demo **success**, staging **failure** al passo `API regression smoke`. La causa specifica non è stata diagnosticata: non attribuirla automaticamente al vecchio hosting.
- [Frontend Golden Journey del 20 ottobre](https://github.com/casazen/frontend/actions/runs/39390594076): **success**. Questo supera la vecchia affermazione generale che il job web non sia mai girato; non dimostra ogni variante Stripe o il funzionamento in produzione.
- [Mobile Maestro del 20 ottobre](https://github.com/casazen/mobile/actions/runs/39389809362): **failure** a `Repository tokens present` e `Ephemeral stack down`. Risolvere prima i prerequisiti del job; non attribuire questo run a un difetto dell'app né considerarlo una verifica Maestro completata.

## Planning proposto

| Ordine | Attività | Dipendenze | Criterio di completamento |
|---|---|---|---|
| P0-1 | Diagnosticare staging frontend e prerequisiti mobile | Accesso ai log/configurazione CI | Causa documentata, smoke verde, job mobile arriva ai test; nessun segreto nei report |
| P0-2 | Consolidare #528 e #553 con revisione PO | Decisioni commerciali e scope Agenzia | Un listino, un insieme di decisioni e una roadmap coerenti; ipotesi separate dai fatti |
| P1-1 | Triage finale #145 e #83 | Verifica AC mirata | Issue chiuse solo con evidenze, oppure ridotte a gap precisi |
| P1-2 | Rivalutare RS-7, CO-13, CO-22 e domande legali/fiscali | Codice aggiornato, fonti ufficiali e pareri necessari | Matrice implementato/mancante/bloccato, fonte e decisione per ogni residuo |
| P1-3 | Tradurre Quokka consolidata in pacchetti implementabili | P0-2, risposte prospect e provider | Task con dipendenze, test di accettazione e distinzione pilota/go-live; ristima delle integrazioni |

Dati ancora da raccogliere per Quokka: distribuzione delle 250 proprietà per paese/cantone/comune, CRM attuale e costo, volumi di ospiti/prenotazioni, soggetto che incassa e regime dei proprietari, staff/lingue e finestra di migrazione. Riconfermare l'utilità della domanda sulle serrature alla luce della v10. Nessun contatto esterno avviato.

## Modifiche di questa verifica

Creato questo registro e aggiunti rimandi da PLANNING e DOMANDE-APERTE. Nessuna issue chiusa, PR unita, snapshot storico riscritto o configurazione esterna modificata. Il planning è una proposta ordinata; decisioni commerciali e pareri restano da raccogliere.
