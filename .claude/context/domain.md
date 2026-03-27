# CasaZen - Dominio Applicativo

## Descrizione
CasaZen e' un sistema di gestione per affitti brevi (short-term rentals) in Italia. Gestisce proprieta' turistiche, prenotazioni, pagamenti e sincronizzazione con piattaforme OTA.

## Ambito Normativo
Il sistema opera nel contesto normativo italiano ed europeo relativo a:
- **Affitti brevi** (locazioni turistiche < 30 giorni)
- **Cedolare secca** e regime fiscale per locazioni brevi
- **Codice CIN** (Codice Identificativo Nazionale) obbligatorio dal 2025
- **Comunicazioni alla Questura** (alloggiati web)
- **Imposta di soggiorno** (tassa di soggiorno comunale)
- **Normativa OTA** (obblighi delle piattaforme online come intermediari)
- **GDPR** e protezione dati personali degli ospiti
- **Normativa regionale** (varia per regione/comune)

## Attori del Sistema
- **Proprietario** - possiede una o piu' proprieta', gestisce affitti
- **Ospite** - prenota e soggiorna nella proprieta'
- **Piattaforme OTA** - Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda
- **Enti pubblici** - Questura, Comune, Agenzia delle Entrate

## Processi Chiave
1. **Gestione Proprieta'** - registrazione, documentazione, codice CIN
2. **Gestione Prenotazioni** - creazione, modifica, cancellazione, sincronizzazione OTA
3. **Gestione Pagamenti** - incasso via Stripe, ricevute, contabilita'
4. **Comunicazioni Obbligatorie** - alloggiati web, imposta di soggiorno, dichiarazioni fiscali
5. **Compliance** - verifica requisiti normativi, scadenze, aggiornamenti

## Rischi Normativi Principali
- Mancata comunicazione alloggiati entro 24h dall'arrivo
- Assenza o esposizione errata del Codice CIN
- Mancato versamento imposta di soggiorno
- Non conformita' GDPR nel trattamento dati ospiti
- Mancata ritenuta fiscale da parte delle OTA (21% dal 2024)
