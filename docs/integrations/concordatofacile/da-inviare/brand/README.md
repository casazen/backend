# CasaZen — kit marchio per ConcordatoFacile

Questo kit contiene il marchio CasaZen da usare per la veste personalizzata del modulo e delle comunicazioni all'utente, su fondo chiaro.

## File inclusi

- `lockup.svg`: marchio completo con simbolo e nome CasaZen. Usarlo quando deve essere visibile anche il nome del servizio, ad esempio nell'intestazione del modulo o delle email.
- `marchio.svg`: solo simbolo CasaZen, senza nome. Usarlo per spazi compatti o dove il nome CasaZen è già presente.
- `README.md`: descrizione del kit, colori e indicazioni d'uso.

I due file SVG sono vettoriali e possono essere ridimensionati mantenendo le proporzioni. Il simbolo rappresenta un cerchio zen a pennello in terracotta che incornicia una casa in inchiostro.

## Colori su fondo chiaro

| Colore | Codice HEX | Uso |
|---|---|---|
| Terracotta | `#e2622d` | Cerchio zen e colore di accento |
| Inchiostro | `#1f3a3d` | Casa e nome CasaZen |

Usare uno sfondo bianco o chiaro che mantenga leggibili entrambi i colori.

## Indicazioni d'uso

- Mantenere proporzioni e colori originali: non deformare, ruotare, ricolorare o aggiungere ombre.
- Lasciare uno spazio libero intorno al marchio almeno pari alla larghezza della casa contenuta nel simbolo.
- Dimensioni minime: simbolo 16 px; marchio completo 24 px di altezza. Verificare sempre la leggibilità nel contesto finale.
- Il nome nel lockup usa Figtree 600, se disponibile, con alternativa sans di sistema. Per la stampa convertire il testo in tracciati.

## Dominio e veste personalizzata

Il dominio definitivo CasaZen è `https://casazen.space`.

Una volta ricevuti asset e dominio, attendiamo la vostra data per la veste personalizzata.

## Indirizzo da inserire nelle email

Nel bottone «Carica i dati in CasaZen» e nel link per esteso della conferma testuale del modulo usare:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

Sostituire `<leaseId>` con l'UUID CasaZen ricevuto nei parametri del modulo. Esempio:

`https://casazen.space/app/long-rent/documents/upload?leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`

È l'indirizzo web di atterraggio per l'utente, non l'endpoint di stato né l'API di upload. Dopo l'accesso, CasaZen individua la casa dal contratto e permette all'utente di caricare i documenti. Non inserire dati personali, chiavi o propertyId nel link. Il percorso deve essere implementato e verificato prima di attivare i link nelle email operative.
