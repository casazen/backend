# Scenario prenotazioni Quokka360 - TEST

Creato il 17 novembre 2026, limitato a `casazen_test` e al bucket privato `casazen-test-private`. Produzione invariata.

## Contenuto
- 36 prenotazioni dal 26 ottobre al 30 dicembre 2026: 12 concluse, 3 in corso, 21 future.
- 6 prenotazioni per origine: Airbnb, Booking.com, Expedia, Vrbo, CasaZen, sito Quokka360.
- 36 anagrafiche fittizie e 72 ospiti registrati; email @example.com, documenti TEST, nessun marketing.
- 57 registrazioni di pagamento simulate: saldo completo per passate/correnti; acconto e saldo da incassare per future. Nessun identificativo Stripe o pagamento reale.
- 36 documenti: 15 emessi di test e 21 bozze; ricevute del proprietario privato per Como, fatture svizzere della Quokka360 simulata per Lugano/Ascona. Numerazione TEST, nessun invio SDI.
- 90 attività operative e promemoria. Le prenotazioni dal 23 novembre sono 18 e portano la nota di gestione Chiara Ruggeri.
- 25 file PDF di prova caricati nel bucket privato e riletti con verifica SHA-256.
- Alloggiati: 5 soggiorni italiani passati/correnti con dichiarazione manuale simulata, PDF di prova e log marcati TEST. Gli arrivi futuri sono da gestire alla data d'arrivo.
- ISTAT e tassa di soggiorno: prove simulate per ottobre e per il periodo di novembre fino al 17; due versamenti simulati Como di 18 e 66 EUR. Non attestano versamenti reali.
- Ticino: 10 registrazioni Police e 4 OTR simulate, con hash dei file di prova.
- Mittenti di adempimento impostati su Manual per le tre proprietà. Nessun collegamento Octorate e nessun invio a enti/OTA/ospiti.

## Proprietario ed emittenti
La titolarità e il mandato di test partono dal 1 ottobre per coprire lo storico. La policy `QUOKKA-SYNTHETIC-TEST` ha una approvazione di scenario etichettata "SIMULAZIONE TEST - nessuna validazione professionale reale". Nessuna approvazione di un professionista è attestata. Il profilo emittente privato italiano del proprietario resta distinto da quello dell'organizzazione svizzera non registrata IVA, creato per le fatture dei soggiorni svizzeri. Le tariffe CH 3,50 CHF/ospite/notte sono soltanto valori dello scenario; Como usa la tariffa di 3 EUR già presente nel database di test.

## Chiara
Per istruzione di Luca, invito manuale giovedì 19 novembre 2026: nessun account fittizio e nessun invito inviato. Dal 23 novembre attività e promemoria riportano Chiara, ma `StaffUserId` è vuoto fino al collegamento del suo account. Non sono assegnazioni esecutive già attive.

## Verifiche
Zero sovrapposizioni; numero ospiti coerente; zero differenze fra importi prenotazioni, documenti e loro righe; passate/correnti interamente pagate e documentate nello scenario; nessun vuoto nella copertura temporale delle titolarità; 25 file riletti con hash corretto. Dopo riavvio native EF ha cifrato documenti ospiti, dati destinatari e snapshot fiscali. Deployment test `ac14fb03-9d01-4c34-a36b-b8e79d89e364` riuscito.

Il caricatore temporaneo dei file è stato sostituito con una funzione che risponde 410, con verifica JWT attiva e senza segreti o capacità di scrittura.
