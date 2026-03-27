# Imposta di Soggiorno

## Riferimento Normativo
- **Fonte primaria**: D.Lgs. 14/03/2011 n. 23 (art. 4)
- **Modifiche recenti**: L. 30/12/2018 n. 145 (Legge di Bilancio 2019)
- **Aggiornamenti 2026**: L. 30/12/2025 n. 199 (Legge di Bilancio 2026)

## Sintesi dell'Obbligo

L'imposta di soggiorno è una **tassa comunale** applicata ai pernottamenti nelle strutture ricettive e negli immobili in locazione turistica.

### Caratteristiche Principali
- Istituita dai **Comuni** (non obbligatoria, ma sempre più diffusa)
- A carico dell'**ospite**, ma riscossa dal **gestore** della struttura
- Importo variabile per Comune e tipologia struttura
- Destinata a finanziare interventi turistici, culturali e territoriali

## Novità 2026

### Aumenti Generalizzati
Molti Comuni hanno aumentato le tariffe per il 2026:
- **Capoluoghi di provincia**: tetto massimo ~€7/giorno/persona
- **Città d'arte e turistiche**: tetto massimo fino a €12/giorno/persona
- **Comuni Olimpiadi 2026**: incremento ulteriore fino a €5/notte (Milano, Cortina, etc.)

### Incremento Olimpico (Milano-Cortina 2026)
I Comuni che ospitano eventi olimpici possono applicare un **supplemento temporaneo**:
- **Milano**: da €7 a €12/notte
- **Cortina**: aumenti significativi durante periodo olimpico
- **Altri comuni olimpici**: incremento variabile

### Gettito Nazionale 2026
- Gettito previsto: **€1,3 miliardi** (+9,2% rispetto al 2025)
- Numero Comuni con imposta: **1.409 comuni** (+20 rispetto al 2025)

### Nuove Destinazioni d'Uso
Parte del gettito aggiuntivo 2026 sarà destinato a:
- Spese per **inclusività sociale**
- Assistenza ai **minori**
- Interventi turistici e culturali tradizionali

## Modalità di Applicazione

### Chi Riscuote
- Il **gestore della struttura** (proprietario, host, property manager)
- Obbligo di riscossione anche per locazioni brevi private

### Chi Paga
- L'**ospite** al momento del soggiorno
- Pagamento contestuale o separato dal canone di locazione

### Come si Versa
- **Cadenza**: mensile, trimestrale o semestrale (varia per Comune)
- **Modalità**: F24, bonifico, o piattaforme comunali dedicate
- **Scadenze**: fissate da ciascun Comune (es. entro il 16 del mese successivo)

### Esenzioni Comuni
Tipicamente esenti (da verificare per ciascun Comune):
- Minori sotto determinata età (es. under 14)
- Residenti nel Comune
- Soggiorni per motivi di salute/cura
- Accompagnatori di disabili
- Forze dell'ordine in servizio

## Modello 21 - Dichiarazione Annuale
Molti Comuni richiedono la **presentazione annuale** del modello dichiarativo con:
- Riepilogo pernottamenti
- Importi riscossi
- Eventuali esenzioni applicate
- Versamenti effettuati

**Scadenza tipica**: 30 giugno dell'anno successivo

## Impatto su CasaZen

### Funzionalità Coinvolte

1. **Booking Management**
   - Calcolo automatico imposta soggiorno per prenotazione:
     - In base a Comune della proprietà
     - Numero ospiti e notti
     - Eventuali esenzioni (minori, residenti)
   - Addebito separato all'ospite
   - Visualizzazione breakdown costi (canone + imposta)

2. **Payment Processing**
   - Riscossione imposta contestualmente al pagamento
   - Separazione contabile: canone vs. imposta soggiorno
   - Tracking imposta riscossa ma non ancora versata (debito verso Comune)

3. **Configurazione Tariffe Comunale**
   - Database tariffe per Comune aggiornato
   - Configurazione per tipologia struttura
   - Gestione esenzioni configurabili
   - Aggiornamenti tariffari (es. incrementi 2026)

4. **Reporting e Versamenti**
   - Report mensile/trimestrale imposta riscossa
   - Generazione F24 o file per versamento
   - Storico versamenti effettuati
   - Preparazione dati per Modello 21

5. **Compliance Dashboard**
   - Alert scadenze versamenti per Comune
   - Monitor imposta riscossa vs. versata
   - Segnalazione anomalie (mancati versamenti)
   - Generazione dichiarazione annuale (Modello 21)

6. **Guest Portal**
   - Informativa trasparente su imposta dovuta
   - Breakdown dettagliato costi in fase booking
   - Ricevuta separata per imposta soggiorno

### Criticità Tecniche
- **Database tariffe comunali**: 1.409+ Comuni con tariffe variabili, da mantenere aggiornato
- **Variabilità normative**: ogni Comune ha regolamento diverso, scadenze diverse
- **Esenzioni complesse**: logica di esenzioni varia (età minori, residenza, etc.)
- **Versamenti multi-Comune**: un proprietario con immobili in Comuni diversi ha scadenze multiple
- **Incrementi temporanei**: gestire aumenti straordinari (Olimpiadi, eventi speciali)

### Dati da Tracciare
Per ogni Comune:
- Tariffe vigenti per tipologia struttura
- Scadenze versamenti (mensili/trimestrali/semestrali)
- Modalità versamento (F24, bonifico, portale)
- Regole esenzioni
- Formato e scadenza Modello 21

Per ogni prenotazione:
- Imposta soggiorno calcolata
- Imposta riscossa (data, importo)
- Esenzioni applicate
- Versamento effettuato (data, riferimento F24)

## Sorgenti
- [Idealista - Tassa soggiorno 2026](https://www.idealista.it/news/vacanze/mercato-turistico/2026/01/15/312295-tassa-di-soggiorno-2026-quanto-aumenta-e-come-cambiano-le-tariffe-nelle-citta)
- [TuttoTributi - Imposta soggiorno 2026](https://www.tuttotributi.it/imposta-di-soggiorno-il-gettito-2026-arriva-a-13-miliardi/)
- [Comune di Napoli - Imposta soggiorno 2026](https://www.comune.napoli.it/articolo_tematico/tributi-locali/imposta-di-soggiorno/imposta-di-soggiorno-2026/)

**Data consultazione**: 2026-03-27
