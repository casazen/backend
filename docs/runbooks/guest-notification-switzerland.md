# Runbook: notification of the guests to the police in Switzerland (Ticino)

**Issue:** casazen/backend#979 · **Requirements:** NF-126 (complete Ticino file), NF-132 (Swiss data at the check-in) · **Format of the file:**
[`docs/compliance/ticino-notification-format.md`](../compliance/ticino-notification-format.md) · **Analysis:** `alloggiati-ch/analisi-tecnica.md`
(decisions D1-D16) and `alloggiati-ch/specifica-ticino.md` (verified rules).

## 1. The flow

1. A property is in canton Ticino when its country is `CH` and its Swiss municipality belongs to canton `TI`
   (`ITicinoNotificationService.IsPropertyInTicinoAsync`, the one check of the jurisdiction).
2. The guests fill the online check-in (or the host fills the guests in the wizard). For a Ticino property the check-in asks, on top of
   the Alloggiati record: the **document type and number of every adult** (family and group members included; every minor of a stay
   without adults), the **home address** (street, postal code, town) and the state of residence of each guest (optional), and the **plate
   of the car** once for the stay (optional). The public context says it with `requiredGuestFields` and `askVehiclePlate`.
3. Within 24 hours of the arrival the host opens the day (`GET api/properties/{id}/swiss/ticino-notification?date=`), sees who is in the
   file and what the others miss, and downloads the file (`POST …/ticino-notification`, CSV by default). Nothing is stored.
4. The host uploads the file on <https://www.notifiche.ti.ch> («Notifiche → Invio», login and one-time code: CasaZen never logs in,
   decision D1) and records the receipt of the portal (`POST …/upload-receipt`, type `Police`, report date = the arrival day).

## 2. Configuration

| Key (environment variable) | Default | Values | Without it |
|---|---|---|---|
| `GuestNotifications:TicinoCsvEncoding` (`GuestNotifications__TicinoCsvEncoding`) | `windows-1252` | `windows-1252`, `utf-8` (case-insensitive) | the default; any other value stops the application at startup (`ValidateOnStart`) |

The committed `appsettings.json` has the default. Nothing else is needed: no flag (the file completes a flow already in `develop`), no
secret (CasaZen keeps no credential of the portal).

## 3. The encoding of the CSV

- **Windows-1252** (default, decision D8 ⚖ to confirm with the police): the official model `D-Modello_2.CSV` is single-byte (its «à» is
  the byte `0xE0`), and CasaZen writes the header of the model byte for byte. A character Windows-1252 cannot hold is written without its
  accent (`Ł` → `L`, `ș` → `s`, `ř` → `r`) or, as the last resort, `?` (a Cyrillic, Greek or Chinese letter): never a broken byte.
- **UTF-8** (`GuestNotifications__TicinoCsvEncoding=utf-8`): every letter as typed, no byte order mark. Use it only if the portal shows
  broken accents with Windows-1252 (see § 4).
- The day says, before the download, the encoding (`csvCharset`) and how many characters of its rows the encoding cannot hold
  (`transliteratedCount`), so the page can warn before the host uploads the file. The `charset` of the content type of the file says the
  same.

## 4. Verify the first real upload

There is no test environment of the portal: the first upload is a real notification. Do it with a real arrival, never with invented
guests (it would notify invented persons to the police).

1. **Accents.** Choose an arrival whose guests have accented names (`à`, `é`, `ü`). After the upload open the notification on the portal
   («Ricerca») and compare the names. Broken letters (`Ã©` instead of `é`): the portal reads UTF-8; set `GuestNotifications__TicinoCsvEncoding=utf-8`
   on Railway, redeploy, and check the next arrival. Letters fine: keep the default and write the result in
   `alloggiati-ch/specifica-ticino.md` § 4 (question 1).
2. **Duplicates.** The manual says nothing about a file loaded twice. Never upload the same day twice to find out: if a day must be uploaded
   again (a guest added late), check on the portal («Ricerca», the code of the booking is in «Numero bollettino») whether the guests already
   there are listed twice, and delete the duplicates on the portal.
3. **Optional columns.** Check that a guest without address was accepted (the portal's summary of the upload says per row «salvata» or the
   error), and that «Numero bollettino» and «Numero autovettura» are shown on the notification.

## 5. Data protection

- The home address of the guests and the plate are personal data that nothing searches: they are encrypted at rest with the document
  (`EncryptedColumns`, purpose `Casazen.Guest.Document`), never written in a log, and erased with the guest (anonymization, the
  retention of the Alloggiati data). No image of a document is kept for a Swiss property (decision D6).
- The file is rebuilt on every request and never stored; its download is never cached (`Cache-Control: private, no-store`) and asks
  `guest.read`.
