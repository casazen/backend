# Canone concordato: range, dati dell'accordo, tipo di contratto (LT-10)

Task LT-10, difetti A7-10, A7-11, A7-12, A7-13, A7-23. Fonti e classi (U/D/T): `.claude/context/regulations/canone_concordato.md`,
sezioni "Accordi verificati (2026-09)" e "Cosa ha cambiato LT-10".

## Cosa fa il codice

| Punto | Comportamento |
|---|---|
| Tipo di contratto | `ContractType` sul lease: `Libero` (4+4, almeno 4 anni), `Concordato` (3+2, almeno 3 anni), `Transitorio` (1-18 mesi, solo la durata). Durata sempre dalle date (fine inclusa). 422 `lease_term_too_short` / `lease_term_too_long` / `lease_end_before_start`, anche prima della firma e della registrazione |
| Regime fiscale | `TaxRegime` (`CedolareSecca`, `Ordinario`), separato dal tipo. `FiscalRegime` resta come valore derivato (template, IMU, advisory cedolare). I client che inviano solo `fiscalRegime` sono ancora accettati |
| Range | `GET /api/properties/{id}/canone-concordato/eligibility?...&startDate=&endDate=`: calcolato dal server con le regole dell'accordo; nessun conteggio di anni dal client |
| Creazione | `POST /api/leases` con `contractType: Concordato` richiede `canoneConcordato` (caratteristiche). Il server ricalcola il range e lo salva sul lease (`concordatoAssessment`) |
| Dati `Complete` | Canone fuori range → 422 `concordato_rent_out_of_range` |
| Dati `Partial` | Range **indicativo** (`indicative: true`, avviso `partial_data`, fonte e data di verifica): il lease si crea anche con un canone fuori range, l'avviso resta sul lease (`rentWithinRange: false`) |
| Dati `Missing` | 422 `concordato_range_unavailable` (comportamento precedente mantenuto, vedi "Decisioni aperte") |
| Comune ATA | `ataApplies` solo con `HighTensionAreaComuni.VerifiedDirectly` (`HighTensionArea.ReliefsApply`): la stessa regola dell'advisory fiscale del lease (LT-08), che applica cedolare 10% e base del registro al 70% solo in quel caso. Come verificare un comune: `rli.md`, "Tax advisory (LT-08)" |
| Dati per il contratto | Dati catastali dell'immobile (`PUT /api/properties/{id}/cadastral`), codice e classe dell'APE (`PUT /api/properties/{id}/documents/{docId}/ape`), deposito cauzionale del lease: vedi `lease-contract-templates.md` |

## Passare un accordo da Partial a Complete

Solo dopo una conferma scritta di un'organizzazione firmataria o del Comune su vigenza, deposito e punti D
(`canone_concordato.md`, "Esito"). Con `Complete` il range **blocca** la creazione dei lease fuori range.

1. Conservare la conferma (documento, data, mittente) fuori dal database.
2. Aggiornare il comune, per esempio Seveso:
   ```sql
   UPDATE "TerritorialRentAgreements"
   SET "DataCompleteness" = 0, "LastVerifiedAt" = '<data della conferma>'
   WHERE "Comune" = 'Seveso' AND "AgreementName" = 'Accordo locale Quadro — Provincia di Monza e della Brianza';
   ```
   (`DataCompleteness`: 0 Complete, 1 Partial, 2 Missing.)
3. Se la conferma dice che i coefficienti si **moltiplicano**: `"CoefficientCombination" = 1` sulla stessa riga
   (0 = somma, valore attuale).
4. Aggiornare `canone_concordato.md` con fonte e data.

Finché LT-13 non porta un'amministrazione dei dati normativi, queste modifiche si fanno a mano sul database di ogni
ambiente (test e produzione) e vanno riportate nel seed (`CanoneConcordatoMbSeed`) con una nuova migrazione di dati.

## Verifica

- Test unitari: `CanoneConcordatoEligibilityServiceTests` (fasce 50,5 / 74,5 / 99,5 mq, tetti e pavimenti, durata,
  stufe, pertinenze, SF 3), `LeaseWorkflowServiceTests` (durata per tipo, 422 con dati verificati, avviso con Partial).
- PostgreSQL: `LeaseConcordatoAndContractDataIntegrationTests`, `LeaseContractTypeAndConcordatoRulesMigrationPostgresTests`.
- Manuale su test: creare un lease concordato a Seveso con canone oltre il massimo: il lease si crea e il dettaglio
  mostra "Range indicativo" e "canone fuori da questo range".

## Decisioni aperte (product owner)

- Comune senza dati (`Missing`): bloccare la creazione del concordato (oggi) o consentirla con un avviso come per `Partial`?
- Transitorio: esigenze transitorie da documentare e canone nelle fasce dell'accordo non modellati; nessun modello di
  contratto.
- Punti D dell'accordo MB elencati in `canone_concordato.md` ("Dubbi rimasti").
