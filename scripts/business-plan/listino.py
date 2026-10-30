#!/usr/bin/env python3
"""Listino CasaZen (B2C e B2B): costi fissi, costi unitari, prezzi, margini e confronto con i concorrenti.

Genera `docs/business-plan/LISTINO.md`. Solo libreria standard.

    python3 scripts/business-plan/listino.py

Le ipotesi di costo vengono da `Sessions/prospect-quokka360/analisi-funzionalita.md` § 5 (listini ufficiali dei
fornitori al 2026-10-15, USD = EUR 1:1 per prudenza) e dai listini Stripe; i prezzi dei concorrenti dalla ricerca di
mercato in `docs/business-plan/VERIFICA-PIANI-B2C.md` e `LISTINO.md` § 6. Ogni cifra qui è un parametro: per cambiare
un prezzo o un costo si modifica questo file e si rigenera il documento.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs" / "business-plan" / "LISTINO.md"

# ---------------------------------------------------------------------------
# 1. Costi fissi mensili (euro, IVA esclusa)
# ---------------------------------------------------------------------------
FIXED_LAUNCH = [  # produzione con tutte le integrazioni attive, primi clienti
    ("Hosting backend e database (Railway Pro + Supabase Pro)", 85.0, "analisi Quokka § 5.1; VM Oracle Always Free + Supabase Pro ridurrebbe a ~€ 25"),
    ("Frontend Vercel Pro (uso commerciale)", 20.0, "il piano Hobby non ammette uso commerciale"),
    ("Email transazionali Resend Pro", 20.0, "Scale € 90 oltre 50.000 email/mese"),
    ("Channel manager NextPax, minimo mensile (50 unità incluse)", 350.0, "€ 3,15/unità oltre le 50, fatturazione annuale"),
    ("Strumenti di sviluppo assistito (Claude Code Max)", 200.0, "ricerca e sviluppo, non costo del venduto"),
    ("Commercialista", 150.0, "stima"),
    ("Pareri fiscali e legali ricorrenti", 100.0, "budget"),
    ("RC professionale", 50.0, "stima"),
    ("Conto, PEC, firma digitale, bolli", 30.0, "stima"),
]
FIXED_SCALE_DELTA = [("Email Resend Scale (oltre 50.000 email/mese)", 70.0), ("Hosting a regime (più risorse)", 100.0)]

# ---------------------------------------------------------------------------
# 2. Costi variabili unitari (euro al mese)
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class Unit:
    cm_api: float = 3.15            # NextPax per unità oltre le 50 incluse (fatturazione annuale; € 3,50 mensile)
    hosting: float = 0.10           # quota di hosting, database e storage per unità
    email: float = 0.05             # email transazionali per unità
    ai_light: float = 0.10          # suggerimenti e testi: per host Starter
    ai_inbox: float = 0.30          # risposte AI in bozza, routine, traduzioni: per unità Pro/Scale/B2B
    ai_marketing: float = 0.35      # AI automatica + CRM e campagne (opzione B2B): per unità
    sdi_invoice: float = 0.10       # una fattura elettronica (Invoicetronic, pacchetti prepagati)
    owner_statement: float = 0.10   # rendiconto e fattura al proprietario (B2B), per unità
    support_b2c_host: float = 0.50  # assistenza e onboarding, per host B2C (Starter)
    support_b2c_pro: float = 1.00   # per host Pro
    support_b2c_scale: float = 2.00 # per host Scale
    support_b2b_unit: float = 0.50  # per unità B2B
    free_host: float = 0.45         # hosting, email e assistenza di un host del piano gratuito
    verification: float = 0.33      # Didit, una verifica documento + volto (senza NFC)
    verification_nfc: float = 0.48  # con lettura NFC del chip
    lock: float = 5.00              # Seam, per serratura al mese
    card_pct: float = 0.015         # Stripe, carte EEA
    card_fixed: float = 0.25        # per transazione
    billing_pct: float = 0.007      # Stripe Billing
    tax_pct: float = 0.005          # Stripe Tax
    sepa_pct: float = 0.008         # addebito SEPA / bonifico per le fatture annuali B2B (stima)

U = Unit()


def card_cost(amount: float) -> float:
    return amount * (U.card_pct + U.billing_pct + U.tax_pct) + U.card_fixed


def b2b_payment_cost(amount: float) -> float:
    return amount * (U.sepa_pct + U.billing_pct + U.tax_pct)


# ---------------------------------------------------------------------------
# 3. Listino B2C (prezzi mensili, IVA esclusa; annuale −10 %)
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class B2CPlan:
    name: str
    price: float
    included: int
    extra_price: float | None
    cap: int | None
    seats: str
    cm_included: bool
    ai: str   # "light" | "inbox"
    support: float


B2C = [
    B2CPlan("Free", 0.0, 1, None, 1, "1", False, "light", 0.0),
    B2CPlan("Starter", 29.0, 3, None, 3, "2", False, "light", U.support_b2c_host),
    B2CPlan("Pro", 79.0, 10, 8.0, 50, "10", True, "inbox", U.support_b2c_pro),
    B2CPlan("Scale", 199.0, 25, 7.0, 100, "illimitate", True, "inbox", U.support_b2c_scale),
]
ANNUAL_DISCOUNT = 0.10
B2C_CM_OPTION = 4.0   # channel manager via API in opzione sul piano Starter, per unità


def b2c_price(plan: B2CPlan, units: int) -> float:
    extra = max(0, units - plan.included)
    return plan.price + extra * (plan.extra_price or 0.0)


def b2c_cost(plan: B2CPlan, units: int, cm_option: bool = False) -> float:
    if plan.name == "Free":
        return U.free_host
    ai = U.ai_light if plan.ai == "light" else U.ai_inbox * units
    cm = U.cm_api * units if (plan.cm_included or cm_option) else 0.0
    price = b2c_price(plan, units) + (B2C_CM_OPTION * units if cm_option else 0.0)
    return cm + U.hosting * units + U.email * units + ai + U.sdi_invoice + plan.support + card_cost(price)


# ---------------------------------------------------------------------------
# 4. Listino B2B (per unità al mese, pagamento annuale; mensile +10 %; minimo € 90)
# ---------------------------------------------------------------------------
B2B_BANDS = [("10-49", 10, 10.0), ("50-149", 50, 9.0), ("150-399", 150, 8.0)]
B2B_AI_MARKETING = 4.0
B2B_VERIFICATIONS_INCLUDED = 6     # nel tutto compreso, per unità al mese
B2B_ALL_INCLUSIVE = {"10-49": 15.0, "50-149": 14.0, "150-399": 13.0}
B2B_VERIFICATION_PRICE = 0.75
B2B_VERIFICATION_NFC_EXTRA = 0.20
B2B_LOCK_PRICE = 7.0
B2B_MINIMUM = 100.0
B2B_MONTHLY_SURCHARGE = 0.10
FOUNDING_PARTNER_MARGIN = 1.0      # euro per unità sopra il costo documentato


def b2b_base_cost(price: float) -> float:
    return (U.cm_api + U.hosting + U.email + U.ai_inbox + U.owner_statement + U.support_b2b_unit
            + b2b_payment_cost(price))


def b2b_all_inclusive_cost(price: float, nfc: bool = False) -> float:
    v = U.verification_nfc if nfc else U.verification
    return b2b_base_cost(price) + U.ai_marketing + B2B_VERIFICATIONS_INCLUDED * v


# ---------------------------------------------------------------------------
# 5. Concorrenti: prezzo mensile stimato per N unità (fonti in LISTINO.md § 6)
# ---------------------------------------------------------------------------
def kross(n: int) -> float:                 # Premium € 6/unità, minimo 3 unità ✓
    return max(3, n) * 6.0

def kross_vikey(n: int) -> float:           # + Vikey Master € 9,90/unità ✓ (check-in e invio Questura)
    return kross(n) + n * 9.90

def smoobu(n: int) -> float:                # Professional € 35/proprietà mensile [NV]
    return n * 35.0

def lodgify(n: int) -> float:               # Professional $ 53/rental; Ultimate $ 307 (10), $ 574 (25), $ 968 (50) [NV]
    points = {1: 53, 3: 159, 5: 230, 10: 307, 25: 574, 50: 968, 100: 1800, 250: 4000}
    return float(points.get(n, n * 20))

def hospitable(n: int) -> float:            # Host $ 29 (1 incl.), Professional $ 59 + $ 15, Mogul $ 99 + $ 30 [NV]
    if n <= 1:
        return 29.0
    return 59.0 + max(0, n - 2) * 15.0

def guesty(n: int) -> float:                # Lite $ 29/listing (1-3); Pro $ 40-72/listing, media $ 50 [NV]
    return n * 29.0 if n <= 3 else n * 50.0

def hostaway(n: int) -> float:              # $ 160 minimo a 5; $ 32 (6-20); $ 28 (21-50); $ 24 (51-100); ~$ 20 oltre [NV]
    if n <= 5:
        return 160.0
    if n <= 20:
        return n * 32.0
    if n <= 50:
        return n * 28.0
    if n <= 100:
        return n * 24.0
    return n * 20.0

def avantio(n: int) -> float:               # € 14/unità, minimo € 295 [NV]
    return max(295.0, n * 14.0)

def octorate(n: int) -> float:              # Basic da € 8/camera [NV]
    return n * 8.0

def chekin(n: int) -> float:                # Premium € 5,95/unità, minimo 3 ✓ (solo check-in: si somma a un PMS)
    return max(3, n) * 5.95


def casazen(n: int) -> tuple[str, float]:
    if n <= 1:
        return "Free / Starter", 0.0
    if n <= 3:
        return "Starter", 29.0
    if n <= 24:
        return "Pro", b2c_price(B2C[2], n)
    if n <= 100:
        return "Scale", b2c_price(B2C[3], n)
    band = "150-399" if n >= 150 else "50-149"
    return f"Agenzia tutto compreso ({band})", n * B2B_ALL_INCLUSIVE[band]


# ---------------------------------------------------------------------------
# 6. Rendering
# ---------------------------------------------------------------------------
def eur(x: float, dec: int = 0) -> str:
    s = f"{abs(x):,.{dec}f}".replace(",", "X").replace(".", ",").replace("X", ".")
    return ("-€ " if x < 0 else "€ ") + s


def pct(x: float) -> str:
    return f"{x * 100:.0f} %"


def render() -> str:
    L: list[str] = []
    L.append("# Listino CasaZen: prezzi B2C e B2B, costi e margini")
    L.append("")
    L.append("> **Documento generato** da `scripts/business-plan/listino.py`: non modificare a mano. Versione del piano 1.1.0,")
    L.append("> 15 ottobre 2026. Prezzi in euro, IVA esclusa; USD = EUR 1:1 per prudenza. I prezzi dei concorrenti sono")
    L.append("> quelli della ricerca di mercato (§ 6): ✓ = pagina ufficiale letta, [NV] = fonti terze 2026.")
    L.append("")
    L.append("## 1. Il metodo")
    L.append("")
    L.append("1. Ho calcolato i **costi fissi** della piattaforma in produzione e i **costi unitari** di ogni integrazione")
    L.append("   (channel manager, verifica d'identità, serrature, fattura elettronica, AI, email, hosting, pagamenti).")
    L.append("2. Ho fissato un **margine lordo obiettivo**: almeno il 50 % sulle unità incluse nei piani a pieno utilizzo,")
    L.append("   almeno il 40 % sulle unità in più e sulle opzioni, oltre il 70 % sui piani d'ingresso.")
    L.append("3. Ho confrontato il risultato con i listini 2026 di tredici concorrenti a 1, 3, 5, 10, 25, 50, 100 e 250 unità")
    L.append("   e corretto dove CasaZen risultava fuori mercato per eccesso o per difetto.")
    L.append("4. Ho verificato che B2C e B2B siano **coerenti**: a parità di unità il B2C costa meno del B2B (ha meno funzioni),")
    L.append("   e nessun cliente ha convenienza a restare nel piano sbagliato (nessun gradino all'indietro tra i piani).")
    L.append("")
    # --- Fixed costs
    L.append("## 2. Costi fissi mensili")
    L.append("")
    L.append("| Voce | € / mese | Nota |")
    L.append("|---|---:|---|")
    tot = 0.0
    for name, cost, note in FIXED_LAUNCH:
        L.append(f"| {name} | {eur(cost)} | {note} |")
        tot += cost
    L.append(f"| **Totale al lancio (tutte le integrazioni attive)** | **{eur(tot)}** | di cui € 200 di sviluppo e € 350 di minimo channel manager |")
    scale_tot = tot + sum(c for _, c in FIXED_SCALE_DELTA)
    L.append(f"| Totale a regime (+ {', '.join(n.lower() for n, _ in FIXED_SCALE_DELTA)}) | {eur(scale_tot)} | dall'anno 2 |")
    L.append("")
    L.append("Prima del primo cliente i fissi scendono a ~€ 205 (hosting minimo e strumenti di sviluppo): le integrazioni")
    L.append("restano in sandbox gratuita e il minimo del channel manager si firma solo con il primo cliente vicino.")
    L.append("")
    # --- Unit costs
    L.append("## 3. Costi unitari delle integrazioni")
    L.append("")
    L.append("| Voce | Costo | Fonte |")
    L.append("|---|---:|---|")
    L.append(f"| Channel manager via API (NextPax), per unità al mese oltre le 50 incluse | {eur(U.cm_api, 2)} (annuale) / € 3,50 (mensile) | nextpax.com/nextpax-pricing ✓ |")
    L.append(f"| Verifica d'identità documento + volto (Didit) | {eur(U.verification, 2)} / {eur(U.verification_nfc, 2)} con NFC | didit.me/pricing ✓; Sumsub $ 1,35 con minimo $ 149/mese |")
    L.append(f"| Serratura smart (Seam), per dispositivo al mese | {eur(U.lock, 2)} | seam.co/pricing ✓ |")
    L.append(f"| Fattura elettronica SDI (Invoicetronic, pacchetti prepagati) | {eur(U.sdi_invoice, 2)} a fattura | invoicetronic.com/pricing ✓ |")
    L.append(f"| Rendiconto e fattura al proprietario (B2B) | {eur(U.owner_statement, 2)} per unità | una fattura SDI per proprietario al mese |")
    L.append(f"| AI (DeepSeek su Bedrock UE): suggerimenti / risposte in bozza e routine / automatica + CRM | {eur(U.ai_light, 2)} per host / {eur(U.ai_inbox, 2)} per unità / +{eur(U.ai_marketing, 2)} per unità | $ 0,74/M token in ingresso, $ 2,22/M in uscita ✓ |")
    L.append(f"| Email transazionali (Resend) | {eur(U.email, 2)} per unità | resend.com/pricing ✓ |")
    L.append(f"| Hosting, database, storage | {eur(U.hosting, 2)} per unità | quota dei fissi a 1.000+ unità |")
    L.append(f"| Assistenza e onboarding | {eur(U.support_b2c_host, 2)} host Starter / {eur(U.support_b2c_pro, 2)} Pro / {eur(U.support_b2c_scale, 2)} Scale / {eur(U.support_b2b_unit, 2)} per unità B2B | stima del tempo di customer success |")
    L.append(f"| Host del piano gratuito | {eur(U.free_host, 2)} per host | hosting, email, assistenza di base |")
    L.append(f"| Incasso delle nostre fatture: carta EEA + Stripe Billing + Stripe Tax | {pct(U.card_pct)} + {eur(U.card_fixed, 2)} + {pct(U.billing_pct)} + {pct(U.tax_pct)} | stripe.com/pricing [NV]; B2B annuale via SEPA ~{pct(U.sepa_pct)} |")
    L.append("| Video check-in (Daily), WhatsApp (Meta), pagoPA, commissioni Stripe sugli incassi dei clienti | 0 per CasaZen | gratis entro i limiti o a carico del cliente |")
    L.append("")
    # --- B2C
    L.append("## 4. Listino B2C (host): prezzi, contenuto, margini")
    L.append("")
    L.append(f"Prezzi mensili, IVA esclusa; **pagamento annuale −{int(ANNUAL_DISCOUNT*100)} %**. Oltre 100 proprietà si passa al listino Agenzia.")
    L.append("")
    L.append("| Piano | Prezzo | Annuale | Proprietà incluse | Ogni proprietà in più | Tetto | Persone | Cosa aggiunge |")
    L.append("|---|---:|---:|---:|---:|---:|---:|---|")
    what = {
        "Free": "Marketplace fornitori con Garanzia, calendario iCal, pagina pubblica dell'immobile con richiesta di prenotazione \"paga in struttura\" approvata dall'host, cockpit compliance in sola lettura, app. Marchio \"Powered by CasaZen\"",
        "Starter": "Sito di prenotazione diretta con checkout Stripe sul proprio conto (sottodominio CasaZen), wizard di compliance (CIN, imposta di soggiorno, sicurezza), portale ospite e file Alloggiati, check-out guidato, suggerimenti stagionali, locazioni lunghe (contratti, RLI assistita, canone). Channel manager via API in opzione a € 4/unità",
        "Pro": "Channel manager via API incluso (Airbnb, Booking.com, Vrbo, Expedia), dominio proprio senza marchio CasaZen, temi e branding, routine (libreria pronta), inbox con risposte AI in bozza (fase 2), dispositivi smart (serrature € 7/serratura), check-in biometrico opzionale (€ 0,75 a verifica)",
        "Scale": "Routine avanzate e AI automatica (fase 2), persone illimitate con ruoli, supporto prioritario, export e API",
    }
    for p in B2C:
        annual = eur(p.price * (1 - ANNUAL_DISCOUNT)) if p.price else "—"
        extra = eur(p.extra_price) if p.extra_price else "non previste"
        cap = str(p.cap) if p.cap else "—"
        L.append(f"| **{p.name}** | {eur(p.price)} | {annual} | {p.included} | {extra} | {cap} | {p.seats} | {what[p.name]} |")
    L.append("")
    L.append("### 4.1 Margine lordo per piano")
    L.append("")
    L.append("| Piano | Utilizzo | Prezzo | Costo CasaZen | Margine | Margine % |")
    L.append("|---|---|---:|---:|---:|---:|")
    cases = [
        (B2C[0], 1, "1 proprietà"),
        (B2C[1], 1, "1 proprietà"), (B2C[1], 3, "3 proprietà (pieno)"),
        (B2C[2], 5, "5 proprietà (tipico)"), (B2C[2], 10, "10 proprietà (incluse, pieno)"), (B2C[2], 24, "24 proprietà (incluse + 14 extra)"),
        (B2C[3], 25, "25 proprietà (incluse, pieno)"), (B2C[3], 50, "50 proprietà (+ 25 extra)"), (B2C[3], 100, "100 proprietà (tetto)"),
    ]
    for plan, n, lab in cases:
        price = b2c_price(plan, n)
        cost = b2c_cost(plan, n)
        m = price - cost
        L.append(f"| {plan.name} | {lab} | {eur(price)} | {eur(cost, 2)} | {eur(m, 2)} | {pct(m / price) if price else '—'} |")
    # Starter + CM option
    price = 29.0 + 3 * B2C_CM_OPTION
    cost = b2c_cost(B2C[1], 3, cm_option=True)
    L.append(f"| Starter + channel manager via API | 3 proprietà | {eur(price)} | {eur(cost, 2)} | {eur(price - cost, 2)} | {pct((price - cost) / price)} |")
    # extra unit margins
    for plan in (B2C[2], B2C[3]):
        ex_cost = U.cm_api + U.hosting + U.email + U.ai_inbox + plan.extra_price * (U.card_pct + U.billing_pct + U.tax_pct)
        L.append(f"| {plan.name}, una proprietà in più | marginale | {eur(plan.extra_price)} | {eur(ex_cost, 2)} | {eur(plan.extra_price - ex_cost, 2)} | {pct((plan.extra_price - ex_cost) / plan.extra_price)} |")
    L.append("")
    L.append("Lettura: Starter e Pro a utilizzo tipico stanno sopra il 70 %; Pro e Scale a pieno utilizzo tra il 50 e il 60 %")
    L.append("perché il channel manager via API (€ 3,15/unità) è incluso; le unità in più rendono il 43-50 %. Il piano Free costa")
    L.append(f"~{eur(U.free_host, 2)} al mese per host: con il 10 % su un solo intervento da € 40 al mese (€ 4) si ripaga nove volte.")
    L.append("")
    L.append("### 4.2 Perché questi prezzi")
    L.append("")
    L.append("- **Free (1 proprietà)**: Hospitable regala proprietà illimitate (inbox, calendario, marketplace pulizie) e vende sito e")
    L.append("  automazioni; CasaZen regala l'ingresso nel marketplace, che è dove guadagna il 10 %. È il piano in cui entra l'host")
    L.append("  invitato dal suo fornitore. Un piano, non una prova a tempo: le prove scadono, il ciclo della rete no.")
    L.append("- **Starter € 29 (3 proprietà)**: in linea con Hospitable Host ($ 29) e Guesty Lite ($ 29/listing), sotto Smoobu (€ 35 per")
    L.append("  **una** proprietà) e Lodgify ($ 33-53 per rental). Include il sito diretto, che Hospitable vende da $ 59.")
    L.append("- **Pro € 79 (10 incluse + € 8)**: con il channel manager via API incluso costa meno di Kross Premium + Vikey (€ 15,90/unità)")
    L.append("  già da 6 unità, e meno di Smoobu, Lodgify, Hospitable e Hostaway a ogni taglia. Resta sopra Kross da solo (€ 6/unità),")
    L.append("  che però non ha compliance eseguita, fornitori, sito e portale ospite.")
    L.append("- **Scale € 199 (25 incluse + € 7)**: il passaggio Pro → Scale è conveniente esattamente a 25 proprietà (entrambi € 199) e")
    L.append("  Scale resta sotto il listino Agenzia (€ 8-10/unità) fino al tetto di 100: nessun gradino all'indietro.")
    L.append("- **Unità in più a € 8 e € 7**: sotto il Base B2B (€ 10/9/8) perché il B2C non ha portale proprietari, staff e compliance")
    L.append("  svizzera; sopra il costo marginale (€ 3,60-3,80) con margine 43-55 %.")
    L.append(f"- **Annuale −{int(ANNUAL_DISCOUNT*100)} %**: lo sconto di mercato (Smoobu 10 %, Hospitable 12 %, Hostaway 10-20 %).")
    L.append("")
    # --- B2B
    L.append("## 5. Listino B2B (agenzie): prezzi, opzioni, margini")
    L.append("")
    L.append(f"Prezzi per unità al mese, IVA esclusa, con **pagamento annuale** (+{int(B2B_MONTHLY_SURCHARGE*100)} % con pagamento mensile); minimo {eur(B2B_MINIMUM)} al mese; oltre 400 unità preventivo.")
    L.append("")
    L.append("| Pacchetto | 10-49 | 50-149 | 150-399 | Cosa contiene |")
    L.append("|---|---:|---:|---:|---|")
    L.append("| **Base** | " + " | ".join(eur(p) for _, _, p in B2B_BANDS) + " | Gestionale, channel manager via API con contenuti, chat e recensioni, inbox con risposte AI in bozza, sito diretto, check-in online, adempimenti via API (Alloggiati, ISTAT, feratel) e file pronti per quelli senza API, portale proprietari con rendiconti, fatture SDI per conto dell'agenzia e file di bonifico, routine, fornitori e staff propri, app |")
    L.append(f"| **+ AI e Marketing** | +{eur(B2B_AI_MARKETING)} | +{eur(B2B_AI_MARKETING)} | +{eur(B2B_AI_MARKETING)} | Risposte AI inviate in automatico sopra una soglia di confidenza, routine create da una frase, CRM, campagne email e WhatsApp |")
    L.append(f"| **Tutto compreso** | " + " | ".join(f"**{eur(B2B_ALL_INCLUSIVE[b])}**" for b, _, _ in B2B_BANDS) + f" | Base + AI e Marketing + **{B2B_VERIFICATIONS_INCLUDED} verifiche d'identità per unità al mese** |")
    L.append(f"| Check-in biometrico | {eur(B2B_VERIFICATION_PRICE, 2)} a verifica | | | Documento e volto all'ingresso con alternativa in videochiamata; NFC +{eur(B2B_VERIFICATION_NFC_EXTRA, 2)}; nel B2B è il predefinito |")
    L.append(f"| Serrature smart | {eur(B2B_LOCK_PRICE)} per serratura al mese | | | Codice per soggiorno rilasciato dopo l'identificazione; hardware escluso |")
    L.append(f"| Founding Partner | costo documentato + {eur(FOUNDING_PARTNER_MARGIN)}/unità | | | Prime 10 agenzie, 24 mesi, pagamento annuale, caso di studio; verifiche al costo; costi fissi coperti dal partner |")
    L.append("")
    L.append("### 5.1 Margine lordo per unità")
    L.append("")
    L.append("| Pacchetto | Scaglione | Prezzo | Costo CasaZen | Margine | Margine % |")
    L.append("|---|---|---:|---:|---:|---:|")
    for band, _, p in B2B_BANDS:
        c = b2b_base_cost(p)
        L.append(f"| Base | {band} | {eur(p)} | {eur(c, 2)} | {eur(p - c, 2)} | {pct((p - c) / p)} |")
    for band, _, p in B2B_BANDS:
        price = p + B2B_AI_MARKETING
        c = b2b_base_cost(price) + U.ai_marketing
        L.append(f"| Base + AI e Marketing | {band} | {eur(price)} | {eur(c, 2)} | {eur(price - c, 2)} | {pct((price - c) / price)} |")
    for band, _, _ in B2B_BANDS:
        price = B2B_ALL_INCLUSIVE[band]
        c = b2b_all_inclusive_cost(price)
        cn = b2b_all_inclusive_cost(price, nfc=True)
        L.append(f"| Tutto compreso ({B2B_VERIFICATIONS_INCLUDED} verifiche) | {band} | {eur(price)} | {eur(c, 2)} ({eur(cn, 2)} con NFC) | {eur(price - c, 2)} | {pct((price - c) / price)} ({pct((price - cn) / price)} NFC) |")
    v_m = B2B_VERIFICATION_PRICE - U.verification
    L.append(f"| Verifica d'identità in più | — | {eur(B2B_VERIFICATION_PRICE, 2)} | {eur(U.verification, 2)} | {eur(v_m, 2)} | {pct(v_m / B2B_VERIFICATION_PRICE)} |")
    L.append(f"| Serratura smart | — | {eur(B2B_LOCK_PRICE)} | {eur(U.lock, 2)} | {eur(B2B_LOCK_PRICE - U.lock, 2)} | {pct((B2B_LOCK_PRICE - U.lock) / B2B_LOCK_PRICE)} |")
    L.append("")
    L.append("### 5.2 Perché questi prezzi")
    L.append("")
    L.append("- **Base € 10 / 9 / 8** (era € 9 / 8 / 7 nell'analisi Quokka): sopra Kross Premium (€ 6, solo gestionale + channel manager)")
    L.append("  e Octorate (€ 8/camera), sotto Avantio (€ 14 con minimo € 295), Lodgify Ultimate (~$ 19-31/rental), Hostaway ($ 24-40),")
    L.append("  Guesty Pro ($ 40-72). Il portale proprietari è incluso (Avantio lo include, OwnerRez lo fa pagare, Guesty $ 5-10/listing).")
    L.append("  Con € 9 / 8 / 7 il margine scendeva al 38 % nello scaglione alto, perché il costo unitario (€ 4,35) è per tre quarti il")
    L.append("  channel manager: con un euro in più per unità il margine sta tra il 46 e il 56 %, e le agenzie sotto le 10 unità")
    L.append("  restano meglio servite dai piani Pro e Scale.")
    L.append(f"- **Tutto compreso € 15 / 14 / 13 con {B2B_VERIFICATIONS_INCLUDED} verifiche** (invariato nel prezzo): contro Kross Premium + Vikey Master (€ 15,90/unità)")
    L.append("  e Kross + Chekin Premium (€ 11,95, senza AI, CRM e portale proprietari) resta sotto o alla pari con molto più contenuto;")
    L.append("  le verifiche incluse scendono da 8 a 6 per unità al mese (tre prenotazioni da due adulti), le altre a € 0,75: così il")
    L.append("  margine sta tra il 48 e il 54 % invece del 37-45 %. Comprato a parte (Base + AI + 6 verifiche) costerebbe € 18,50-16,50:")
    L.append("  il pacchetto sconta il 19-21 % e spinge le agenzie verso il tutto compreso, che è dove CasaZen rende di più.")
    L.append("- **Verifica € 0,75** (costo € 0,33-0,48): meno della metà di Uplisting (£ 1,25) e di Hospitable ($ 2); Vikey e Chekin la")
    L.append("  vendono dentro un canone per unità (€ 4,90-9,90 e € 5,95).")
    L.append(f"- **Serratura € {B2B_LOCK_PRICE:.0f}** (costo $ 5): nel mezzo del mercato ($ 3-8; Kross KDoor € 29/mese).")
    L.append("- **Minimo € 100**: dieci unità a € 10; sotto le 10 unità un'agenzia usa Pro o Scale.")
    L.append("- **Founding Partner**: prezzo = costo + € 1 per unità, margine bloccato. Per Quokka 360 (250 unità) il canone resta")
    L.append("  ~€ 1.730 al mese più le verifiche al costo, come nell'analisi del 15 ottobre.")
    L.append("")
    # --- Comparison
    L.append("## 6. Confronto con i concorrenti per numero di unità")
    L.append("")
    L.append("Prezzo mensile stimato del pacchetto più vicino a CasaZen (gestionale + channel manager + sito), senza opzioni; CasaZen")
    L.append("nel piano più conveniente per quel numero di unità. Kross + Vikey e Kross + Chekin aggiungono il check-in con verifica.")
    L.append("")
    sizes = [1, 3, 5, 10, 25, 50, 100, 250]
    L.append("| Unità | " + " | ".join(str(n) for n in sizes) + " |")
    L.append("|---|" + "---:|" * len(sizes))
    L.append("| **CasaZen** (piano) | " + " | ".join(f"**{eur(casazen(n)[1])}** ({casazen(n)[0]})" for n in sizes) + " |")
    rows = [
        ("Kross Premium ✓", kross), ("Kross + Vikey Master ✓", kross_vikey), ("Octorate Basic [NV]", octorate),
        ("Smoobu Professional [NV]", smoobu), ("Lodgify Professional/Ultimate [NV]", lodgify),
        ("Hospitable Host/Professional [NV]", hospitable), ("Guesty Lite/Pro [NV]", guesty),
        ("Hostaway [NV]", hostaway), ("Avantio [NV]", avantio), ("Chekin Premium ✓ (solo check-in)", chekin),
    ]
    for name, f in rows:
        L.append(f"| {name} | " + " | ".join(eur(f(n)) for n in sizes) + " |")
    L.append("")
    L.append("Lettura: CasaZen è il più economico a 1-3 unità (Free, Starter), il secondo dopo Kross da 5 a 100 unità con molto")
    L.append("più contenuto (compliance eseguita, fornitori con garanzia, sito diretto, portale ospite), e a 250 unità sta tra Kross")
    L.append("e Avantio. Contro Kross + Vikey (il pacchetto italiano con check-in) CasaZen costa meno da 6 unità in su.")
    L.append("")
    # --- Fixed cost coverage
    L.append("## 7. Copertura dei costi fissi")
    L.append("")
    L.append(f"Costi fissi al lancio: {eur(tot)} al mese (§ 2). Quante vendite li coprono, una linea alla volta:")
    L.append("")
    L.append("| Solo con... | Margine unitario | Serve |")
    L.append("|---|---:|---:|")
    m_starter = 29.0 - b2c_cost(B2C[1], 2)
    m_pro = 79.0 - b2c_cost(B2C[2], 5)
    m_base = 10.0 - b2b_base_cost(10.0)
    m_all = 13.0 - b2b_all_inclusive_cost(13.0)
    L.append(f"| Host Starter (2 proprietà) | {eur(m_starter, 2)} | {tot / m_starter:.0f} host |")
    L.append(f"| Host Pro (5 proprietà) | {eur(m_pro, 2)} | {tot / m_pro:.0f} host |")
    L.append(f"| Unità B2B Base (10-49) | {eur(m_base, 2)} | {tot / m_base:.0f} unità |")
    L.append(f"| Unità B2B tutto compreso (150-399) | {eur(m_all, 2)} | {tot / m_all:.0f} unità |")
    L.append("| Un founding partner da 250 unità | € 400 di fissi coperti + € 250 di margine | copre il 65 %; il resto con ~15 host Starter |")
    L.append("| Marketplace, 10 % su interventi da € 40 | € 4 a intervento | ~250 interventi al mese |")
    L.append("")
    L.append("Nel modello finanziario (scenario Base) la piattaforma è in utile dal go-live del primo founding partner (M6) e il")
    L.append("listino sopra produce un ARPU B2C di circa € 60 al mese per host pagante (mix 55 % Starter, 38 % Pro, 7 % Scale).")
    L.append("")
    L.append("## 8. Decisioni registrate con questa versione")
    L.append("")
    L.append("| # | Decisione | Prima |")
    L.append("|---|---|---|")
    L.append("| L1 | Piano **Free** a 1 proprietà con marketplace | non esisteva (prova a tempo non prevista) |")
    L.append("| L2 | Starter € 29, 3 proprietà | invariato |")
    L.append("| L3 | Pro € 79: 10 incluse + € 8 per unità fino a 50, channel manager via API incluso | 50 proprietà a prezzo fisso, channel manager a parte |")
    L.append("| L4 | Scale € 199: 25 incluse + € 7 per unità fino a 100, poi listino Agenzia | illimitate a prezzo fisso |")
    L.append("| L5 | Sconto annuale B2C −10 % | non previsto |")
    L.append("| L6 | B2B Base € 10 / 9 / 8, AI e Marketing +€ 4, minimo € 100 | Base € 9 / 8 / 7, minimo € 90 (analisi Quokka § 6.2) |")
    L.append(f"| L7 | Tutto compreso € 15 / 14 / 13 con **{B2B_VERIFICATIONS_INCLUDED}** verifiche per unità al mese | 8 verifiche |")
    L.append("| L8 | Verifica in più € 0,75 (+€ 0,20 con NFC); serratura € 7 | € 0,75; serratura € 6 |")
    L.append("| L9 | Marketplace: 10 % pagato dal cliente, fornitori senza canone | invariato (Turno: $ 0 + fee sui pagamenti; Chekin 10 % sugli upsell) |")
    L.append("")
    L.append("Cosa cambia nel codice se le decisioni sono confermate: `PlanCatalog` (Free, incluse ed extra), entitlement sulle")
    L.append("unità in più, prezzi Stripe per quantità (`spec-saas-billing` oggi prevede solo prezzi fissi), gating del channel")
    L.append("manager via API per piano, catalogo demo, testi dei piani in IT/EN.")
    L.append("")
    L.append("## 9. Fonti dei prezzi dei concorrenti")
    L.append("")
    L.append("- Kross Booking ✓ <https://www.krossbooking.com/prezzo> (Basic € 4, Premium € 6 per unità, minimo 3)")
    L.append("- Vikey ✓ <https://get.vikey.it/shop/> (Starter € 4,90, Master € 9,90 per unità al mese)")
    L.append("- Chekin ✓ <https://chekin.com/en/pricing/> (Basic € 3,95, Premium € 5,95, Enterprise € 7,95 per unità, minimo 3)")
    L.append("- Beds24 ✓ <https://www.beds24.com/pricing.html> (da € 15,50; € 0,55 per collegamento)")
    L.append("- Hospitable ✓ <https://hospitable.com/pricing/> (Essentials gratis illimitato; Host 1 inclusa +$ 10; Professional 2 +$ 15; Mogul 3 +$ 15-30); prezzi [NV] roommaster, bnbcalc")
    L.append("- Turno ✓ <https://turno.com/pricing/> ($ 0 con pulitori del marketplace, $ 10/proprietà con i propri; fee sui pagamenti fino all'8 % [NV])")
    L.append("- Smoobu [NV] support.smoobu.com, bnbcalc (Professional € 35, annuale € 31,50; Teams Pro+ € 55)")
    L.append("- Lodgify [NV] roommaster, plugnplayva (Starter $ 33, Professional $ 53, Ultimate $ 77; Ultimate $ 307 a 10, $ 574 a 25, $ 968 a 50 listing)")
    L.append("- Guesty [NV] costbench, roommaster, hostport (Lite $ 9-29/listing; Pro $ 40-72/listing + onboarding)")
    L.append("- Hostaway [NV] roommaster, stayswitchr ($ 160 minimo; $ 32 / 28 / 24 per unità a scaglioni; setup $ 300-1.000)")
    L.append("- Avantio [NV] bnbcalc, maltsite (€ 14 per unità, minimo € 295)")
    L.append("- Octorate [NV] octorate.com/best-channel-manager, hostliopro (Basic da € 8 per camera)")
    L.append("- Uplisting [NV] hostport ($ 100 fino a 5, ~$ 20/listing); Enso Connect [NV] ($ 9-16/listing o da $ 100/mese)")
    L.append("- Stripe [NV] checkoutpage, globalfeecalculator (carte EEA 1,5 % + € 0,25; Billing 0,7 %; Tax 0,5 %)")
    L.append("- NextPax ✓, Didit ✓, Seam ✓, Invoicetronic ✓, Resend ✓, AWS Bedrock ✓: `Sessions/prospect-quokka360/analisi-funzionalita.md` § 5")
    L.append("")
    return "\n".join(L) + "\n"


if __name__ == "__main__":
    OUT.write_text(render(), encoding="utf-8")
    print(f"scritto {OUT}")
