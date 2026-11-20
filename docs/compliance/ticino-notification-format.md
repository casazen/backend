# Ticino police notification file (notifiche.ti.ch)

**Issue:** casazen/backend#979 · **Verified:** 2026-11-19 against the official sources below · **Replaces:** the invented
`<TicinoNotification>` XML described here until 2026-11 (never an official format).

## The obligation

A structure in canton Ticino notifies **every guest to the cantonal police within 24 hours of the arrival** on the portal
<https://www.notifiche.ti.ch> (login + TOTP app; no API). The portal accepts a bulk upload at **«Notifiche → Invio»** (files `xml`,
`xlsx` or `csv`); it reads the file row by row and shows, per row, whether the notification was saved or why not.

CasaZen **transmits nothing**: it writes the file of an arrival day, the host uploads it on the portal and records the portal's
summary page as the receipt of the day (`POST api/properties/{id}/swiss/upload-receipt`, type `Police`, report date = arrival day).
A day is notified once a receipt of it is recorded.

## Sources (official)

| What | URL |
|---|---|
| Formats and mandatory fields | <https://www4.ti.ch/index.php?id=145950> |
| XML schema (`F-Modello_4.xsd`) | `https://www4.ti.ch/fileadmin/DI/POLIZIA/documentazione/autorizzazioni/notifiche/F-Modello_4.xsd` |
| XML model (`E-Modello_3.xml`) | same folder, `E-Modello_3.xml` |
| CSV model (`D-Modello_2.CSV`) | same folder, `D-Modello_2.CSV` |
| Excel model (`C-Modello_1.xlsx`) | same folder (not written by CasaZen) |
| User manual of the portal (v1.4, 18.02.2025) | §5.1 manual entry, §5.2 bulk upload, §5.2.1 errors |
| Structure identifier | Ticino Turismo registration, <https://www.ticino.ch/it/partners/alloggi/notifica-numero-identificativo.html> |

Copies of the XSD and of the CSV model are test fixtures (`Casazen.Tests/Fixtures/ticino/`, kept byte for byte by `.gitattributes`).

## Mandatory fields (official page)

Identificativo utente (examples `XNA9999`, `NL-99999999`), Cognome, Nome, Data di nascita completa, Data di arrivo, Data di partenza,
Nazionalità (ISO 3166-1 alpha-3, `CHE`, `ITA`), Tipo documento (Passaporto / Carta identità / Altro). Nazione (country of
residence, alpha-3) is optional; the other fields are optional.

Portal checks (manual §5.2.1): birth date between 01.01.1900 and today; arrival and departure not before 01.01.2000; an unknown
user id gives «Struttura sconosciuta o XNA attribuito a diverse strutture» and the manual says it can be replaced by the
LEAR / NO-LEAR number of the structure.

## Who is a row (manual §5.1, format page)

- One notification per **adult** guest (18 or more at the arrival).
- Minor children travelling with a parent are counted on **one** parent's row in «Mag14» (14 to 17) and «Min14» (under 14); the
  notified person is never counted there. An adult child is notified separately.
- CasaZen counts the minors on the head of family or group of the stay, otherwise on its first adult. A stay without adults
  notifies each minor. (Assumption: a 14-year-old counts in «Mag14».)

## XML (`F-Modello_4.xsd`)

Root `<registrationForms>` with `<registration>` elements; every one of the 18 children is required, in this order:
`userId` (string), `firstname`, `lastname`, `birthdate` (`xs:date`, `yyyy-MM-dd`), `nationality` (`[A-Z][A-Z][A-Z]`),
`arrivaldate` (`xs:date`), `placeofbirth`, `street`, `zip` (**`xs:integer`**), `city`, `nation` (**`[A-Z][A-Z][A-Z]`**),
`departuredate` (`xs:date`), `persover14` (integer), `persunder14` (integer), `documentType` (`Passport` | `Identity card` |
`Other`), `documentNumber`, `carid`, `referenceNr`. UTF-8.

Two facts of the published XSD that the format page does not say:

1. `<registration>` has **no `maxOccurs`**, so the schema as published allows **one** registration per file, although the root is
   plural and the portal reads files "line by line". CasaZen writes all the rows of the day in one file; the test validates each
   registration against the XSD and states that the file with several is refused by the schema as published. **Not verified on the
   portal**: if it refuses a multi-row XML, use the CSV.
2. `zip` must be a whole number and `nation` a 3-letter code, with no empty value allowed. A foreign postcode with letters
   (`SW1A 1AA`) or an unknown country of residence cannot be written in XML: CasaZen then offers the CSV only
   (`ticino_xml_not_available`).

