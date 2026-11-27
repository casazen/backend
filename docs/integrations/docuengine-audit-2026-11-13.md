# Verifica CasaZen / OpenAPI DocuEngine — 13 novembre 2026

Esito: integrazione parziale, non allineata al contratto reale del servizio di registrazione. La presenza del client HTTP e del pagamento Stripe non dimostra la possibilità di completare una pratica. Nessuna pratica è stata inviata durante la verifica.

## Fonti e prove

- https://console.openapi.com/apis/docuengine/info
- https://console.openapi.com/it/apis/docuengine/documentation
- Specifica ufficiale: https://console.openapi.com/oas/en/docuengine.openapi.json (DocuEngine 1.0.0).
- Catalogo autenticato: GET https://docuengine.openapi.com/documents: HTTP 200, success=true, 53 servizi.
- Stesso token su GET https://test.docuengine.openapi.com/documents: HTTP 401, Wrong Token. Il token è quindi stato validato solo per produzione.
- Servizio: `Registrazione Contratti Affitto`, documentId `67e13cfd2cbb7e30d3d93e9d`, prezzo di catalogo EUR 12,90, 61 campi field0–field60.
- SHA-256 della specifica scaricata: `98f084556b6dac33e11a6315be2ca68aa88a9b896e2393f34bb6224e0382ee55`.
- Commit backend esaminato: `255db3de138c9bddf086d1bc60a661e2c24af999`. Le modifiche frontend del turno precedente sono locali e non pubblicate.

## Risultati

| Priorità | Area | Riscontro |
|---|---|---|
| Bloccante | Payload registrazione | `OpenapiLeaseSearch` invia 14 campi in ordine ipotizzato; il servizio reale ne definisce 61. field0 richiede territorialOffice, ma riceve Libero/Transitorio/Concordato. field1 richiede rentalContractType, ma riceve il regime fiscale. Anche i valori successivi sono in posizioni errate. |
| Bloccante | Dati e allegati | Non vengono prodotti ufficio territoriale, numero pagine/copie, data stipula, provincia, rendita catastale, contatti e codice fiscale del richiedente, nomi delle parti, documenti identità di locatore/conduttore, garanzie e clausole penali. Sono previsti anche ulteriori locatori/conduttori e immobili. Il solo PDF firmato non basta. |
| Bloccante | Codici e condizioni | Il fornitore richiede codici L1/L2/... e ORDINARIO/CEDOLARE; il codice invia enum CasaZen. Cedolare: percentuali possesso. Ordinario: IBAN, CF intestatario e tipo pagamento imposte. Una semplice modifica a LeaseFieldOrder non genera i valori mancanti. |
| Alta | Validazione prima di Stripe | Il checkout crea la sessione dopo il controllo di appartenenza al tenant, senza verificare tutti i prerequisiti DocuEngine, la configurazione del client, lo stato firmato o la presenza degli allegati richiesti. La validazione completa deve precedere l’incasso. |
| Alta | Ripresa dopo pagamento | Il gate server verifica un pagamento Completed e importo corretto prima dell’invio. Però un nuovo click su checkout dopo un pagamento già concluso riceve openapi_registration_already_paid; la UI tratta il caso come errore generico, senza offrire direttamente la ripresa della delega/invio. |
| Alta | Importo e IVA | Il catalogo conferma EUR 12,90 per OpenAPI. Il codice aggiunge EUR 20,00 CasaZen e crea un checkout di EUR 32,90. Il pannello dettaglio dice EUR 32,90 + IVA, ma la sessione non aggiunge IVA né imposta automatic_tax/tax_rates. Allineare il prezzo esposto e la configurazione fiscale senza assumere un’aliquota. |
| Alta | Instradamento stato/ricevuta | L’invio può usare OpenAPI anche con il provider generico configurato. `OpenapiAwareLeaseRegistrationProvider` sceglie però OpenAPI per polling e ricevuta solo se il provider generico non è configurato. Con entrambi configurati, l’ID può essere interrogato sul provider sbagliato. Va persistita l’identità del provider per ogni pratica. |
| Media | Callback | Il payload callback JSON con header segreto è conforme allo schema NewCallback. Nel backend non è stato trovato un endpoint che consumi X-Casazen-Openapi-Secret. Non configurare CallbackUrl finché non esiste il relativo handler. Il polling è già previsto e può sostituire la callback. |
| Media | Test | I test mock di OpenapiNonConcordatoRegistrationTests asseriscono esplicitamente field0=Libero/Transitorio: verificano il mapping attuale, non il contratto reale. Servono fixture del catalogo e test dei campi/codici/condizioni effettivi. |
| Conforme | Trasporto base | Bearer, POST /requests con documentId/search, envelope data.id, GET /requests/{id}, stati DONE/CANCELLED, GET /requests/{id}/documents con data[].downloadUrl, file base64/URL e gestione HTTP 402 sono coerenti con la specifica. Non è stata eseguita una pratica completa. |

