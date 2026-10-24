# CasaZen — Planning: pilota Quokka

Aggiornato il 23 ottobre 2026 su indicazione del PO.

## Focus attuale

Sviluppare il prima possibile ciò che serve al pilota Quokka e alle funzionalità Agenzia. Riferimento di prodotto: **Quokka v10 nella [PR #553](https://github.com/casazen/backend/pull/553)**; il file integrato [analisi-funzionalita.md](prospect-quokka360/analisi-funzionalita.md) è ancora alla v4. Non usare la v4 per sovrascrivere le decisioni della v10.

- Quokka incassa e riversa le quote ai proprietari.
- Nessuna scadenza fissata; numero di proprietà e territori del pilota da concordare.
- CRM attuale esterno, ancora da identificare.
- Volumi dichiarati: 20.000 prenotazioni annue e circa 200 ospiti/mese; rapporto da chiarire prima di dimensionare i costi.

Decisioni raccolte: [intervista](INTERVISTA-PLANNING-2026-10-20.md). Evidenze del primo triage: [verifica](VERIFICA-PLANNING-2026-10-20.md).

## Fase 1 in sviluppo

Dal 23 ottobre 2026 è in sviluppo la **fase 1**: le parti del pilota che si sviluppano senza fornitori terzi. Il documento che la guida, e che si fornisce a Helix, è [requisiti-fase1-helix.md](prospect-quokka360/requisiti-fase1-helix.md) (requisiti Q1-01…Q1-29).

- Contenuto: livello Agenzia e proprietari; nucleo comune (consensi per canale, coda di approvazione, condizioni, servizio AI, canali di messaggio); check-in con documento, selfie e confronto del volto in casa, verifica dell'operatore, QR e codice di accesso; Alloggiati automatico contro un server finto; adempimenti senza API; portale e rendiconti dei proprietari con ritenute, F24, file di bonifico e fatture generate; fornitori propri e staff; CRM e campagne email; routine.
- Le porte verso terzi (WhatsApp, SMS, SDI, videochiamata, enti) restano finte e i flag spenti fino ai gate di lancio indicati nel documento.
- **Channel management escluso**: è un punto ancora aperto del PO, che deve capire cosa offre il fornitore. La specifica Octorate della [PR #558](https://github.com/casazen/backend/pull/558) resta una traccia separata e non entra nella fase 1.
- Sviluppo con il processo Helix (`casazen/.helix`, fuori dai repository): il documento è l'input del run. Domanda bloccante prima dei rendiconti: D-03, da dove arrivano gli importi delle prenotazioni OTA.

## Prossimo lavoro

Avviare il run Helix della fase 1 (intake, gap, ADR, scomposizione, consegna) e rispondere alle domande della sezione 6 dei requisiti. In parallelo, attività del PO che non consumano sviluppo: valutazione del channel manager, iter con Google, Meta e WhatsApp, una struttura reale per il primo invio ad Alloggiati, conferma scritta di fal sui dati di AuraFace. Riallineare business plan e listino della [PR #528](https://github.com/casazen/backend/pull/528) alle decisioni Quokka. Le informazioni mancanti bloccano solo le attività che ne dipendono.

## Pulizia autorizzata

Il PO ha richiesto la cancellazione del precedente piano AI assistants e dei suoi task e domande: l'analisi sarà rifatta in futuro. Rimosse tutte le specifiche della precedente raccolta Sessions. I report di audit rimasti sono evidenze storiche, non il planning attivo. Le funzionalità AI previste da Quokka restano nella sua analisi.
