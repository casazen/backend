# Verifica di mercato dei piani B2C (Starter, Pro, Scale)

**Data:** 15 ottobre 2026 · **Versione del piano:** 1.0.0 · **Stato:** verifica fatta; decisione del founder aperta.

**Perché questa nota.** I piani B2C di CasaZen (Starter € 29 fino a 3 proprietà, Pro € 79 fino a 50, Scale € 199
illimitate) sono un'ipotesi del founder, scritta nel codice (`Casazen.Core/Services/PlanCatalog.cs`) senza una ricerca
di mercato. Qui confronto prezzi e limiti con i listini 2026 dei concorrenti e propongo una correzione.

**Legenda.** ✓ = letto sulla pagina ufficiale del fornitore il 15 ottobre 2026 · **[NV]** = da fonti terze (confronti e
recensioni 2026), non verificato sulla pagina ufficiale perché la rete della sessione la blocca; i valori discordanti
tra fonti sono riportati come intervallo.

---

## 1. Come prezzano i concorrenti per i piccoli host

| Concorrente | Modello di prezzo | Prezzo | Cosa include al prezzo base | Fonte |
|---|---|---|---|---|
| **Kross Booking** (IT) | Per unità, minimo 3 unità | Basic € 4/unità (senza channel manager e booking engine); **Premium € 6/unità** (PMS + channel manager + booking engine); preventivo oltre 20 camere; nessun costo di attivazione | Gestionale completo con channel manager via API | ✓ krossbooking.com/prezzo |
| **Smoobu** (DE) | Per proprietà | **€ 35/proprietà** al mese; € 31,50 annuale; € 28 biennale; Flex € 29 + 0,9 % per prenotazione; Teams Pro+ € 55/unità | Channel manager, sito, inbox | [NV] support.smoobu.com, bnbcalc, Capterra |
| **Lodgify** (ES) | Per rental, decrescente con il numero | Basic $ 18, Starter $ 33, Professional $ 53, Ultimate $ 77 al mese per rental (luglio 2026); 1,9 % sulle prenotazioni nei piani bassi; ~$ 25-28/rental con 5 rental in annuale | Sito diretto, channel manager | [NV] roommaster, costbench, toolseam |
| **Hospitable** (US) | Flat con proprietà incluse + extra | **Essentials gratis, proprietà illimitate** (inbox, calendario, sync Airbnb/Vrbo/Booking/Agoda, marketplace pulizie, task); Host ~$ 29 (1 inclusa, +$ 10); Professional ~$ 59 (2 incluse, +$ 15); Mogul ~$ 99 (3 incluse, +$ 15-30); −12 % annuale. Sito diretto solo da Professional | Vedi colonna prezzo | ✓ hospitable.com/pricing (incluse ed extra); prezzi [NV] |
| **Guesty Lite** | Per listing, 1-3 listing; oltre → Guesty Pro su preventivo | $ 9/listing + 1 % per prenotazione; $ 20 annuale; $ 29 mensile | Channel manager, inbox, automazioni | [NV] costbench, hostport |
| **Beds24** (DE) | A consumo, per unità e per collegamento | Da € 15,50/mese; ~€ 3 per proprietà in più; € 0,55 per collegamento canale al mese; booking engine incluso senza commissioni | PMS, channel manager, booking engine | ✓ beds24.com/pricing |
| **OwnerRez** (US) | Per proprietà, scala decrescente | $ 40 per 1 proprietà; ~$ 88 per 5; ~$ 127 per 10; moduli a parte (proprietari, SMS, AI) | PMS, channel manager | [NV] staystra, hostport |
| **Hostfully** (US) | A bande (in cambiamento) | $ 79 per 1-4 proprietà, $ 189 per 5-19; nuovo modello $ 15-25/proprietà + quota piattaforma | PMS, guidebook | [NV] thehoststack, bnbcalc |
| **Hostaway** (FI) | Preventivo, minimo 5 unità | ~$ 125-175 per 1-4 listing; setup $ 300-1.000; 1 % sui pagamenti Stripe, 1,8 % sul booking engine | Suite completa | [NV] roommaster, zoye |
| **Uplisting** (UK) | Minimo + per listing | $ 100 fino a 5 listing, poi ~$ 20/listing | PMS, channel manager | [NV] hostport |
| **Octorate** (IT) | Per struttura / per camera | ~€ 30-39 per 1 struttura; Basic da € 8 per camera | PMS, channel manager | [NV] softwareadvice, checkinfacile |
| **Tokeet**, **Futurestay** (US) | Per rental | $ 14,99 fino a 5, $ 11,99 per 6-15 (Tokeet); $ 20-29/proprietà (Futurestay) | Channel manager | [NV] futurestay, guideflow |
| Media di mercato | Piano d'ingresso | $ 9-40 al mese, media $ 22,50 per proprietà | — | [NV] guida prezzi Capterra 2026 |

