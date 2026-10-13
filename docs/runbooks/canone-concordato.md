# Canone concordato: range, dati dell'accordo, tipo di contratto (LT-10, LT-13)

Task LT-10 (difetti A7-10, A7-11, A7-12, A7-13, A7-23) e LT-13 (dati normativi su DB e amministrazione, A7-22; sezioni
e IMU solo per contratti concordato, A7-24). Fonti e classi (U/D/T): `.claude/context/regulations/canone_concordato.md`,
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

## Amministrazione dei dati normativi (LT-13, A7-22)

Da LT-13 in poi i dati dell'accordo (stato, scadenza, regole del calcolo, fasce) e i canali IMU dei Comuni sono
amministrabili da `/app/admin/compliance/ltr-reference-data` (permesso `admin.ltr.manage`), senza deploy:

- **Accordo**: `DataCompleteness`, URL della fonte, scadenza formale (`ExpiresAt`), nota di scadenza,
  "resta in vigore fino a un nuovo accordo" (`RemainsInForceUntilReplaced`) e ogni soglia/coefficiente del calcolo
  (`Casazen.Core.Entities.TerritorialRentAgreement`).
- **Fasce**: zona, fogli catastali, mq minimo/massimo e i sei valori €/mq/anno delle tre sub-fasce
  (`ConcordatoRentBand`), modificabili dalla stessa scheda dell'accordo.
- **Canali IMU**: ufficio destinatario, email, PEC, indirizzo, istruzioni e aliquota (con l'eventuale valore derivato)
  per comune (`ComuneImuChannel`), usati sia dalla bozza di comunicazione IMU (`ComuneImuNotificationService`) sia
  dall'endpoint `GET /api/leases/{id}/canone-concordato/imu-notification` che abilita i pulsanti in UI.
- **"Segna verificato"**: registra data (non nel futuro) e fonte del controllo, senza toccare gli altri campi.
- **Registro delle modifiche**: `GET /api/admin/canone-concordato/audit?entityId=` (`RegulatoryDataAuditEntry`), un
  diff campo per campo per ogni salvataggio e ogni verifica, con l'id dell'admin. Le modifiche a una fascia sono
  registrate sotto l'id dell'accordo, non della fascia, così compaiono nello stesso registro.

Passare un accordo da `Partial` a `Complete` (con `Complete` il range **blocca** la creazione dei lease fuori range)
richiede comunque una conferma scritta di un'organizzazione firmataria o del Comune su vigenza, deposito e punti D
(`canone_concordato.md`, "Esito") **prima** di cambiare lo stato dalla pagina admin: conservare la conferma (documento,
data, mittente) fuori dal database, poi aggiornare `DataCompleteness` e, se la conferma lo dice, `CoefficientCombination`
(Additiva/Moltiplicativa), e chiudere con "Segna verificato". Aggiornare anche `canone_concordato.md` con fonte e data.

I 55 comuni e i due canali IMU pilota (Seveso, Cesano Maderno) sono seminati dalla migrazione
`AddLtrReferenceDataAdmin` con valori congelati (mai dalla classe `CanoneConcordatoMbSeed` "viva", A7-22): un
aggiornamento successivo si fa dalla pagina admin, non da una nuova migrazione, a meno che serva seminare un nuovo
comune o accordo.

## Verifica

- Test unitari: `CanoneConcordatoEligibilityServiceTests` (fasce 50,5 / 74,5 / 99,5 mq, tetti e pavimenti, durata,
  stufe, pertinenze, SF 3, zone dell'accordo, avviso `agreement_expired`), `LeaseWorkflowServiceTests` (durata per
  tipo, 422 con dati verificati, avviso con Partial), `RegulatoryReferenceDataAdminServiceTests` e
  `AdminCanoneConcordatoControllerTests` (LT-13: CRUD, verifica, registro delle modifiche),
  `ComuneImuNotificationServiceTests` (canale IMU dal database, stato `GET .../imu-notification`).
- PostgreSQL: `LeaseConcordatoAndContractDataIntegrationTests`, `LeaseContractTypeAndConcordatoRulesMigrationPostgresTests`.
- Manuale su test: creare un lease concordato a Seveso con canone oltre il massimo: il lease si crea e il dettaglio
  mostra "Range indicativo" e "canone fuori da questo range".

## Decisioni aperte (product owner)

- Comune senza dati (`Missing`): bloccare la creazione del concordato (oggi) o consentirla con un avviso come per `Partial`?
- Transitorio: esigenze transitorie da documentare e canone nelle fasce dell'accordo non modellati; nessun modello di
  contratto.
- Punti D dell'accordo MB elencati in `canone_concordato.md` ("Dubbi rimasti").
