# Guest Portal · casazen/backend#987

Il piano usa la [specifica della issue](https://github.com/casazen/backend/issues/987) e le tavole Figma del pacchetto CasaZen Guest Portal. Il backend parte dai contratti già integrati in [#1016](https://github.com/casazen/backend/pull/1016) e [#1018](https://github.com/casazen/backend/pull/1018).

| Incremento | Stato sul ramo |
| --- | --- |
| E50 · Accesso, panoramica, email | Frontend sul contratto ufficiale `/s/:token`. Scheda host con emissione, copia, rigenerazione, revoca e invio email. Recupero con codice ed email per le prenotazioni dirette e OTA. Account web per le prenotazioni con email verificata corrispondente e manifest per installare il soggiorno. Restano associazione OTA senza email e migrazione dei vecchi punti di ingresso. |
| E51 · Check-in e guida | Apertura del check-in con il token del soggiorno, orario di arrivo, guida per immobile, QR pubblico e visibilità server per le parti riservate. Il codice porta usa il servizio di rilascio esistente. Il check-in del portale registra la versione delle regole accettate. Restano traduzioni e approvazione delle proposte AI. |
| E52 · Chat | Chat manuale per dirette e OTA nell’hub CasaZen. Il browser rilegge il thread durante l’apertura. Restano integrazione OTA quando disponibile, traduzione e risposte automatiche. |
| E53 · Soggiorno e privacy | Cancellazione diretta con anteprima di rimborso e nuova conferma se l’importo cambia. Indicazione del canale di prenotazione per OTA. Restano richiesta di modifica con approvazione e differenza e il saldo. Il centro privacy gestisce le preferenze per email e WhatsApp e inoltra copia e cancellazione dei dati come attività con scadenza a 30 giorni. La scheda Privacy dell’ospite permette all’host autorizzato di chiuderle dopo aver risposto. |
| E54 · Documenti e recensioni | Elenco e PDF dei documenti fiscali CasaZen emessi per il soggiorno. Invio di una recensione verificata diretta o OTA nella finestra esistente. La richiesta di correzione di una fattura emessa apre un’attività per l’host. Restano i documenti degli extra e dei fornitori e invito OTA. |
| E55 · Extra dell’host | Restano catalogo, prezzi e disponibilità, acquisto e relativa fatturazione. |
| E56–E58 · Fornitori, incassi, controversie | Restano catalogo esposto per casa, inviti e origine del fornitore, commissioni e riparto, ordini, rilascio dei fondi, rimborsi, contestazioni e revisione. L’attivazione degli incassi richiede il parere indicato in §8 della specifica. |
| E59–E60 · Configurazione e verifica di rilascio | Restano wizard, accettazione completa nel browser e deploy coordinato dei due repository nell’ambiente test. |

## Verifica

La migrazione `AddGuestHouseGuide` aggiunge la guida per proprietà e la versione delle regole accettate nella sessione di check-in. La guida viene cifrata per immobile e mostrata come testo semplice. La guida e il riepilogo sono legati all’organizzazione e all’immobile ricavati dal token. L’endpoint QR omette accesso, indirizzo e Wi-Fi. Il download dei PDF include solo documenti CasaZen emessi per la prenotazione aperta.

`GuestPortalOverviewIntegrationTests` usa PostgreSQL e verifica il confine del token. Il nuovo scenario invia e legge un messaggio su una prenotazione OTA, verifica i campi riservati della guida, l’esclusione di una cancellazione OTA, la finestra recensione e l’assenza di documenti per il soggiorno di prova.

Solo quando il rilascio coordinato è completo il flag `Features__GuestPortal` va configurato sul servizio test di Railway. Le funzionalità supplier e risposte automatiche hanno flag distinti. Per i criteri di attivazione si applica la specifica.
