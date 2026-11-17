# Swiss accommodation registration and the Italian CIN

Issue: https://github.com/casazen/backend/issues/905

The Italian CIN does not apply to CH properties. The activation API omits the CIN step, the Italian CIN report excludes CH properties, and channel publication must not apply the Italian CIN requirement to them. This exemption does **not** verify or waive cantonal registration.

For Lugano / Ticino the host must obtain the cantonal identification number and display it in online and offline listings (LTur art. 21a). Lear authorisation may also apply; requirements depend on capacity, rental duration and services. Other Swiss cantons must be checked individually. CasaZen displays an official registration link rather than treating an absent CIN as proof of Swiss compliance. A dedicated cantonal identifier field and validation are outside this fix; hosts must enter the number on their publishing platforms.

Sources verified 2026-11-17:
- Official cantonal law, LTur art. 21a: https://www3.ti.ch/CAN/RLeggi/public/index.php/raccolta-leggi/legge/num/463
- Ticino Turismo registration instructions: https://www.ticino.ch/it/partners/alloggi/notifica-numero-identificativo.html
- Federal housing office summary: https://www.bwo.admin.ch/it/esempio-ticino
