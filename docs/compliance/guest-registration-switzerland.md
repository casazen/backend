# Registration of the guests: the regimes of Italy and of the 26 cantons

**Issue:** casazen/backend#979 · **Requirements:** NF-125 (regimes with source and certainty), NF-129 (readiness of a Ticino property) · **Decisions:** D3 (one place decides the regime),
D5 (certainty and sources on every rule, an uncertain canton is a question, never a red alarm), D6 (no image of the identity document in Switzerland), D12 (information, register,
assisted notification), D14 (one data model for every channel) of `alloggiati-ch/analisi-tecnica.md` · **Research read on:** 2026-11-20 (Italy: 2026-10-01).

The law that asks a host to register the guests is **cantonal** in Switzerland: the Confederation fixes only a floor for **foreign** guests. This page is the catalog CasaZen
reads (`GuestRegistrationRegimeCatalog`), what it says, how sure we are, and how to change it. It is not legal advice and never says so: where a source is partial the card
says «verifica con il comune o la polizia».

## 1. How CasaZen decides the regime of a property

`IGuestRegistrationRegimeResolver` (`Casazen.Infrastructure/Services/GuestRegistrationRegimeResolver.cs`) is the one place that decides it:

1. **Country `IT`** (or none, the old rows): the regime `IT`, the existing Alloggiati flows, unchanged. No place, no setting of the host.
2. **Country `CH`**: the place resolver of E29/F01 (`IPropertySwissPlaceResolver`) gives the municipality and the canton: stored by the host, else derived from the postal
   code and the city. A place that is **not known** (no municipality, or several left open) is `CH-UNKNOWN`: the resolver never chooses a canton silently. Otherwise the regime of
   the canton, `CH-TI` ... `CH-ZH`.
3. **Another country**: no regime (`null`); the endpoint answers 422 `guest_registration_country_not_supported`.

The Ticino code of PR #997 (`TicinoNotificationService`) reads the canton from the same place resolver, so the two never disagree; it keeps working as before.

### The duty in effect

| The host's setting (`Properties.GuestNotificationOverride`) | The duty |
|---|---|
| `Required` | **Required** |
| `NotRequired` | **NotRequired** |
| none (`null`, the default) | the default of the regime: **Required** for Ticino and for a regime that is **verified** as an obligation to *report to an authority* that **covers private rentals** (Italy, Schaffhausen, Zug); **Ask** for every other canton |

`Ask` means the sources do not settle it for a private rental: the host answers «Devo notificare» or «Non devo notificare» (reversible, never blocking) and nothing is raised in red
meanwhile. The setting is the host's own answer or its own exemption; it never decides what the law asks. It is for Swiss properties only (an Italian property answers 422
`guest_registration_override_not_applicable`) and it is **not read while `Features:GuestRegistrationRegimes` is off**.

## 2. The certainty

| Certainty | Meaning |
|---|---|
| `Verified` | the research read it on an official source and gives none of the facts that make the entry (obligation, who must comply, channel, deadline) as partial |
| `Partial` | reported by a summary, by a tourism body or by a text the research could not read in full, or one of those facts is open |
| `Unverified` | not found (only `CH-UNKNOWN`: nothing is assumed about an unknown canton) |

A fact the research leaves «non verificato» is written as such (`Unknown`, no deadline with the note «la scadenza non è verificata», no retention): **an article number or a deadline is never
invented**. The efficacy of the rule on a private rental is a separate column (`PrivateRentals`: `Yes`, `Partial` = only some guests or some hosts, `No`, `Unknown`).

## 3. The catalog

`Obligation`: **Report** = the host transmits the data to an authority; **Register** = the host keeps a register to show to the police; **Optional** = the register is a faculty.
`Support` = what CasaZen does: **File** (writes the file of the portal), **Register** (the guest register to download, E31), **Assisted** (prepares the data and the deadline of a notification the
host makes itself, E31), **Info**. Sentences for the user: `SharedResources` keys `GuestRegime_<code>_Summary` (Italian and English). The sources are in the catalog (`Sources`, `LegalReferences`).

