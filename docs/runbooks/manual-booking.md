# Manual booking: availability preview and light creation (SR-06a)

What the host does when a guest books by phone or in person: first the console says whether the dates are free, then the booking is
created with only the name and the email of the guest. Two endpoints, one migration, no email, no payment.

| Endpoint | Permission (TN-3) | What it does |
|---|---|---|
| `GET /api/properties/{id}/availability?from&to[&channel]` | `booking.write` on the property | the state of the stay, what takes the nights, the nearest free window |
| `POST /api/bookings/manual` | `booking.write` on the property | creates the booking (by direct contact), or completes a portal's block |

`POST /api/bookings` (the whole contact of the guest: surname, phone and country required) and `POST /api/bookings/quote` are unchanged.
A property of another org, or one the caller does not reach (a collaborator limited to some properties), is **404** on both endpoints,
as for the host quote.

## 1. Availability (`GET /api/properties/{id}/availability`)

`from` is the arrival day and `to` the departure day (`yyyy-MM-dd`, years 2000-2100, the departure is not a night); `channel`
(`Airbnb`, `BookingCom`, `Other`) is optional. 400 `bad_request` without valid dates or with an unknown channel; 422
`property_not_bookable_in_long_mode` for a property in long-term mode (PM-01).

```json
{ "propertyId": "…", "from": "2026-11-10", "to": "2026-11-13", "state": "occupied", "invalidReason": null, "nights": 3,
  "minNights": 2,
  "conflicts": [ { "id": "…", "type": "booking", "title": "Giulia Bianchi", "channel": "Direct", "from": "2026-11-11", "to": "2026-11-14" } ],
  "nearestFreeWindow": { "from": "2026-11-14", "to": "2026-11-17" }, "linkableBlockId": null }
```

| `state` | Meaning | Other fields |
|---|---|---|
| `ok` | the nights are free and the stay is not shorter than the minimum | — |
| `belowMinimum` | free, but shorter than `minNights` (DB-03): the host may go on, the booking is theirs | `minNights` |
| `occupied` | at least one night is taken | `conflicts`, `nearestFreeWindow` (null when there is none close) |
| `linkableBlock` | the only thing on exactly these dates is a portal's reservation that arrived as an iCal block and is not a stay yet | `conflicts` (the block), `linkableBlockId` |
| `invalid` | `invalidReason`: `checkOutNotAfterCheckIn`, `checkInInPast` (before today in Europe/Rome), `stayTooLong` (over 366 nights) | — |

