# Decisioni del product owner (2026-09-23)

Vincolanti per tutti i task del risanamento. Se un task incontra una scelta di prodotto non coperta qui, **non la inventa**: implementa la parte non ambigua e la riporta come dubbio.

| # | Tema | Decisione |
|---|---|---|
| D1 | Affitti lunghi (LTR) | **Feature attiva**: va completata e vanno corretti tutti i difetti (A7-*). Le PR Cursor LTR vanno integrate dopo review. |
| D2 | Richiesta fornitore | **Affitti brevi: legata alla singola prenotazione** (`bookingId`). **Affitti lunghi: legata alla proprietà.** Aggiornare #340 di conseguenza. |
| D3 | Dominio pubblico | **Configurabile, nessun default nel codice** (es. `App__PublicSiteBaseUrl`, `Seo__PublicBaseUrl`, `VITE_PUBLIC_SITE_URL`). Nessun dominio scritto a mano. |
| D4 | "Prezzi AI" | Rinominare in **"Suggerimenti stagionali"**, niente dicitura AI né confidenza finta, basati sul prezzo reale. Pricing dinamico vero dopo l'MVP. |
| D5 | "Paga in struttura" | La prenotazione **deve sempre essere confermata dall'host**: resta in attesa finché l'host non accetta; se accetta è valida. |
| D6 | Alloggiati Web | **Cercare le specifiche ufficiali** (tracciato, tabelle, web service) e implementare se disponibili; se non lo sono: **stato onesto** ("Da inviare manualmente") + riepilogo dati per ospite. Mai "Inviato" senza ricevuta reale. |
| D7 | Prenotazioni OTA via iCal | L'host può **convertire un blocco iCal in "soggiorno OTA"** (nome ed email ospite): da lì partono link check-in, Alloggiati e cockpit. |
| D8 | Storage file | **Supabase Storage** (API S3): bucket pubblico per foto, privato con URL firmati per documenti. |
| D9 | Configurazioni esterne | **Codice + runbook** in `docs/runbooks/`; l'app segnala (health check o avvio) le configurazioni mancanti. Le applica il product owner. |
| D10 | API OTA partner (Airbnb/Booking) | **Nascoste** dietro flag spento (restano freeze). L'iCal resta attivo e va migliorato. |
| D11 | Discovery AI fornitori | **Spenta** dietro flag. |
| D12 | `SupplierJob` e check-in QR | **Eliminare**; resta solo `ServiceRequest`. |
| D13 | Formato CIN | **Cercare la fonte ufficiale**; se non trovata: formato permissivo IT + 6 cifre ISTAT + 10 alfanumerici, segnalandolo. |
| D14 | Punti legali/fiscali | **Cercare fonti ufficiali** (leggi, Agenzia Entrate, EUR-Lex, documentazione provider) e implementare citandole. I **testi ToS/Privacy/DPA li fornisce il product owner**: non vanno scritti dagli agenti. |
| D15 | Registrazione RLI e firma elettronica | **Cercare documentazione pubblica** (Openapi.it, provider firma); se non integrabile senza contratto: **flusso manuale onesto** (firma offline con upload, registrazione manuale con promemoria). |

## Punti non decisi (restano invariati finché non vengono chiesti)
- Pagamento del fornitore: resta il flag manuale "Pagato" (nessuna integrazione Stripe verso il fornitore).
- `chargeToGuest`: resta rifiutato per gli affitti brevi (come #340).
- Durata dell'attesa di approvazione per "Paga in struttura": configurabile, da decidere (vedi BK-06).
