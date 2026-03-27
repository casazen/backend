# Comunicazione Alloggiati Web

## Riferimento Normativo
- **Fonte primaria**: Art. 109 TULPS (Testo Unico Leggi di Pubblica Sicurezza - R.D. 18/06/1931 n. 773)
- **Decreto attuativo**: D.M. Ministero dell'Interno 07/01/2013
- **Portale**: Alloggiati Web (Polizia di Stato)

## Sintesi dell'Obbligo

Tutti i gestori di strutture ricettive (incluse locazioni turistiche e affitti brevi) hanno l'**obbligo di comunicare** alla Questura i dati di tutti gli ospiti che pernottano.

### Caratteristiche Principali
- **Termine**: entro **24 ore dall'arrivo** (immediatamente se soggiorno < 24h)
- **Soggetti obbligati**: tutti i gestori di strutture ricettive, B&B, affittacamere, case vacanze, locazioni turistiche
- **Dati richiesti**: dati anagrafici completi + documento identità di tutti gli ospiti (compresi minori)
- **Sanzioni**: **penali** (non solo amministrative)

## Modalità di Comunicazione

### Piattaforma Nazionale: Alloggiati Web
La comunicazione avviene tramite il portale **Alloggiati Web** della Polizia di Stato.

**Accesso:**
- Credenziali rilasciate dalla Questura competente per territorio
- Registrazione preliminare necessaria

**Dati da Comunicare:**
Per ogni ospite (adulti e minori):
- Nome e cognome
- Data e luogo di nascita
- Residenza
- Cittadinanza
- Tipo e numero documento identità
- Data arrivo e partenza prevista

### Piattaforme Regionali Alternative
Alcune Regioni/Comuni hanno piattaforme proprie che sostituiscono o integrano Alloggiati Web:
- **Toscana**: portali provinciali
- **Veneto**: piattaforma regionale
- **Puglia**: portale regionale
- Altri: verificare con Comune/Regione di competenza

**Importante**: utilizzare la piattaforma dell'ente territoriale competente.

## Scadenze e Tempistiche
- **24 ore dall'arrivo**: termine ordinario
- **Immediatamente**: se il soggiorno è inferiore a 24 ore
- **Check-out**: non è richiesta comunicazione di partenza (solo arrivo)

## Sanzioni
- **Omessa comunicazione**: sanzione **penale** (contravvenzione)
- **Comunicazione tardiva**: sanzione penale
- **Dati incompleti o errati**: sanzione amministrativa/penale

Le sanzioni sono **personali** (a carico del gestore) e non possono essere delegate.

## Impatto su CasaZen

### Funzionalità Coinvolte

1. **Guest Management**
   - Raccolta completa dati ospite in fase booking:
     - Dati anagrafici (nome, cognome, data nascita, residenza)
     - Cittadinanza
     - Tipo e numero documento identità
     - Scadenza documento
   - Upload scan/foto documento identità
   - Validazione completezza dati prima dell'arrivo

2. **Check-In Digitale**
   - Self check-in ospiti via web/mobile app
   - Compilazione modulo alloggiati da parte dell'ospite
   - Raccolta consenso privacy (GDPR)
   - Verifica documento identità (scan/OCR)

3. **Integrazione Alloggiati Web**
   - API/connettore per invio automatico dati a portale Alloggiati Web
   - Mapping dati CasaZen → formato Alloggiati Web
   - Gestione credenziali Questura per proprietario
   - Conferma invio e tracking comunicazioni

4. **Integrazione Portali Regionali**
   - Connettori specifici per piattaforme regionali (Toscana, Veneto, Puglia, etc.)
   - Configurazione automatica in base a ubicazione proprietà
   - Fallback manuale se API non disponibile

5. **Compliance Dashboard**
   - Alert ospiti con dati incompleti (pre-arrivo)
   - Monitor comunicazioni effettuate vs. prenotazioni
   - Scadenza 24h evidenziata per comunicazioni pendenti
   - Report comunicazioni mancanti/in ritardo

6. **Comunicazione ISTAT (separata)**
   - Oltre ad Alloggiati Web, esiste obbligo **separato** di comunicazione ISTAT
   - Dati aggregati mensili (numero arrivi, presenze, nazionalità)
   - Scadenza: mensile (varia per regione)
   - Integrare nel sistema di reporting

### Criticità Tecniche
- **API Alloggiati Web**: non sempre disponibile/stabile, potrebbe richiedere inserimento manuale
- **Portali regionali**: eterogeneità tecnologie, alcuni richiedono inserimento manuale
- **Dati incompleti**: ospiti potrebbero non fornire dati completi in tempo
- **Minori**: obbligo comunicazione anche per minori, genitori potrebbero non fornire documenti
- **Check-in last-minute**: se ospite arriva senza prenotazione, raccolta dati urgente
- **Responsabilità penale**: il proprietario è personalmente responsabile, CasaZen è solo strumento

### Dati da Tracciare
Per ogni ospite:
- Dati anagrafici completi
- Documento identità (tipo, numero, scadenza, ente rilascio)
- Scansione/foto documento
- Data/ora comunicazione effettuata
- Conferma invio portale (numero protocollo se disponibile)
- Eventuali errori/fallimenti comunicazione

Per ogni proprietà:
- Credenziali accesso Alloggiati Web
- Portale regionale alternativo (se applicabile)
- Configurazione automatica comunicazioni

## Workflow Ottimale
1. **Pre-arrivo** (3-7 giorni prima):
   - Invio email/SMS ospite con link check-in digitale
   - Richiesta compilazione dati + upload documento

2. **Pre-arrivo** (1 giorno prima):
   - Reminder se dati non completi
   - Alert proprietario se dati mancanti

3. **Check-in** (giorno arrivo):
   - Invio automatico dati ad Alloggiati Web/portale regionale
   - Conferma invio
   - Fallback manuale se errore

4. **Post check-in** (entro 24h):
   - Monitoraggio comunicazioni pendenti
   - Alert urgente se scadenza 24h imminente

## Sorgenti
- [ANBBA - Vademecum 2026 Locazioni Turistiche](https://www.anbba.it/vademecum-locazioni-turistiche-2026/)
- [Lodgify - Comunicazione ISTAT](https://www.lodgify.com/blog/it/comunicazione-istat-ospiti/)
- [Affitti Brevi 360 - Alloggiati Web](https://affittibrevi360.it/disbrigo-check-in-burocrazia/comunicazioni-alla-questura-per-gli-affitti-brevi-cosa-devi-sapere/)
- [CheckInFacile - Multa Alloggiati Web 2026](https://checkinfacile.com/blog/multa-alloggiati-web-sanzioni.html)

**Data consultazione**: 2026-03-27