## Variabili Railway

Applicazione: casazen-api / backend / production. Con la skill Railway sono state salvate `Openapi__Token` e `Openapi__BaseUrl=https://docuengine.openapi.com`. Il valore del token non è incluso in questo documento o nei file del repository. La lettura delle variabili tramite OAuth espone solo i nomi.

È stato usato skipDeploys=true: le nuove variabili saranno caricate al prossimo deployment. Non è stato avviato un deploy. `Openapi__LeaseDocumentId` non è stato impostato e `Features__RliProvider` non è stato attivato: il mapping resta incompleto. Il valore predefinito del flag nel repository è false; il nome della variabile non risultava presente nel servizio di produzione.

Il token di produzione non è stato copiato nell’ambiente test, perché la sandbox lo rifiuta. Il suo accesso GET /documents è stato verificato; i permessi POST e l’esito di registrazione non sono stati provati.

## Lavori necessari per l’allineamento

1. Versionare il contratto del servizio reale e implementare field0–field60 con i corretti codici e le condizioni obbligatorie.
2. Raccogliere nella UI e nel backend i dati e documenti mancanti; validare la pratica prima di creare Stripe Checkout.
3. Allineare prezzo/IVA e aggiungere la ripresa delle pratiche già pagate. Conservare la conferma server del pagamento prima dell’invio.
4. Persistire il provider della pratica e usare quello per polling e ricevuta; implementare la callback solo se necessaria.
5. Sostituire i test del mapping ipotizzato con test basati sul catalogo verificato; collaudare pagamento, delega, invio, stato e ricevuta in un ambiente di prova con credenziali compatibili.
6. Solo dopo l’allineamento configurare documentId, abilitare il percorso e pubblicare le modifiche.

## Confronto posizioni del payload attuale

| Campo | CasaZen attuale | DocuEngine richiede |
|---|---|---|
| field0 | contractType | territorialOffice |
| field1 | taxRegime | rentalContractType |
| field2 | startDate | contractStartDate |
| field3 | endDate | contractEndDate |
| field4 | annualRent | contractPages |
| field5 | landlordFiscalCode | contractCopies |
| field6 | tenantFiscalCode | annualFee |
| field7 | city | contractDate |
| field8 | address | contractFile |
| field9 | cadastralSheet | province |
| field10 | cadastralParcel | town |
| field11 | cadastralSubaltern | address |
| field12 | cadastralCategory | cadastralSheet |
| field13 | signedContract | cadastralParcel |

## Campi del servizio reale

Obbligatorietà: la colonna riflette required del catalogo; ulteriori condizioni sono espresse dalla sua validation e dagli help (tra cui regime fiscale, quote e più parti).