## CSV (`D-Modello_2.CSV`)

Header (18 columns, semicolon-separated, exactly):

```
Id Struttura;Cognome;Nome;Data di nascita;Nazionalità;Data di arrivo;Data di partenza;Luogo di nascita;Numero bollettino;Indirizzo;CAP;Domicilio;Nazione;Numero autovettura;Mag14;Min14;Tipo documento;Numero documento
```

- Encoding **ISO-8859-1** (the model has `à` as byte `0xE0`), lines ended by CRLF, no line break after the last row (as in the model).
- Dates `dd.MM.yyyy`; document types `Passaporto`, `Carta identità`, `Altro`.
- CasaZen replaces a semicolon inside a value with a comma, a double quote with an apostrophe, and a character ISO-8859-1 cannot hold
  with the same letter without its accent (`Ł` → `L`) or `?`.

The CSV is the **default** file: it has none of the XML limits above.

## Where CasaZen takes each field

| Field | CasaZen source |
|---|---|
| Identificativo utente / `userId` | `Property.TicinoStructureId` («Numero identificativo LEAR / NO-LEAR», entered by the host, normalized uppercase) |
| Cognome, Nome | `StayGuest.LastName`, `FirstName` (the check-in rows; the booker alone while there are none) |
| Data di nascita | `StayGuest.DateOfBirth` |
| Nazionalità | `StayGuest.CitizenshipCode` (Alloggiati «Stati» code) or `CitizenshipName`, mapped to alpha-3 by `TicinoCountryCodes` |
| Data di arrivo / partenza | `Booking.CheckInDate` / `CheckOutDate` |
| Luogo di nascita | `BirthComuneName` (born in Italy) or `BirthCountryName` |
| Indirizzo, CAP | the linked guest record (`Guest.Address`, `Guest.PostalCode`), when the row is linked to one |
| Domicilio | `ResidenceComuneName` (living in Italy) or `Guest.City` |
| Nazione | `ITA` when living in Italy, else `ResidenceCountryCode` / `ResidenceCountryName` mapped to alpha-3 |
| Mag14, Min14 | the minors of the stay, on one adult (see above) |
| Tipo documento | `StayGuest.DocumentTypeCode` (`PAS*` passport, `IDENT`/`IDELE`/`CIDIP` identity card, other codes «Altro»), else `DocumentType` |
| Numero documento | `StayGuest.DocumentNumber` |
| Numero autovettura, Numero bollettino | empty (CasaZen does not collect them) |

`TicinoCountryCodes` maps the 236 states of the committed Alloggiati table: generated once by matching their Italian names with the
CLDR/ICU region names, reviewed by hand. Not mapped: APOLIDE, BOPHUTHATSWANA, CECOSLOVACCHIA (no country). KOSOVO has no ISO code
and is written `XKX` (the user-assigned code used by the EU): to confirm on the first real upload.

## When a guest misses data

A stay with a guest that misses a mandatory field (or with fewer guests recorded than the booking counts) is **left out of the file
whole**, and the API lists per guest what is missing (`firstName`, `lastName`, `birthDate`, `nationality`, `documentType`; `guests`
for the stay). A minor counted on an adult needs neither nationality nor document. The file never holds a row the portal would
refuse for a missing mandatory field.

## API

| Route | What |
|---|---|
| `GET api/properties/{id}/swiss/ticino-notification?date=yyyy-MM-dd` | the arrival day: rows, issues, `xmlAvailable`, receipts, deadline, the days still to notify (no `date`: the oldest pending day of the last 14, else today) |
| `POST api/properties/{id}/swiss/ticino-notification` `{ reportDate, format: "csv" \| "xml" }` | the file (attachment), never stored |
| `POST api/properties/{id}/swiss/upload-receipt` | the receipt of the portal (type `Police`, report date = arrival day) |
| `GET api/compliance/summary` → `ticinoNotifications` | one `NotifyTicinoPolice` item per Ticino property and arrival day still to notify, the day in `period` |

Errors: `property_not_in_ticino`, `ticino_structure_id_missing`, `ticino_notification_empty`, `ticino_xml_not_available`,
`ticino_file_format_invalid`.

## Not verified

- A real upload on notifiche.ti.ch (it needs the owner's login and TOTP): the multi-row XML, the CSV encoding and the empty optional
  columns are as the official models show them, not tried on the portal.
- What the portal accepts for Kosovo and for stateless guests.
