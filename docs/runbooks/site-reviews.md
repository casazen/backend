# Verified reviews of the booking site (DB-12a)

A guest who stayed through the booking site can review the stay once. The review is a mark from 1 to 5 stars and an optional text, it is
published as soon as the guest sends it, and it shows on the page of the property with the first name of the guest (or «Ospite»). The
host answers once and can report a review; **it cannot hide or delete one**. Only the staff takes a review down, after a report.
There is **no feature flag**: the endpoints are always on. No review exists until a host asks a guest for one.

## What «verified» means

One thing: the booking is a **direct** one (`Source = Direct`, the public checkout), it is **confirmed** (not cancelled, not pending), its
stay is over (the Europe/Rome day after the check-out day or later) and the review is written within **14 days** after the check-out day
(decision DB-D12, default). It does **not** prove that the guest arrived (a no-show is not detected). `ReviewRules.Evaluate` is the only
place that decides it.

## The invitation: only when the host asks

`POST api/bookings/{id}/review-invite` (`booking.write`, scope of the property) sends **one email in Italian** to the guest with a signed
link `/book/{orgSlug}/booking/{id}/review?token=…`. That call is the **only** thing that writes to the guest: no job, no reminder, no
automatic email after a check-out. Once per booking (`ReviewInvites.BookingId` is unique, written before the email is queued; if the
email cannot be queued the mark is taken back and the host may ask again). `GET` on the same route tells the host whether it can ask
(`canInvite`, `reason`), when it asked and whether the guest already reviewed.

The token (`ReviewLinkTokens`) is made by ASP.NET Core Data Protection, purpose `Casazen.Reviews.InviteLink.v1`, time limited to the end
of the window, and carries only the booking id. Nothing is stored for it. A link that is not ours, is expired or is for another booking
gets the same 422 `review_link_invalid`. The token travels in the **body** of the calls, never in the address.

## Endpoints

| Who | Route |
|---|---|
| Public | `GET api/public/orgs/{slug}/properties/{idOrSlug}/reviews?page&pageSize` (policy `PublicRead`); `ratingSummary { count, average }` in `GET …/properties/{idOrSlug}`, **absent** while there is no review |
| Guest, with the link | `POST api/public/bookings/{id}/review/lookup` body `{token}` (policy `PublicBookingLookup`); `POST api/public/bookings/{id}/review` body `{token, rating, text?, showName}` (policy `PublicReviewSubmit`, 10 / 10 min) |
| Host | `GET api/reviews?propertyId&page&pageSize`; `POST api/reviews/{id}/reply` (once); `POST api/reviews/{id}/report` (reason from a list, note); `GET/POST api/bookings/{id}/review-invite` |
| Staff (`AdminOnly`) | `GET api/admin/reviews/reports?status=`; `POST api/admin/reviews/reports/{id}/decide` `{decision: Keep \| Remove, reason}` |

Errors: 409 `review_already_submitted`, `review_reply_already_sent`, `review_already_reported`, `review_invite_already_sent`,
`review_report_already_decided`, `review_not_available`; 422 `review_link_invalid`, `review_invite_not_allowed`,
`review_invite_not_sent`, `review_rating_invalid`, `review_text_too_long`, `review_reply_invalid`, `review_decision_reason_invalid`.

## Privacy and GDPR

- The public reads the first name (only the first word of the field, or «Ospite» when the guest did not agree), the mark, the text, the
  month of the stay and the host's answer. No surname, email, country, date or booking id. The text is plain text: it is shown as text.
- A **report** keeps the review public until the staff decides; the host sees the state `Reported`. One report per review, ever.
- Erasing or anonymizing a guest (`GuestDataEraser`, from the host's action and from the retention job) sets the name of its reviews to
  «Ospite»; the mark and the text stay. The export of the guest's data does not list the reviews yet (follow-up).
- Logs carry ids, never names, texts or addresses.

## Before the first real review in production

The texts below are **drafts** and need the opinion of the legal adviser (decision DB-D12): the legal basis of the invitation email, the
page «come verifichiamo le recensioni», and the sentence of the email that explains how the review is published. No sample review is ever
seeded in production.