---

## 2. Cosa ne deduco

1. **Il mercato prezza per unità.** I piani flat esistono, ma con poche proprietà incluse e un prezzo per ogni
   proprietà in più (Hospitable 1/2/3 incluse, Guesty Lite 1-3, Hostfully 1-4 poi 5-19, Tokeet fino a 5). Nessun
   concorrente vende 50 proprietà a prezzo fisso, e "illimitate" a prezzo fisso lo fa solo Hospitable, **gratis**, come
   esca per vendere automazioni e sito.
2. **Starter € 29 fino a 3 proprietà è in linea, anzi aggressivo**: per 3 proprietà Smoobu chiede ~€ 105, Lodgify ~$ 100,
   Guesty Lite ~$ 87, Hospitable Host ~$ 49, Kross Premium € 18 (con channel manager via API incluso). CasaZen a € 29
   include compliance italiana, sito diretto, portale ospite e fornitori: **fattibile**.
3. **Pro € 79 fino a 50 proprietà è fuori mercato per difetto**: a 50 unità vale € 1,58 per unità, contro € 6 di Kross
   Premium (il più economico), € 31,50 di Smoobu e i € 7-9 per unità del **mio stesso listino B2B**. Un gestore con
   50 unità sceglierebbe Pro a € 79 invece del piano agenzia a ~€ 400: il piano **cannibalizza il B2B** e non copre
   neppure i costi per unità (channel manager via API € 3,15, SDI, AI). **Non sostenibile così.**
4. **Scale € 199 illimitate** ha lo stesso problema, moltiplicato: nessun tetto, nessun ricavo per unità, nessuna
   ragione per un'agenzia di passare al listino B2B. **Non sostenibile così.**
5. **I tre prezzi (€ 29 / € 79 / € 199) sono buoni punti di prezzo**: ricalcano Hospitable ($ 29 / $ 59 / $ 99) e
   Lodgify ($ 33 / $ 53 / $ 77) e stanno sotto il minimo di Hostaway e Uplisting. Il problema sono i **limiti**, non i prezzi.
6. **Il concorrente da battere per l'host piccolo è Hospitable Essentials, che è gratis**: CasaZen vince solo se la
   compliance italiana, il sito diretto incluso già in Starter e i fornitori con garanzia valgono € 29 al mese agli occhi
   dell'host. È una promessa di prodotto, non di prezzo.
7. **Sconto annuale**: il mercato dà il 10-12 % sul pagamento annuale (Smoobu 10 %, Hospitable 12 %). Il listino B2C
   non lo prevede ancora.

---

## 3. Proposta: stessi prezzi, limiti per unità incluse più prezzo per unità in più

| Piano | Prezzo al mese | Proprietà incluse | Ogni proprietà in più | Tetto | Oltre il tetto |
|---|---:|---:|---:|---:|---|
| **Starter** | € 29 | 3 | non previste | 3 | → Pro |
| **Pro** | € 79 | 10 | € 7 | 50 | → Scale |
| **Scale** | € 199 | 30 | € 6 | 100 | → piano **Agenzia** (listino B2B per unità) |
| Pagamento annuale | −10 % | | | | come il B2B (+10 % mensile) |
| Channel manager via API (opzione) | +€ 4 per unità | | | | costo CasaZen € 3,15-3,50 |

**Perché questi numeri.** Il prezzo per unità in più (€ 6-7) è sotto il Base B2B (€ 9 / € 8 / € 7) perché il B2C non ha
portale proprietari, staff e compliance svizzera; resta sopra Kross Premium (€ 6) solo con il channel manager via API
in opzione, e sotto tutti gli altri. Il passaggio Pro → Scale conviene da 28 unità, Scale → Agenzia da 100: nessun
cliente ha interesse a restare nel piano sbagliato.

**Esempi** (prezzi mensili, IVA esclusa, senza opzioni):

| Unità | CasaZen proposto | Kross Premium ✓ | Smoobu [NV] | Hospitable Professional [NV] | Lodgify Professional [NV] | B2B Base CasaZen |
|---:|---:|---:|---:|---:|---:|---:|
| 3 | Starter € 29 | € 18 | ~€ 105 | ~$ 74 | ~$ 159 | — |
| 5 | Pro € 79 | € 30 | ~€ 175 | ~$ 104 | ~$ 125-265 | — |
| 20 | Pro € 149 | € 120 | ~€ 700 | ~$ 329 | preventivo | € 180 (10-49) |
| 50 | Scale € 319 | € 300 | ~€ 1.750 | ~$ 779 | preventivo | € 400 (50-149) |
| 100 | Scale € 619 | preventivo | ~€ 3.500 | ~$ 1.529 | preventivo | € 800 (50-149) |

