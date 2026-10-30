# Listino CasaZen: prezzi B2C e B2B, costi e margini

> **Documento generato** da `scripts/business-plan/listino.py`: non modificare a mano. Versione del piano 1.1.0,
> 15 ottobre 2026. Prezzi in euro, IVA esclusa; USD = EUR 1:1 per prudenza. I prezzi dei concorrenti sono
> quelli della ricerca di mercato (§ 6): ✓ = pagina ufficiale letta, [NV] = fonti terze 2026.

## 1. Il metodo

1. Ho calcolato i **costi fissi** della piattaforma in produzione e i **costi unitari** di ogni integrazione
   (channel manager, verifica d'identità, serrature, fattura elettronica, AI, email, hosting, pagamenti).
2. Ho fissato un **margine lordo obiettivo**: almeno il 50 % sulle unità incluse nei piani a pieno utilizzo,
   almeno il 40 % sulle unità in più e sulle opzioni, oltre il 70 % sui piani d'ingresso.
3. Ho confrontato il risultato con i listini 2026 di tredici concorrenti a 1, 3, 5, 10, 25, 50, 100 e 250 unità
   e corretto dove CasaZen risultava fuori mercato per eccesso o per difetto.
4. Ho verificato che B2C e B2B siano **coerenti**: a parità di unità il B2C costa meno del B2B (ha meno funzioni),
   e nessun cliente ha convenienza a restare nel piano sbagliato (nessun gradino all'indietro tra i piani).

## 2. Costi fissi mensili

| Voce | € / mese | Nota |
|---|---:|---|
| Hosting backend e database (Railway Pro + Supabase Pro) | € 85 | analisi Quokka § 5.1; VM Oracle Always Free + Supabase Pro ridurrebbe a ~€ 25 |
| Frontend Vercel Pro (uso commerciale) | € 20 | il piano Hobby non ammette uso commerciale |
| Email transazionali Resend Pro | € 20 | Scale € 90 oltre 50.000 email/mese |
| Channel manager NextPax, minimo mensile (50 unità incluse) | € 350 | € 3,15/unità oltre le 50, fatturazione annuale |
| Strumenti di sviluppo assistito (Claude Code Max) | € 200 | ricerca e sviluppo, non costo del venduto |
| Commercialista | € 150 | stima |
| Pareri fiscali e legali ricorrenti | € 100 | budget |
| RC professionale | € 50 | stima |
| Conto, PEC, firma digitale, bolli | € 30 | stima |
| **Totale al lancio (tutte le integrazioni attive)** | **€ 1.005** | di cui € 200 di sviluppo e € 350 di minimo channel manager |
| Totale a regime (+ email resend scale (oltre 50.000 email/mese), hosting a regime (più risorse)) | € 1.175 | dall'anno 2 |

Prima del primo cliente i fissi scendono a ~€ 205 (hosting minimo e strumenti di sviluppo): le integrazioni
restano in sandbox gratuita e il minimo del channel manager si firma solo con il primo cliente vicino.

## 3. Costi unitari delle integrazioni

| Voce | Costo | Fonte |
|---|---:|---|
| Channel manager via API (NextPax), per unità al mese oltre le 50 incluse | € 3,15 (annuale) / € 3,50 (mensile) | nextpax.com/nextpax-pricing ✓ |
| Verifica d'identità documento + volto (Didit) | € 0,33 / € 0,48 con NFC | didit.me/pricing ✓; Sumsub $ 1,35 con minimo $ 149/mese |
| Serratura smart (Seam), per dispositivo al mese | € 5,00 | seam.co/pricing ✓ |
| Fattura elettronica SDI (Invoicetronic, pacchetti prepagati) | € 0,10 a fattura | invoicetronic.com/pricing ✓ |
| Rendiconto e fattura al proprietario (B2B) | € 0,10 per unità | una fattura SDI per proprietario al mese |
| AI (DeepSeek su Bedrock UE): suggerimenti / risposte in bozza e routine / automatica + CRM | € 0,10 per host / € 0,30 per unità / +€ 0,35 per unità | $ 0,74/M token in ingresso, $ 2,22/M in uscita ✓ |
| Email transazionali (Resend) | € 0,05 per unità | resend.com/pricing ✓ |
| Hosting, database, storage | € 0,10 per unità | quota dei fissi a 1.000+ unità |
| Assistenza e onboarding | € 0,50 host Starter / € 1,00 Pro / € 2,00 Scale / € 0,50 per unità B2B | stima del tempo di customer success |
| Host del piano gratuito | € 0,45 per host | hosting, email, assistenza di base |
| Incasso delle nostre fatture: carta EEA + Stripe Billing + Stripe Tax | 2 % + € 0,25 + 1 % + 0 % | stripe.com/pricing [NV]; B2B annuale via SEPA ~1 % |
| Video check-in (Daily), WhatsApp (Meta), pagoPA, commissioni Stripe sugli incassi dei clienti | 0 per CasaZen | gratis entro i limiti o a carico del cliente |

## 4. Listino B2C (host): prezzi, contenuto, margini

Prezzi mensili, IVA esclusa; **pagamento annuale −10 %**. Oltre 100 proprietà si passa al listino Agenzia.

| Piano | Prezzo | Annuale | Proprietà incluse | Ogni proprietà in più | Tetto | Persone | Cosa aggiunge |
|---|---:|---:|---:|---:|---:|---:|---|
| **Free** | € 0 | — | 1 | non previste | 1 | 1 | Marketplace fornitori con Garanzia, calendario iCal, pagina pubblica dell'immobile con richiesta di prenotazione "paga in struttura" approvata dall'host, cockpit compliance in sola lettura, app. Marchio "Powered by CasaZen" |
| **Starter** | € 29 | € 26 | 3 | non previste | 3 | 2 | Sito di prenotazione diretta con checkout Stripe sul proprio conto (sottodominio CasaZen), wizard di compliance (CIN, imposta di soggiorno, sicurezza), portale ospite e file Alloggiati, check-out guidato, suggerimenti stagionali, locazioni lunghe (contratti, RLI assistita, canone). Channel manager via API in opzione a € 4/unità |
| **Pro** | € 79 | € 71 | 10 | € 8 | 50 | 10 | Channel manager via API incluso (Airbnb, Booking.com, Vrbo, Expedia), dominio proprio senza marchio CasaZen, temi e branding, routine (libreria pronta), inbox con risposte AI in bozza (fase 2), dispositivi smart (serrature € 7/serratura), check-in biometrico opzionale (€ 0,75 a verifica) |
| **Scale** | € 199 | € 179 | 25 | € 7 | 100 | illimitate | Routine avanzate e AI automatica (fase 2), persone illimitate con ruoli, supporto prioritario, export e API |

### 4.1 Margine lordo per piano

| Piano | Utilizzo | Prezzo | Costo CasaZen | Margine | Margine % |
|---|---|---:|---:|---:|---:|
| Free | 1 proprietà | € 0 | € 0,45 | -€ 0,45 | — |
| Starter | 1 proprietà | € 29 | € 1,88 | € 27,12 | 94 % |
| Starter | 3 proprietà (pieno) | € 29 | € 2,18 | € 26,82 | 92 % |
| Pro | 5 proprietà (tipico) | € 79 | € 21,48 | € 57,52 | 73 % |
| Pro | 10 proprietà (incluse, pieno) | € 79 | € 39,48 | € 39,52 | 50 % |
| Pro | 24 proprietà (incluse + 14 extra) | € 191 | € 92,91 | € 98,09 | 51 % |
| Scale | 25 proprietà (incluse, pieno) | € 199 | € 97,72 | € 101,28 | 51 % |
| Scale | 50 proprietà (+ 25 extra) | € 374 | € 192,45 | € 181,55 | 49 % |
| Scale | 100 proprietà (tetto) | € 724 | € 381,90 | € 342,10 | 47 % |
| Starter + channel manager via API | 3 proprietà | € 41 | € 11,96 | € 29,04 | 71 % |
| Pro, una proprietà in più | marginale | € 8 | € 3,82 | € 4,18 | 52 % |
| Scale, una proprietà in più | marginale | € 7 | € 3,79 | € 3,21 | 46 % |

Lettura: Starter e Pro a utilizzo tipico stanno sopra il 70 %; Pro e Scale a pieno utilizzo tra il 50 e il 60 %
perché il channel manager via API (€ 3,15/unità) è incluso; le unità in più rendono il 43-50 %. Il piano Free costa
~€ 0,45 al mese per host: con il 10 % su un solo intervento da € 40 al mese (€ 4) si ripaga nove volte.

### 4.2 Perché questi prezzi

- **Free (1 proprietà)**: Hospitable regala proprietà illimitate (inbox, calendario, marketplace pulizie) e vende sito e
  automazioni; CasaZen regala l'ingresso nel marketplace, che è dove guadagna il 10 %. È il piano in cui entra l'host
  invitato dal suo fornitore. Un piano, non una prova a tempo: le prove scadono, il ciclo della rete no.
- **Starter € 29 (3 proprietà)**: in linea con Hospitable Host ($ 29) e Guesty Lite ($ 29/listing), sotto Smoobu (€ 35 per
  **una** proprietà) e Lodgify ($ 33-53 per rental). Include il sito diretto, che Hospitable vende da $ 59.
- **Pro € 79 (10 incluse + € 8)**: con il channel manager via API incluso costa meno di Kross Premium + Vikey (€ 15,90/unità)
  già da 6 unità, e meno di Smoobu, Lodgify, Hospitable e Hostaway a ogni taglia. Resta sopra Kross da solo (€ 6/unità),
  che però non ha compliance eseguita, fornitori, sito e portale ospite.
- **Scale € 199 (25 incluse + € 7)**: il passaggio Pro → Scale è conveniente esattamente a 25 proprietà (entrambi € 199) e
  Scale resta sotto il listino Agenzia (€ 8-10/unità) fino al tetto di 100: nessun gradino all'indietro.
- **Unità in più a € 8 e € 7**: sotto il Base B2B (€ 10/9/8) perché il B2C non ha portale proprietari, staff e compliance
  svizzera; sopra il costo marginale (€ 3,60-3,80) con margine 43-55 %.
- **Annuale −10 %**: lo sconto di mercato (Smoobu 10 %, Hospitable 12 %, Hostaway 10-20 %).

## 5. Listino B2B (agenzie): prezzi, opzioni, margini

Prezzi per unità al mese, IVA esclusa, con **pagamento annuale** (+10 % con pagamento mensile); minimo € 100 al mese; oltre 400 unità preventivo.

| Pacchetto | 10-49 | 50-149 | 150-399 | Cosa contiene |
|---|---:|---:|---:|---|
| **Base** | € 10 | € 9 | € 8 | Gestionale, channel manager via API con contenuti, chat e recensioni, inbox con risposte AI in bozza, sito diretto, check-in online, adempimenti via API (Alloggiati, ISTAT, feratel) e file pronti per quelli senza API, portale proprietari con rendiconti, fatture SDI per conto dell'agenzia e file di bonifico, routine, fornitori e staff propri, app |
| **+ AI e Marketing** | +€ 4 | +€ 4 | +€ 4 | Risposte AI inviate in automatico sopra una soglia di confidenza, routine create da una frase, CRM, campagne email e WhatsApp |
| **Tutto compreso** | **€ 15** | **€ 14** | **€ 13** | Base + AI e Marketing + **6 verifiche d'identità per unità al mese** |
| Check-in biometrico | € 0,75 a verifica | | | Documento e volto all'ingresso con alternativa in videochiamata; NFC +€ 0,20; nel B2B è il predefinito |
| Serrature smart | € 7 per serratura al mese | | | Codice per soggiorno rilasciato dopo l'identificazione; hardware escluso |
| Founding Partner | costo documentato + € 1/unità | | | Prime 10 agenzie, 24 mesi, pagamento annuale, caso di studio; verifiche al costo; costi fissi coperti dal partner |

### 5.1 Margine lordo per unità

| Pacchetto | Scaglione | Prezzo | Costo CasaZen | Margine | Margine % |
|---|---|---:|---:|---:|---:|
| Base | 10-49 | € 10 | € 4,40 | € 5,60 | 56 % |
| Base | 50-149 | € 9 | € 4,38 | € 4,62 | 51 % |
| Base | 150-399 | € 8 | € 4,36 | € 3,64 | 46 % |
| Base + AI e Marketing | 10-49 | € 14 | € 4,83 | € 9,17 | 66 % |
| Base + AI e Marketing | 50-149 | € 13 | € 4,81 | € 8,19 | 63 % |
| Base + AI e Marketing | 150-399 | € 12 | € 4,79 | € 7,21 | 60 % |
| Tutto compreso (6 verifiche) | 10-49 | € 15 | € 6,83 (€ 7,73 con NFC) | € 8,17 | 54 % (48 % NFC) |
| Tutto compreso (6 verifiche) | 50-149 | € 14 | € 6,81 (€ 7,71 con NFC) | € 7,19 | 51 % (45 % NFC) |
| Tutto compreso (6 verifiche) | 150-399 | € 13 | € 6,79 (€ 7,69 con NFC) | € 6,21 | 48 % (41 % NFC) |
| Verifica d'identità in più | — | € 0,75 | € 0,33 | € 0,42 | 56 % |
| Serratura smart | — | € 7 | € 5,00 | € 2,00 | 29 % |

### 5.2 Perché questi prezzi

- **Base € 10 / 9 / 8** (era € 9 / 8 / 7 nell'analisi Quokka): sopra Kross Premium (€ 6, solo gestionale + channel manager)
  e Octorate (€ 8/camera), sotto Avantio (€ 14 con minimo € 295), Lodgify Ultimate (~$ 19-31/rental), Hostaway ($ 24-40),
  Guesty Pro ($ 40-72). Il portale proprietari è incluso (Avantio lo include, OwnerRez lo fa pagare, Guesty $ 5-10/listing).
  Con € 9 / 8 / 7 il margine scendeva al 38 % nello scaglione alto, perché il costo unitario (€ 4,35) è per tre quarti il
  channel manager: con un euro in più per unità il margine sta tra il 46 e il 56 %, e le agenzie sotto le 10 unità
  restano meglio servite dai piani Pro e Scale.
- **Tutto compreso € 15 / 14 / 13 con 6 verifiche** (invariato nel prezzo): contro Kross Premium + Vikey Master (€ 15,90/unità)
  e Kross + Chekin Premium (€ 11,95, senza AI, CRM e portale proprietari) resta sotto o alla pari con molto più contenuto;
  le verifiche incluse scendono da 8 a 6 per unità al mese (tre prenotazioni da due adulti), le altre a € 0,75: così il
  margine sta tra il 48 e il 54 % invece del 37-45 %. Comprato a parte (Base + AI + 6 verifiche) costerebbe € 18,50-16,50:
  il pacchetto sconta il 19-21 % e spinge le agenzie verso il tutto compreso, che è dove CasaZen rende di più.
- **Verifica € 0,75** (costo € 0,33-0,48): meno della metà di Uplisting (£ 1,25) e di Hospitable ($ 2); Vikey e Chekin la
  vendono dentro un canone per unità (€ 4,90-9,90 e € 5,95).
- **Serratura € 7** (costo $ 5): nel mezzo del mercato ($ 3-8; Kross KDoor € 29/mese).
- **Minimo € 100**: dieci unità a € 10; sotto le 10 unità un'agenzia usa Pro o Scale.
- **Founding Partner**: prezzo = costo + € 1 per unità, margine bloccato. Per Quokka 360 (250 unità) il canone resta
  ~€ 1.730 al mese più le verifiche al costo, come nell'analisi del 15 ottobre.

## 6. Confronto con i concorrenti per numero di unità

Prezzo mensile stimato del pacchetto più vicino a CasaZen (gestionale + channel manager + sito), senza opzioni; CasaZen
nel piano più conveniente per quel numero di unità. Kross + Vikey e Kross + Chekin aggiungono il check-in con verifica.

| Unità | 1 | 3 | 5 | 10 | 25 | 50 | 100 | 250 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **CasaZen** (piano) | **€ 0** (Free / Starter) | **€ 29** (Starter) | **€ 79** (Pro) | **€ 79** (Pro) | **€ 199** (Scale) | **€ 374** (Scale) | **€ 724** (Scale) | **€ 3.250** (Agenzia tutto compreso (150-399)) |
| Kross Premium ✓ | € 18 | € 18 | € 30 | € 60 | € 150 | € 300 | € 600 | € 1.500 |
| Kross + Vikey Master ✓ | € 28 | € 48 | € 80 | € 159 | € 398 | € 795 | € 1.590 | € 3.975 |
| Octorate Basic [NV] | € 8 | € 24 | € 40 | € 80 | € 200 | € 400 | € 800 | € 2.000 |
| Smoobu Professional [NV] | € 35 | € 105 | € 175 | € 350 | € 875 | € 1.750 | € 3.500 | € 8.750 |
| Lodgify Professional/Ultimate [NV] | € 53 | € 159 | € 230 | € 307 | € 574 | € 968 | € 1.800 | € 4.000 |
| Hospitable Host/Professional [NV] | € 29 | € 74 | € 104 | € 179 | € 404 | € 779 | € 1.529 | € 3.779 |
| Guesty Lite/Pro [NV] | € 29 | € 87 | € 250 | € 500 | € 1.250 | € 2.500 | € 5.000 | € 12.500 |
| Hostaway [NV] | € 160 | € 160 | € 160 | € 320 | € 700 | € 1.400 | € 2.400 | € 5.000 |
| Avantio [NV] | € 295 | € 295 | € 295 | € 295 | € 350 | € 700 | € 1.400 | € 3.500 |
| Chekin Premium ✓ (solo check-in) | € 18 | € 18 | € 30 | € 60 | € 149 | € 298 | € 595 | € 1.488 |

Lettura: CasaZen è il più economico a 1-3 unità (Free, Starter), il secondo dopo Kross da 5 a 100 unità con molto
più contenuto (compliance eseguita, fornitori con garanzia, sito diretto, portale ospite), e a 250 unità sta tra Kross
e Avantio. Contro Kross + Vikey (il pacchetto italiano con check-in) CasaZen costa meno da 6 unità in su.

## 7. Copertura dei costi fissi

Costi fissi al lancio: € 1.005 al mese (§ 2). Quante vendite li coprono, una linea alla volta:

| Solo con... | Margine unitario | Serve |
|---|---:|---:|
| Host Starter (2 proprietà) | € 26,97 | 37 host |
| Host Pro (5 proprietà) | € 57,52 | 17 host |
| Unità B2B Base (10-49) | € 5,60 | 179 unità |
| Unità B2B tutto compreso (150-399) | € 6,21 | 162 unità |
| Un founding partner da 250 unità | € 400 di fissi coperti + € 250 di margine | copre il 65 %; il resto con ~15 host Starter |
| Marketplace, 10 % su interventi da € 40 | € 4 a intervento | ~250 interventi al mese |

Nel modello finanziario (scenario Base) la piattaforma è in utile dal go-live del primo founding partner (M6) e il
listino sopra produce un ARPU B2C di circa € 60 al mese per host pagante (mix 55 % Starter, 38 % Pro, 7 % Scale).

## 8. Decisioni registrate con questa versione

| # | Decisione | Prima |
|---|---|---|
| L1 | Piano **Free** a 1 proprietà con marketplace | non esisteva (prova a tempo non prevista) |
| L2 | Starter € 29, 3 proprietà | invariato |
| L3 | Pro € 79: 10 incluse + € 8 per unità fino a 50, channel manager via API incluso | 50 proprietà a prezzo fisso, channel manager a parte |
| L4 | Scale € 199: 25 incluse + € 7 per unità fino a 100, poi listino Agenzia | illimitate a prezzo fisso |
| L5 | Sconto annuale B2C −10 % | non previsto |
| L6 | B2B Base € 10 / 9 / 8, AI e Marketing +€ 4, minimo € 100 | Base € 9 / 8 / 7, minimo € 90 (analisi Quokka § 6.2) |
| L7 | Tutto compreso € 15 / 14 / 13 con **6** verifiche per unità al mese | 8 verifiche |
| L8 | Verifica in più € 0,75 (+€ 0,20 con NFC); serratura € 7 | € 0,75; serratura € 6 |
| L9 | Marketplace: 10 % pagato dal cliente, fornitori senza canone | invariato (Turno: $ 0 + fee sui pagamenti; Chekin 10 % sugli upsell) |

Cosa cambia nel codice se le decisioni sono confermate: `PlanCatalog` (Free, incluse ed extra), entitlement sulle
unità in più, prezzi Stripe per quantità (`spec-saas-billing` oggi prevede solo prezzi fissi), gating del channel
manager via API per piano, catalogo demo, testi dei piani in IT/EN.

## 9. Fonti dei prezzi dei concorrenti

- Kross Booking ✓ <https://www.krossbooking.com/prezzo> (Basic € 4, Premium € 6 per unità, minimo 3)
- Vikey ✓ <https://get.vikey.it/shop/> (Starter € 4,90, Master € 9,90 per unità al mese)
- Chekin ✓ <https://chekin.com/en/pricing/> (Basic € 3,95, Premium € 5,95, Enterprise € 7,95 per unità, minimo 3)
- Beds24 ✓ <https://www.beds24.com/pricing.html> (da € 15,50; € 0,55 per collegamento)
- Hospitable ✓ <https://hospitable.com/pricing/> (Essentials gratis illimitato; Host 1 inclusa +$ 10; Professional 2 +$ 15; Mogul 3 +$ 15-30); prezzi [NV] roommaster, bnbcalc
- Turno ✓ <https://turno.com/pricing/> ($ 0 con pulitori del marketplace, $ 10/proprietà con i propri; fee sui pagamenti fino all'8 % [NV])
- Smoobu [NV] support.smoobu.com, bnbcalc (Professional € 35, annuale € 31,50; Teams Pro+ € 55)
- Lodgify [NV] roommaster, plugnplayva (Starter $ 33, Professional $ 53, Ultimate $ 77; Ultimate $ 307 a 10, $ 574 a 25, $ 968 a 50 listing)
- Guesty [NV] costbench, roommaster, hostport (Lite $ 9-29/listing; Pro $ 40-72/listing + onboarding)
- Hostaway [NV] roommaster, stayswitchr ($ 160 minimo; $ 32 / 28 / 24 per unità a scaglioni; setup $ 300-1.000)
- Avantio [NV] bnbcalc, maltsite (€ 14 per unità, minimo € 295)
- Octorate [NV] octorate.com/best-channel-manager, hostliopro (Basic da € 8 per camera)
- Uplisting [NV] hostport ($ 100 fino a 5, ~$ 20/listing); Enso Connect [NV] ($ 9-16/listing o da $ 100/mese)
- Stripe [NV] checkoutpage, globalfeecalculator (carte EEA 1,5 % + € 0,25; Billing 0,7 %; Tax 0,5 %)
- NextPax ✓, Didit ✓, Seam ✓, Invoicetronic ✓, Resend ✓, AWS Bedrock ✓: `Sessions/prospect-quokka360/analisi-funzionalita.md` § 5