| Code | Obligation | Channel | Deadline | Retention (years) | Private rentals | Certainty | Support | Notes (what the research leaves open) |
|---|---|---|---|---|---|---|---|---|
| `IT` | Report | portal file upload (Alloggiati Web) | 24 h from arrival (6 h for stays up to 24 h) | – | Yes | Verified (2026-10-01) | File | Art. 109 TULPS; the existing flows |
| `CH-AG` | Register | none | none | 5 | Unknown | Partial | Register | recipient and private rentals not verified; law read through summaries |
| `CH-AI` | Optional | none | none | – | Yes | Verified | Register | art. 40 cpv. 2 read in the law (state 1.1.2021); the federal duty for foreign guests remains |
| `CH-AR` | Register | none | none | 5 | Unknown | Partial | Register | for which guests (all or only foreign) not clear; law not read |
| `CH-BE` | Register | none | not verified | 5 | Unknown | Partial | Register | a police platform «may» be offered (PolV art. 58 cpv. 2): which one not found |
| `CH-BL` | Register | paper | not verified | 10 | Unknown | Partial | Register | the 10 years are to be double-checked; a 2014 text had the sending within 24 h |
| `CH-BS` | Report | portal | every day | – | Unknown | Partial | Assisted | cantonal electronic system; articles, login and private rentals not verified |
| `CH-FR` | Report | portal (the tourism union's platform) | not verified | – | Unknown | Partial | Assisted | whether the platform replaces the paper form of the police not said |
| `CH-GE` | Report | portal (cantonal account) | at arrival (privates), every day (hotels) | – | Partial | Partial | Assisted | privates: foreign guests under 90 days (an old page said 60); a reform is under discussion |
| `CH-GL` | Register | none | none | 3 | Unknown | Partial | Register | law read through summaries |
| `CH-GR` | Register | paper | none | 1 | Unknown | Partial | Register | only foreign guests; the sending within 24 h was abolished |
| `CH-JU` | Register | none | not verified | – | No | Partial | Register | holiday lets without hotel services excluded; the form and the ordinance not verified; the police PDF form has a send button and the page does not say who receives it |
| `CH-LU` | Register | none | none | 5 | Partial | Partial | Register | private rentals: foreign guests (cantonal and city pages) |
| `CH-NE` | Report | portal (Guichet unique) | every day | – | Partial | Partial | Assisted | holiday lets: daily sending not confirmed by the text, a report says it is due |
| `CH-NW` | Report | paper (or electronic data) | at least weekly | – | Partial | Partial | Assisted | the electronic format and channel are asked of the police |
| `CH-OW` | Register | none | not verified | – | Unknown | Partial | Register | a revision (12 h, 1 year, electronic sending) is under way: whether it is already in force is not verified |
| `CH-SG` | Register | portal (the tourism body's system) | none | 5 (foreign guests) | Unknown | Partial | Register | private rentals not stated by the sources; nothing is sent to the police |
| `CH-SH` | Report | portal | every day by 02:00 | – | Yes | Verified | Assisted | official brochure of 11/2023; which day the hour refers to is ambiguous |
| `CH-SO` | Register | none | none | 3 | Partial | Partial | Register | private rentals when the activity is professional |
| `CH-SZ` | Register | none | none | 2 | Partial | Partial | Register | only foreign guests; 2 years is a recommendation |
| `CH-TG` | Report | e-mail file | not verified | – | Unknown | Partial | Assisted | the specification of the CSV and the code of the structure only through the police; the 5 years of the research are the deletion term of the police system, not the host's retention |
| `CH-TI` | Report | portal file upload | 24 h from arrival | 5 | Yes | Verified | File | the file of E30; the 5 years are those of the group sheets and the parental authorizations |
| `CH-UR` | Register | none | at arrival | – | Unknown | Partial | Register | an opinion of 2020 puts the retention at most 1 year |
| `CH-VD` | Register | none | none | – | Yes | Verified | Register | register with a copy of the document, at the disposal of the authority (the page names no police recipient): CasaZen does not keep document images, the copy stays with the host |
| `CH-VS` | Register | paper | not verified | – | Partial | Partial | Register | holiday lets without hotel services: the municipality (not confirmed) |
| `CH-ZG` | Report | portal | at arrival | – | Yes | Verified | Assisted | fields and retention not verified |
| `CH-ZH` | Report | interface of the management software | not verified | – | Partial | Partial | Assisted | «commercial» is not defined; the specification is not public; the 3 years of the research are the retention in the police system (text of 2014, partial), not the host's |
| `CH-UNKNOWN` | Unknown | none | – | – | Unknown | Unverified | Info | the municipality is missing or ambiguous |

**Retention.** The number is what the research gives as the years the host keeps the data; a recommendation (`CH-SZ`) is said in the sentence. A number that is how long a police system keeps the data is not an obligation of
the host and is left out (`null`): `CH-ZH` (3 years in the police system, text of 2014, partial), `CH-TG` (deletion 5 years after the departure) and `CH-UR` (an opinion of 2020: at most 1 year). **Images.** `RetainDocumentImages` is `false` for every Swiss regime (decision D6): only the transcribed
data are kept.

## 4. The federal frame

- **Base.** Article 45 of the Schengen Convention → article 16 LStrI (`AIG`) → article 18 OASA (`VZAE`): whoever lodges **foreign** guests for payment, a private person on a platform included, reports
  them to the cantonal authority (SEM report of 26.8.2026, read in full). The Confederation collects nothing and checks nothing; the cantons choose authority, channel and deadline. A canton may extend the duty to
  Swiss guests.
- **Consultation until 27.11.2026.** The amendment of the VZAE (motion 21.4426) would allow the electronic notification **without the signature of the guest** (the host checks the data against the
  document) and a path on **EasyGov from 2028**, **optional**, for the cantons without a digital system; Ticino, Basel-Stadt and Fribourg may keep their own. No national API for software is planned; the date
  of entry into force is not in the communiqué. It is a draft, not law in force: **the catalog does not change until the result is known.**
- **Standard.** `eCH-0290` «Meldeschein Beherbergungsbetriebe» v1.0.0 is «in work»: its fields are not read. The single data model of the guests (D14) leaves a new channel open.

## 5. How to update the catalog

1. Read the source again (the page of the authority, the text of the law) and change **the entry** in `GuestRegistrationRegimeCatalog` and its sentence in `SharedResources.resx` **and** `.en.resx` together.
2. Move `VerifiedOn` to the day you read it, keep `Certainty` at `Partial` or `Unverified` unless the source is the authority's own, and keep the doubt in the sentence.
3. Never name a supplier of software or of a cantonal system in a sentence or a title (the authorities and their portals may be named: «Polizia cantonale», «Guichet unique»).
4. Say what changed in this page and run `GuestRegistrationRegimeCatalogTests` (every canton of the municipality directory has an entry with an obligation, a channel, a certainty, a date and at least one source, and
   both languages have the sentence). The effective duty follows from the entry: only a `Verified` obligation to report that covers private rentals raises a duty by default.
5. Moving a canton to `Verified` + `ReportToAuthority` + `Yes` makes its hosts see a **required** duty: do it only on the word of the product owner.
6. The deadline of a `DailyByTime` regime has an optional time of day, `DeadlineTime` (Schaffhausen: 02:00, the day after the arrival): copy it from the source or leave it empty, never write a time nobody wrote
   (an empty time means no deadline: the day is listed in the cockpit with no due date and no alert is sent, as for a regime of kind `None`). The deadline of the assisted notification of a canton other than Ticino is computed from the kind and this time by `SwissAssistedNotification.DueAtUtc`
   ([`guest-notification-switzerland.md`](../runbooks/guest-notification-switzerland.md) § 25).

## 6. What the catalog does not hold

- **No connector.** CasaZen never logs into a police portal, never keeps a password or the seed of a one-time code and never simulates a browser (D1). The interfaces of the vendors of the cantonal systems have no
  public specification: none is called, none is named to the user.
- **No guest data.** The catalog and the card hold rules, not people.
- **No other canton than the 26** and no other country than Italy and Switzerland.

Code: `Casazen.Core/Regulatory/GuestRegistration/*` (the regime, the catalog, the duty rules, the readiness), `Casazen.Core/Services/IGuestRegistrationRegimeResolver.cs`,
`Casazen.Infrastructure/Services/GuestRegistrationRegimeResolver.cs`, `Casazen.Web/Controllers/PropertyGuestRegistrationController.cs`. Runbook: [`guest-notification-switzerland.md`](../runbooks/guest-notification-switzerland.md) § 21-24.