Contro Kross, che resta il più economico in Italia, CasaZen costa di più se si aggiunge il channel manager via API
(+€ 4/unità): il valore in più deve venire dalla compliance eseguita dal software, dal sito diretto e dai fornitori con
garanzia. Contro tutti gli altri CasaZen costa meno a parità di unità.

**Opzione da valutare: un piano gratuito.** Hospitable dimostra che un piano gratis con proprietà illimitate funziona
come esca; per CasaZen un piano **Free a 1 proprietà** (sito su sottodominio con "Powered by CasaZen", calendario iCal,
portale ospite, **marketplace fornitori**, senza automazioni e senza fattura elettronica) sarebbe coerente con il
sistema che si auto-alimenta: l'host invitato dal suo fornitore entra senza pagare, ordina interventi (CasaZen incassa
il 10 %) e passa a Starter quando vuole il sito con il suo marchio e la compliance automatica. Pro: ingresso senza
attrito, più volume per il marketplace. Contro: costi AI e supporto per utenti che non pagano, conversione da misurare
(benchmark 15-25 % in 6-12 mesi). È una scelta di strategia, non di prezzo: la lascio aperta.

---

## 4. Impatto

| Dove | Cosa cambia se la proposta viene accettata |
|---|---|
| **Business plan** § 5.1 e Appendice A.1 | Tabella dei piani con proprietà incluse, prezzo per unità in più e tetti; sconto annuale |
| **Modello finanziario** | L'ARPU B2C di € 55 non cambia (mix Starter/Pro/Scale); le unità in più sono un ricavo aggiuntivo non modellato (prudenziale). Da aggiungere come riga quando ci saranno dati reali |
| **Codice** | `PlanCatalog` (3 / 50 / illimitate → 3 / 10+extra / 30+extra), `IEntitlementService` (conteggio delle unità in più), prezzi Stripe per quantità (oggi `spec-saas-billing` prevede solo prezzi fissi per piano), test `PlanLimitAndOrgProvisioningPostgresTests`, catalogo demo, testi i18n dei piani |
| **Sito pubblico** | Pagina prezzi (oggi assente per scelta: iscrizione su invito) |

Niente di questo si fa prima della decisione del founder; la decisione va registrata come versione `MAJOR` o `MINOR`
del piano (cambia il listino) e nel registro delle decisioni di prodotto.

---

## 5. Fonti

Pagine ufficiali lette il 15 ottobre 2026 (✓):

- Kross Booking, prezzi: <https://www.krossbooking.com/prezzo>
- Beds24, pricing: <https://www.beds24.com/pricing.html>
- Hospitable, pricing (proprietà incluse ed extra; i prezzi mensili non sono esposti nella pagina letta): <https://hospitable.com/pricing/>

Fonti terze 2026 ([NV]; lodgify.com/pricing e la pagina di supporto Smoobu rispondono 403 dalla rete della sessione):

- Lodgify: <https://www.roommaster.com/blog/lodgify-pricing>, <https://costbench.com/software/vacation-rental-software/lodgify/>, <https://toolseam.com/lodgify-pricing/>
- Smoobu: <https://support.smoobu.com/hc/en-us/articles/360003170680-How-much-does-Smoobu-cost-Plans-and-pricing-explained>, <https://www.bnbcalc.com/reviews/smoobu-review>, <https://www.capterra.com/p/160106/Smoobu/pricing/>
- Hospitable (prezzi): <https://www.roommaster.com/blog/hospitable-pricing>, <https://www.bnbcalc.com/reviews/hospitable-review>
- Guesty Lite: <https://costbench.com/software/vacation-rental-software/guesty/>, <https://hostport.co/blog/best-guesty-alternatives>
- OwnerRez, Uplisting, Beds24: <https://staystra.com/best-str-channel-manager-2026-hostaway-guesty-lodgify-ownerrez-beds24/>, <https://www.hostport.co/blog/best-channel-managers-apartments>
- Hostaway: <https://www.roommaster.com/blog/hostaway-pricing>, <https://www.zoye.io/blog/hostaway-pricing>
- Hostfully: <https://thehoststack.com/hostfully-review/>, <https://www.bnbcalc.com/reviews/a-hostfully-review>
- Octorate, Ciaobooking, Scidoo: <https://www.softwareadvice.it/software/307972/octorate>, <https://checkinfacile.com/blog/wubook-prezzi-recensioni-alternative-2026.html>, <https://www.capterra.com/p/206171/Ciaobooking/>
- Tokeet, Futurestay, media di mercato: <https://www.futurestay.com/compare/best-vacation-rental-software>, <https://www.capterra.com/resources/vacation-rental-software-pricing-models/>
- Confronto italiano Kross vs Lodgify: <https://www.lodgify.com/it/confronto/lodgify-vs-krossbooking/>