`conflicts[].type` is `booking` (a stay of CasaZen: the booking site, the host, or a portal's reservation the host turned into a stay),
`ical` (a portal's block: `title` is the name of the calendar) or `block` (the host's own block: `title` is the note, null if none);
`channel` is `Direct`, `Manual`, `Airbnb`, `BookingCom` or `Other` (null for the host's own block). The title of a booking is the name of
its guest: the caller has `booking.write` on the property, so they read its bookings anyway.

**One definition of "taken".** The answer adds no rule of its own: a booking takes its dates as `CheckoutHolds.OccupiesDates` says (a
cancelled booking and an expired checkout hold do not, a "pay at the property" request does) and the nights it and the calendar blocks
take are `PropertyOccupancy` (departure day free for a new arrival; a block that became a stay counts through the stay), the same as
the host calendar (`GET /api/calendar`), the iCal export, the public availability and the final check that refuses a booking on taken
dates. The only difference is timing: an expired hold is shown free here and released (or kept, if its guest paid meanwhile) by that
check.

**Nearest free window.** The stay of the same number of nights nearest to the arrival asked: that same arrival, then one day before and
one after, two and two, up to 40 days either way (`HostAvailabilityRules.NearestFreeWindow`), never arriving before today; of two equally
near, the earlier. Read from the same rows as the conflicts (two queries in all).

## 2. Creation (`POST /api/bookings/manual`)

```json
{ "propertyId": "…", "checkInDate": "2026-11-10", "checkOutDate": "2026-11-13", "adults": 2, "children": 0, "childrenAges": [],
  "guest": { "firstName": "Maria", "lastName": "De Luca", "email": "maria@example.com", "phone": null, "country": null },
  "note": "Arriva dopo le 20", "totalPrice": 300.00, "priceNote": "Concordato al telefono", "blockId": null, "source": null }
```

* **Required:** property, dates, `adults` (1-100), `guest.firstName` (2-100) and `guest.email`. The last name, phone and country are
  optional **on this path only**: the guest row is stored with empty strings, which the database accepts (no column became nullable). The
  console splits "Nome e cognome" at the first space (first token = first name, the rest = last name). The documents and the data of all
  the guests are given by the guest with the online check-in: the answer says `guestDataComplete: false`.
* **Direct contact (no `blockId`):** created `Confirmed` with the source `Manual` by `IBookingService.CreateManualBookingAsync`, as the older
  endpoint does. Price, cleaning and tourist tax come from `PriceHostStayAsync` (nightly rates, weekend surcharge, hosts' seasonal prices,
  tourist tax of the comune: never an estimate typed by the client). A check-in in the past is 422 `booking_create_invalid`; more guests
  than the property allows 422 `booking_too_many_guests`; the ages of the minors missing 422 `tourist_tax_child_ages_required`.
* **Dates taken: 409 `booking_dates_unavailable`.** The preview is advice; the real check is the one inside the transaction that inserts
  the booking, under the advisory lock of the property (two requests on the same dates cannot both succeed; the guest row of the loser is
  removed again). It looks at bookings and calendar blocks.
* **Minimum stay:** a stay shorter than `minNights` is created all the same (the host decides, as for the older endpoint) and the answer
  says `belowMinimum: true`.
* **Agreed price (`totalPrice`, `priceNote`).** Without it, or equal to the total of the quote, the booking has the price of the quote.
  Otherwise `TotalPrice` is what the host typed (rounded to the cent), `TouristTax` stays the **tax of the quote**, `BasePrice =
  totalPrice − tax` (the figure the Home and the fiscal reports read; between 0 and 100,000 EUR, else 422 `booking_price_override_invalid`),
  `CleaningFee` is the quote's as far as it fits in `BasePrice` (so `BasePrice − CleaningFee` is never negative), `PriceOverridden = true`
  and `PriceOverrideNote` is the note (trimmed, at most 80 characters, kept only with an agreed price). A host who then changes the dates or
  the guests of the booking (`PUT /api/bookings/{id}`) gets the price of the quote again and `PriceOverridden` goes back to false: the
  agreed price does not follow a different stay.
* **Completing a portal's block (`blockId`).** The id comes from the availability answer (`linkableBlockId`). The stay is created by
  `IOtaStayService.ConvertBlockAsync`, exactly as `POST /api/ical-blocks/{id}/ota-stay` does (source of the feed's channel, the block stays
  linked and the later syncs only mark the stay "da verificare", see `ical.md`): the dates are the block's (other dates: 422
  `booking_block_dates_mismatch`), a block of another property or org is 404 `ical_block_not_found`, a block already converted 409
  `ota_stay_block_already_converted`. No quote, no tax and no payment: the portal collects, `totalPrice` is the amount the host reads on the
  portal (optional, never made up) and `priceNote` is not used. `source` is needed only for a calendar that is not Airbnb or Booking.com
  (422 `ota_stay_source_required`). A portal's reservation **with no block** is not created by hand (decision SR-D29: the iCal import would
  count it twice): the console explains it and points to "Collega il calendario".
* **201** with the booking (as every booking answer, plus `priceOverridden` and `priceOverrideNote`), `belowMinimum`, `minNights` and
  `guestDataComplete`.

**Nothing is sent to a third party.** No email or push to the guest, no payment request: the check-in link and any payment start from an
explicit action of the host (`POST /api/bookings/{id}/checkin/resend-link`, SR-06b).

## 3. Data

Migration `AddBookingPriceOverride`: `Bookings.PriceOverridden boolean NOT NULL DEFAULT false` and `Bookings.PriceOverrideNote
varchar(80) NULL`. Existing rows get `false`/`NULL`; nothing is backfilled or invented. Rollback: `dotnet ef migrations remove` before
deploy, or the `Down` of the migration (drops both columns; the agreed prices stay in `BasePrice`/`TotalPrice`).

Demo showcase (`DemoShowcaseCatalog`, scenario `booking-manual-light`): a booking of Villa del Lago entered by hand, without a last name,
with an agreed price and its note.

## 4. Checks after a deploy

1. `GET /api/properties/{id}/availability?from=…&to=…` with dates on an existing stay answers `occupied` with its guest as `title`; on
   free dates `ok`; with the arrival in the past `invalid`.
2. `POST /api/bookings/manual` with only the name and the email answers 201 `guestDataComplete: false`; the same dates again, 409.
3. With `totalPrice` below the tourist tax the answer is 422 `booking_price_override_invalid`; above it, `touristTax` in the answer is the
   one of `POST /api/bookings/quote` for the same stay.