| Campo | Nome | Tipo | Required |
|---|---|---|---|
| field0 | territorialOffice | territorialOffice | sì |
| field1 | rentalContractType | optionCode | sì |
| field2 | contractStartDate | date | sì |
| field3 | contractEndDate | date | sì |
| field4 | contractPages | integer | sì |
| field5 | contractCopies | integer | sì |
| field6 | annualFee | float | sì |
| field7 | contractDate | date | sì |
| field8 | contractFile | file | sì |
| field9 | province | string | sì |
| field10 | town | string | sì |
| field11 | address | string | sì |
| field12 | cadastralSheet | cadastralSheet | sì |
| field13 | cadastralParcel | cadastralParcel | sì |
| field14 | cadastralSub | cadastralSub | sì |
| field15 | cadastralIncome | float | sì |
| field16 | cadastralCategory | optionCode | sì |
| field17 | taxRegime | optionCode | sì |
| field18 | applicantName | string | sì |
| field19 | applicantPhone | string | sì |
| field20 | applicantEmail | email | sì |
| field21 | privateApplicantTaxCode | taxCode | condizionale/opzionale |
| field22 | legalApplicantTaxCode | taxCode | condizionale/opzionale |
| field23 | ibanCode | string | condizionale/opzionale |
| field24 | accountHolderTaxCode | taxCode | condizionale/opzionale |
| field25 | landlordName | string | sì |
| field26 | landlordTaxCode | taxCode | sì |
| field27 | landlordDocument | file | sì |
| field28 | landlordPercentage | float | condizionale/opzionale |
| field29 | secondLandlordName | string | condizionale/opzionale |
| field30 | secondLandlordTaxCode | taxCode | condizionale/opzionale |
| field31 | secondLandlordDocument | file | condizionale/opzionale |
| field32 | secondLandlordPercentage | float | condizionale/opzionale |
| field33 | thirdLandlordName | string | condizionale/opzionale |
| field34 | thirdLandlordTaxCode | taxCode | condizionale/opzionale |
| field35 | thirdLandlordDocument | file | condizionale/opzionale |
| field36 | thirdLandlordPercentage | float | condizionale/opzionale |
| field37 | conductorName | string | sì |
| field38 | conductorTaxCode | taxCode | sì |
| field39 | conductorDocument | file | sì |
| field40 | secondConductorName | string | condizionale/opzionale |
| field41 | secondConductorTaxCode | taxCode | condizionale/opzionale |
| field42 | secondConductorDocument | file | condizionale/opzionale |
| field43 | thirdConductorName | string | condizionale/opzionale |
| field44 | thirdConductorTaxCode | taxCode | condizionale/opzionale |
| field45 | thirdConductorDocument | file | condizionale/opzionale |
| field46 | feesPaymentGuarantees | optionCode | sì |
| field47 | guaranteeProviderTaxCode | taxCode | condizionale/opzionale |
| field48 | maximumGuaranteeAmount | float | condizionale/opzionale |
| field49 | penaltyClauses | optionCode | sì |
| field50 | taxPaymentType | optionCode | condizionale/opzionale |
| field51 | secondPropertyCadastralSheet | cadastralSheet | condizionale/opzionale |
| field52 | secondPropertyCadastralParcel | cadastralParcel | condizionale/opzionale |
| field53 | secondPropertyCadastralSub | cadastralSub | condizionale/opzionale |
| field54 | secondPropertyCadastralIncome | float | condizionale/opzionale |
| field55 | secondPropertyCadastralCategory | optionCode | condizionale/opzionale |
| field56 | thirdPropertyCadastralSheet | cadastralSheet | condizionale/opzionale |
| field57 | thirdPropertyCadastralParcel | cadastralParcel | condizionale/opzionale |
| field58 | thirdPropertyCadastralSub | cadastralSub | condizionale/opzionale |
| field59 | thirdPropertyCadastralIncome | float | condizionale/opzionale |
| field60 | thirdPropertyCadastralCategory | optionCode | condizionale/opzionale |
