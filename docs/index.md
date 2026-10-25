# Documentazione CasaZen

La documentazione tecnica e operativa condivisa vive in questa cartella. La guida per gli utenti è consultabile nella webapp dopo il login; gli appunti di analisi e planning restano separati in `Sessions/`.

## Percorsi di lettura

| Esigenza | Partire da |
|---|---|
| Usare il prodotto | **Guida** nell’header della webapp; [fonte e manutenzione](https://github.com/casazen/frontend/blob/develop/docs/product/README.md) |
| Avviare il backend | [README](../README.md), poi [sviluppo locale](technical/development.md) |
| Capire il backend | [Architettura](technical/architecture.md), poi [riferimento tecnico](TECHNICAL.md) |
| Sviluppare il frontend | [Documentazione webapp](https://github.com/casazen/frontend/blob/develop/docs/README.md) |
| Lavorare sul mobile | [Repository mobile](https://github.com/casazen/mobile) e [runbook rilascio](runbooks/mobile-release.md) |
| Configurare o diagnosticare una funzione | [Indice runbook](runbooks/index.md) |
| Preparare un deploy | [Checklist](runbooks/deploy-checklist.md), [health check](runbooks/health-checks.md), [infrastruttura](INFRA.md) |
| Seguire il pilota Quokka | [Planning](../Sessions/PLANNING.md), poi i [requisiti della fase 1 in sviluppo](../Sessions/prospect-quokka360/requisiti-fase1-helix.md) |
| Capire, configurare o provare il servizio ConcordatoFacile | [Processo CF: unico riferimento](integrations/concordatofacile/README.md), poi i [requisiti](../Sessions/business-analysis-canone-concordato.md) |

## Struttura

| Percorso | Contenuto e stato |
|---|---|
| `technical/` | Guide correnti di architettura e sviluppo backend |
| [TECHNICAL.md](TECHNICAL.md) | Riferimento esteso: API, dati, servizi, sicurezza e test |
| [BUSINESS.md](BUSINESS.md) | Modello di dominio e processi; non sostituisce la verifica dello stato implementativo |
| [PROJECT.md](PROJECT.md) | Contesto sintetico del repository per onboarding e agenti |
| [INFRA.md](INFRA.md) | Infrastruttura e ambienti; le sezioni Railway sono storiche, hosting in revisione: [analisi](runbooks/free-hosting-analysis.md) |
| [runbooks/](runbooks/index.md) | Procedure per funzionalità, configurazione, migrazioni, verifica e rollback |
| `adr/` | Decisioni architetturali; [modello ADR](adr/ADR-000-TEMPLATE.md) |
| `integrations/` | Specifiche di integrazione: una specifica non prova che il connettore sia implementato. Per ConcordatoFacile vale solo il [documento di processo](integrations/concordatofacile/README.md) (le specifiche della cartella `da-inviare/` sono superate) |
| `legal/`, `legal-drafts/` | Materiale legale e bozze; conservare lo stato di approvazione indicato nei documenti |
| [AUTH0_SETUP.md](AUTH0_SETUP.md) | Setup Auth0; completare con il [runbook operativo](runbooks/auth0.md) |
| [FRONTEND-TECHNICAL.md](FRONTEND-TECHNICAL.md), [FRONTEND-PROJECT.md](FRONTEND-PROJECT.md) | Indirizzamento alla documentazione mantenuta nel repository frontend |
| [AI-STRATEGY.md](AI-STRATEGY.md) | Strategia e ipotesi di prodotto, da distinguere dalle capacità operative disponibili |

## Fonti per i flussi utente

- Immobili e calendari: [attivazione](runbooks/activation-checklist.md), [iCal](runbooks/ical.md), [cambio locazione](runbooks/property-rental-mode.md).
- Prenotazioni e ospiti: [prenotazione diretta](runbooks/direct-booking.md), [messaggi](runbooks/guest-messages.md), [GDPR](runbooks/gdpr.md).
- Adempimenti: [Alloggiati](runbooks/alloggiati.md), [flussi regionali](runbooks/ross1000.md), [imposta di soggiorno](runbooks/tourist-tax-rates.md).
- Organizzazione: [persone e ruoli](runbooks/org-team.md), [fatturazione](runbooks/billing-tax.md).
- Affitti lunghi: [bozza guidata](runbooks/long-rent-lease-draft.md), [contratto già firmato](runbooks/long-rent-lease-import.md), [RLI](runbooks/rli.md), [template contratti](runbooks/lease-contract-templates.md).
- Fornitori: [profili e richieste](runbooks/suppliers.md).

Alloggiati è attualmente un flusso di preparazione e invio manuale. L'invio automatico è in sviluppo per il pilota (Q1-14 dei [requisiti della fase 1](../Sessions/prospect-quokka360/requisiti-fase1-helix.md)): finché non è rilasciato non modifica lo stato del connettore consegnato. Verificare sempre esiti e ricevute, non soltanto la generazione di un file.

## Mantenimento

Aggiornare il documento del dominio nella stessa PR che modifica il comportamento. Evitare nuove copie della stessa procedura: collegare il runbook competente. Mantenere gli indirizzi dei documenti estesi esistenti per preservare i link usati da codice e istruzioni di repository. I README vicini a seed, test e risorse restano accanto ai relativi file.
