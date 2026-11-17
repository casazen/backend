# Property country and onboarding assistance

Issue #910 showed the staff assistance checklist for an ISTAT connection. The checklist used credential presence as the main instruction and displayed Italian police and statistics requirements for properties abroad.

The checklist now exposes the property's country (never credential values), shows an explicit short procedure for the requested task and omits the Italian police/statistics/sender requirements and panel-step placeholders outside Italy. A credential that is not required must never be displayed as missing. The API refuses creating or reopening a property-specific Italian police/statistics request, or starting its panel step, when the property is outside Italy. Organization-wide help remains available.

The property documents screen and amenities wizard follow the same distinction. A known foreign country does not receive the Italian safety checklist reminder or missing Italian municipality warning. An unknown country remains an explicit activation blocker. Removing an Italian requirement does not verify compliance with foreign requirements.

## Sources checked on 2026-11-17

- Repubblica e Cantone Ticino, guest notifications (non-LEAR): https://www4.ti.ch/di/pol-new/servizi-online/notifiche-degli-ospiti-alla-polizia-no-lear
- Repubblica e Cantone Ticino, guest notifications (LEAR): https://www4.ti.ch/di/pol-new/servizi-online/notifiche-degli-ospiti-alla-polizia-lear
- Ticino Turismo, accommodation registration and identification number: https://www.ticino.ch/it/partners/alloggi/notifica-numero-identificativo.html

The Ticino police pages require electronic guest notifications within 24 hours for the described accommodation categories and describe registration/access routes. The link shown for Swiss properties is explicitly labelled Ticino: it does not assert that all Swiss cantons use that system. CasaZen does not certify completion of these local obligations.

This change builds on #905's distinction between Italian CIN and Swiss registration. It does not change who is allowed to view credentials, the reason/audit requirement, or the one-minute reveal window.
