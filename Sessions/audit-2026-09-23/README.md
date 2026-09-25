# Audit completo CasaZen: 2026-09-23

Evidenze di dettaglio a supporto di [`../PIANO-RISANAMENTO-2026-09.md`](../PIANO-RISANAMENTO-2026-09.md).

## Metodo

- **9 audit per area**, eseguiti da agenti in sola lettura su backend (`develop@4cbaeaa`), frontend (`develop@0b91e3c`) e mobile (`develop@4b20215`). Ogni agente ha confrontato le spec in `Sessions/specs/` e `PLANNING.md` con il codice, tracciando il percorso FE → client API → controller → service → DB → job. Per ogni area ha prodotto:
  - una tabella di copertura del piano (DONE / PARTIAL / STUB / MISSING / BROKEN);
  - un elenco dei difetti con file:riga, scenario di fallimento, fix proposto ed effort;
  - un piano di fix e un verdetto.
- **1 verifica runtime** (`R0-runtime.md`):
  - build e test dei tre repo;
  - migrazioni su PostgreSQL vuoto;
  - 184 test d'integrazione rieseguiti su PostgreSQL reale invece che su EF InMemory;
  - smoke delle API pubbliche;
  - percorso guest nel browser (sito di prenotazione, checkout, "Le mie prenotazioni", portale check-in, pagine SEO) contro API e Postgres reali.
- **Limite:** senza credenziali Auth0 reali, i flussi autenticati (console host e fornitore, app) sono stati verificati tramite analisi statica e test d'integrazione, non via browser.

## Severità

- **P0:** blocca l'uso, perde dati o soldi, oppure crea un rischio di sicurezza o legale.
- **P1:** funzionalità rotta o incompleta.
- **P2:** qualità, UX o debito tecnico.
- **Effort:** S ≤ 0,5 g · M ≤ 2 g · L > 2 g.

## Report

| File | Area | Completamento reale stimato | P0 | P1 | P2 |
|---|---|---|---|---|---|
| [R0-runtime.md](./R0-runtime.md) | Build, test, runtime | — | 3 | 4 | 4 |
| [A1-piattaforma.md](./A1-piattaforma.md) | Accesso, onboarding, billing, admin | ~50% | 2 | 18 | 23 |
| [A2-proprieta-calendario.md](./A2-proprieta-calendario.md) | Proprietà, calendario, iCal, pricing, OTA | ~45% | 3 | 18 | 16 |
| [A3-booking-pagamenti-siti.md](./A3-booking-pagamenti-siti.md) | Direct booking, Stripe/Connect, siti, domini | ~40% | 7 | 17 | 18 |
| [A4-fornitori-marketplace.md](./A4-fornitori-marketplace.md) | Console fornitore, micro-marketplace | ~40% | 4 | 11 | 18 |
| [A5-compliance.md](./A5-compliance.md) | CIN, wizard, check-in, Alloggiati, GDPR, fiscale | ~30% | 8 | 20 | 10 |
| [A6-mobile-e2e.md](./A6-mobile-e2e.md) | App host, push, harness Golden Journey | ~30% | 4 | 19 | 9 |
| [A7-ltr.md](./A7-ltr.md) | Affitti lunghi (in freeze ma sviluppati) | ~25% | 6 | 10 | 15 |
| [A8-seo-ai-freeze.md](./A8-seo-ai-freeze.md) | SEO, funnel, AI, feature in freeze | ~25-30% | 3 | 14 | 13 |
| [A9-trasversale.md](./A9-trasversale.md) | Sicurezza, endpoint, test, CI | — | 6 | 20 | 14 |

I conteggi sono approssimati e includono duplicati tra aree: per esempio l'ospite condiviso tra tenant compare in A2, A5 e A9, e l'upgrade gratuito del piano in A1, A3 e A9. Il piano di risanamento li unifica.

## Attenzione ai report precedenti

I verdetti `GOAL_RAGGIUNTO` in `reports/attempt-*` e `reports/verification-2026-08-16` **non sono prova di funzionamento**. Si basavano su:
- test d'integrazione su InMemory;
- E2E con mock o in demo mode;
- un solo utente che faceva sia l'host sia il fornitore;
- dati iniettati via API;
- gate CI `echo`.

I dettagli sono nei report A2, A3, A4, A5, A6 e A9.
