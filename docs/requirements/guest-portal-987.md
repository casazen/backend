# Guest Portal · casazen/backend#987

Il piano usa la [specifica della issue](https://github.com/casazen/backend/issues/987) e le tavole Figma del pacchetto CasaZen Guest Portal. Il backend parte dai contratti già integrati in [#1016](https://github.com/casazen/backend/pull/1016) e [#1018](https://github.com/casazen/backend/pull/1018).

| Incremento | Stato sul ramo |
| --- | --- |
| E50 · Accesso, panoramica, email | Frontend sul contratto ufficiale `/s/:token`. Scheda host con emissione, copia, rigenerazione, revoca e invio email. Recupero con codice ed email per le prenotazioni dirette e OTA. Account web per le prenotazioni con email verificata corrispondente e manifest per installare il soggiorno. Un link privato permette di collegare a un account verificato anche un soggiorno OTA senza email personale. |
| E51 · Check-in e guida | Check-in con il token del soggiorno, orario di arrivo, guida per immobile, QR pubblico e parti riservate. Regole con versione e codice porta soggetto alle verifiche esistenti. Il wizard propone la bozza e i consigli locali, che l’host sceglie prima di salvare. Sono disponibili le traduzioni dei contenuti pubblici approvati in italiano, inglese, francese, tedesco e spagnolo. |
| E52 · Chat | Chat diretta e OTA nell’hub CasaZen, aggiornamento del thread e risposte automatiche basate sulla guida per orari, parcheggio e Wi-Fi, con passaggio all’host. Restano la traduzione, le bozze da testo ospite con il trattamento previsto e l’unificazione OTA quando la chat di Octorate sarà disponibile. |
| E53 · Soggiorno e privacy | Modifica delle dirette con preventivo, conferma dell’host, aggiornamento del calendario e conguaglio. Saldo e ripresa dei pagamenti esistenti. Cancellazione diretta. Consensi email e WhatsApp e richieste di copia e cancellazione dati con scadenza a 30 giorni e chiusura dalla scheda Privacy dell’ospite. |
| E54 · Documenti e recensioni | PDF fiscali CasaZen emessi, richieste di correzione e recensione verificata diretta o OTA. Sono disponibili la modifica dei dati prima della fattura, la richiesta di correzione successiva e gli inviti OTA per gli ospiti idonei. |
| E55 · Extra dell’host | Catalogo, pubblicazione per immobile, inventario, traduzioni e acquisto. Per gli extra su richiesta si autorizza la carta prima della decisione e si addebita all’approvazione. Gli extra pagati entrano nel documento fiscale quando emesso dopo il pagamento. Il checkout iniziale distingue gli extra immediati dalle richieste. Un documento integrativo copre gli extra pagati dopo la fattura. |
| E56–E58 · Fornitori e controversie | Gli host possono invitare e pubblicare fornitori di fiducia. Lo staff abilita la partecipazione al marketplace e l’host sceglie se mostrarlo. Stripe Connect di test gestisce preventivi, autorizzazioni, riparti e rimborsi. Il fornitore ha calendario e caricamento del documento per ordine. Le parti possono aggiungere foto private e chiedere il riesame umano. La valutazione esterna delle immagini richiede la validazione del trattamento dati. Gli incassi reali richiedono il parere di §8. |
| E59–E60 · Setup e rilascio | Wizard per immobile con bozze e completamento persistenti sul server. La verifica del rilascio deve controllare commit distribuito, feature flag e percorsi ospite, host, fornitore e staff. |

## Verifica

La migrazione `AddGuestHouseGuide` aggiunge la guida per proprietà e la versione delle regole accettate nella sessione di check-in. La guida viene cifrata per immobile e mostrata come testo semplice. La guida e il riepilogo sono legati all’organizzazione e all’immobile ricavati dal token. L’endpoint QR omette accesso, indirizzo e Wi-Fi. Il download dei PDF include solo documenti CasaZen emessi per la prenotazione aperta.

`GuestPortalOverviewIntegrationTests` usa PostgreSQL e verifica il confine del token. Il nuovo scenario invia e legge un messaggio su una prenotazione OTA, verifica i campi riservati della guida, l’esclusione di una cancellazione OTA, la finestra recensione e l’assenza di documenti per il soggiorno di prova.

### Verifica sull’ambiente test

1. Nell’onboarding host aprire il wizard Portale ospiti di un immobile di prova, salvare la guida e riprendere il wizard da una nuova sessione.
2. Aprire una prenotazione diretta futura con un saldo e generare il link dalla scheda Portale ospiti. Aprirlo in una finestra anonima. Verificare che la guida ometta i dati riservati prima del check-in.
3. Chiedere una modifica dall’area Soggiorno, verificare il nuovo importo, approvare dalla prenotazione host e controllare il calendario. Aprire Pagamenti, usare una carta di prova sul conto Stripe Connect di test e verificare il saldo dopo la conferma del server.
4. Aprire la chat, chiedere un orario configurato e verificare il mittente automatico. Usare «Scrivi all’host» e leggere il messaggio dall’hub.
5. Inviare una richiesta dati. Verificare l’attività dell’host, usare gli strumenti GDPR nella scheda dell’ospite, poi chiudere la richiesta dopo la risposta. Verificare lo stato dal portale.
6. Pubblicare un extra su richiesta, autorizzare la carta in modalità test, approvare come host e verificare lo stato dell’ordine. Per i servizi fornitori seguire la sequenza del [runbook dei flag](../runbooks/feature-flags.md).

In Railway test configurare `Features__GuestPortal=true`. Per collaudare anche le risposte locali e gli ordini fornitori impostare `Features__GuestPortalAutoReplies=true`, `Features__GuestPortalSupplierServices=true` e `GuestPortal__SupplierOrderSimulation=true`. Verificare separatamente il commit del backend e il dominio del frontend dopo il deploy.
