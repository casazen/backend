#!/usr/bin/env python3
"""Modello finanziario a 36 mesi di CasaZen (novembre 2026 - ottobre 2029).

Genera `docs/business-plan/MODELLO-FINANZIARIO.md` e `docs/business-plan/modello-finanziario.csv`
a partire dalle ipotesi dichiarate qui sotto. Solo libreria standard: nessuna dipendenza.

    python3 scripts/business-plan/modello_finanziario.py

Ogni cifra del business plan che riguarda ricavi, costi e cassa viene da questo file:
per cambiare un'ipotesi si modifica il dizionario dello scenario e si rigenera il documento.
Tutti gli importi sono in euro, IVA esclusa, al mese. I costi passanti (verifiche d'identità
rifatturate al costo ai partner, commissioni Stripe addebitate ai clienti) sono esclusi sia dai
ricavi sia dai costi perché neutri per CasaZen.
"""

from __future__ import annotations

import csv
import math
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT_MD = ROOT / "docs" / "business-plan" / "MODELLO-FINANZIARIO.md"
OUT_CSV = ROOT / "docs" / "business-plan" / "modello-finanziario.csv"

MONTHS = 36
START_YEAR, START_MONTH = 2026, 11  # M1 = novembre 2026


def label(m: int) -> str:
    y = START_YEAR + (START_MONTH - 1 + m - 1) // 12
    mo = (START_MONTH - 1 + m - 1) % 12 + 1
    return f"{y}-{mo:02d}"


# ---------------------------------------------------------------------------
# Ipotesi comuni a tutti gli scenari (fonti: Sessions/prospect-quokka360/analisi-funzionalita.md
# §5-§6, docs/runbooks/suppliers.md §26, decisione PO 2026-10-08 sul listino B2C)
# ---------------------------------------------------------------------------

@dataclass
class Partner:
    """Un partner B2B (agenzia) con canone fisso mensile."""
    name: str
    start: int          # mese di go-live (prima fattura)
    units: int
    fee: float          # canone mensile IVA esclusa, verifiche escluse (passanti)


@dataclass
class Scenario:
    name: str
    # B2B
    partners: list[Partner]
    list_agency_adds: dict[int, float]       # anno -> nuove agenzie a listino al mese
    list_agency_units: int                   # unità medie per agenzia a listino
    list_agency_price_per_unit: float        # tutto compreso medio per unità
    b2b_churn: float                         # churn mensile delle agenzie a listino
    # B2C
    b2c_start: int
    b2c_adds: dict[int, float]               # anno -> nuovi host paganti al mese (lordi)
    b2c_churn: float
    b2c_arpu: float
    # fornitori e marketplace
    supplier_adds: dict[int, float]          # anno -> nuovi fornitori attivi al mese
    mkt_adoption_start: float                # quota host B2C che usano il marketplace (M7)
    mkt_adoption_end: float                  # quota a M24 e oltre
    mkt_gmv_per_adopting_host: float         # volume mensile di interventi per host che lo usa
    mkt_gmv_per_b2b_unit: float              # volume mensile per unità delle agenzie
    commission: float = 0.10                 # commissione CasaZen sugli interventi, pagata dal cliente
    guarantee_reserve: float = 0.015         # accantonamento per rimborsi e controversie (su GMV)
    # costi
    prod_start: int = 4                      # mese del primo ambiente di produzione
    channel_manager_min: float = 350.0       # minimo mensile NextPax (50 unità incluse)
    channel_manager_unit: float = 3.15       # oltre le 50 unità incluse
    channel_manager_included: int = 50
    b2c_channel_share: float = 0.30          # host B2C che attivano il channel manager
    b2c_units_per_host: float = 2.0
    per_unit_variable: float = 0.40          # SDI, AI, email per unità B2B
    per_host_ai: float = 0.50                # AI per host B2C
    stripe_billing_b2c: float = 0.02         # commissioni Stripe sulle fatture mensili B2C
    stripe_billing_b2b: float = 0.005        # fatture annuali B2B
    dev_tooling: float = 200.0               # Claude Code Max (sviluppo assistito)
    hosting: dict[int, float] = field(default_factory=lambda: {1: 85.0, 2: 125.0, 3: 185.0})
    hosting_pre_prod: float = 5.0
    email: dict[int, float] = field(default_factory=lambda: {1: 20.0, 2: 90.0, 3: 90.0})
    marketing: dict[int, float] = field(default_factory=lambda: {1: 150.0, 2: 400.0, 3: 800.0})
    accountant: float = 150.0                # commercialista, dal mese di costituzione
    company_formation_month: int = 2
    company_formation_oneoff: float = 2000.0  # SRL: notaio, registrazione, bolli
    legal_oneoff_month: int = 3
    legal_oneoff: float = 2500.0             # revisione ToS/Privacy/DPA + contratto Founding Partner
    trademark_oneoff: float = 300.0          # marchio (UIBM/EUIPO)
    advisory_recurring: float = 100.0        # pareri fiscali ricorrenti (DAC7, IVA commissione, CH), dal go-live
    insurance: float = 50.0                  # RC professionale, dal go-live
    bank_pec_misc: float = 30.0              # conto, PEC, firma digitale, bolli


def year_of(m: int) -> int:
    return (m - 1) // 12 + 1


def run(s: Scenario) -> list[dict]:
    rows: list[dict] = []
    b2c_hosts = 0.0
    list_agencies = 0.0
    suppliers = 0.0
    cumulative = 0.0
    go_live = min(p.start for p in s.partners)
    for m in range(1, MONTHS + 1):
        y = year_of(m)
        # --- B2B ---
        partner_units = sum(p.units for p in s.partners if m >= p.start)
        partner_rev = sum(p.fee for p in s.partners if m >= p.start)
        if m > 12:
            list_agencies = list_agencies * (1 - s.b2b_churn) + s.list_agency_adds.get(y, 0.0)
        list_units = list_agencies * s.list_agency_units
        list_rev = list_units * s.list_agency_price_per_unit
        b2b_units = partner_units + list_units
        b2b_rev = partner_rev + list_rev
        # --- B2C ---
        if m >= s.b2c_start:
            b2c_hosts = b2c_hosts * (1 - s.b2c_churn) + s.b2c_adds.get(y, 0.0)
        b2c_rev = b2c_hosts * s.b2c_arpu
        # --- fornitori ---
        if m >= s.b2c_start:
            suppliers += s.supplier_adds.get(y, 0.0)
        # --- marketplace ---
        if m >= s.b2c_start:
            t = min(1.0, (m - s.b2c_start) / max(1, 24 - s.b2c_start))
            adoption = s.mkt_adoption_start + (s.mkt_adoption_end - s.mkt_adoption_start) * t
        else:
            adoption = 0.0
        gmv = b2c_hosts * adoption * s.mkt_gmv_per_adopting_host + b2b_units * s.mkt_gmv_per_b2b_unit
        mkt_rev = gmv * s.commission
        revenue = b2b_rev + b2c_rev + mkt_rev
        # --- costi ---
        in_prod = m >= s.prod_start
        hosting = s.hosting[y] if in_prod else s.hosting_pre_prod
        email = s.email[y] if m >= s.b2c_start else 0.0
        cm_units = b2b_units + b2c_hosts * s.b2c_channel_share * s.b2c_units_per_host
        if m >= go_live:
            channel = s.channel_manager_min + max(0.0, cm_units - s.channel_manager_included) * s.channel_manager_unit
        else:
            channel = 0.0
        variable = b2b_units * s.per_unit_variable + b2c_hosts * s.per_host_ai
        stripe = b2c_rev * s.stripe_billing_b2c + b2b_rev * s.stripe_billing_b2b
        guarantee = gmv * s.guarantee_reserve
        marketing = s.marketing[y] if m >= s.b2c_start else 0.0
        admin = (s.accountant + s.bank_pec_misc) if m >= s.company_formation_month else 0.0
        admin += (s.advisory_recurring + s.insurance) if m >= go_live else 0.0
        oneoff = 0.0
        if m == s.company_formation_month:
            oneoff += s.company_formation_oneoff
        if m == s.legal_oneoff_month:
            oneoff += s.legal_oneoff + s.trademark_oneoff
        platform = hosting + email + channel + variable + stripe + s.dev_tooling
        costs = platform + guarantee + marketing + admin + oneoff
        result = revenue - costs
        cumulative += result
        rows.append({
            "mese": m, "periodo": label(m), "anno": y,
            "agenzie_partner": sum(1 for p in s.partners if m >= p.start),
            "agenzie_listino": list_agencies, "unita_b2b": b2b_units,
            "host_b2c": b2c_hosts, "fornitori_attivi": suppliers,
            "gmv_marketplace": gmv,
            "ricavi_b2b": b2b_rev, "ricavi_b2c": b2c_rev, "ricavi_marketplace": mkt_rev,
            "ricavi_totali": revenue,
            "costi_piattaforma": platform, "costi_garanzia": guarantee, "costi_marketing": marketing,
            "costi_amministrativi": admin, "costi_una_tantum": oneoff, "costi_totali": costs,
            "risultato": result, "cassa_cumulata": cumulative,
        })
    return rows


# ---------------------------------------------------------------------------
# I tre scenari
# ---------------------------------------------------------------------------

QUOKKA_FEE = 1730.0  # §6.3 analisi Quokka: NextPax 980 + servizi 100 + costi fissi 400 + margine 250

BASE = Scenario(
    name="Base",
    partners=[
        Partner("Quokka 360 (founding partner)", start=6, units=250, fee=QUOKKA_FEE),
        Partner("Founding partner 2", start=10, units=80, fee=80 * 8.0),
        Partner("Founding partner 3", start=12, units=60, fee=60 * 8.0),
    ],
    list_agency_adds={2: 0.5, 3: 1.0},
    list_agency_units=45, list_agency_price_per_unit=14.5, b2b_churn=0.01,
    b2c_start=7, b2c_adds={1: 6, 2: 12, 3: 25}, b2c_churn=0.03, b2c_arpu=55.0,
    supplier_adds={1: 3, 2: 5, 3: 8},
    mkt_adoption_start=0.20, mkt_adoption_end=0.50, mkt_gmv_per_adopting_host=220.0,
    mkt_gmv_per_b2b_unit=8.0,
)

PRUDENTE = Scenario(
    name="Prudente",
    partners=[
        Partner("Quokka 360 (founding partner)", start=8, units=250, fee=QUOKKA_FEE),
        Partner("Founding partner 2", start=14, units=60, fee=60 * 8.0),
        Partner("Founding partner 3", start=18, units=50, fee=50 * 8.0),
    ],
    list_agency_adds={2: 0.25, 3: 0.5},
    list_agency_units=40, list_agency_price_per_unit=14.5, b2b_churn=0.015,
    b2c_start=9, b2c_adds={1: 3, 2: 6, 3: 12}, b2c_churn=0.04, b2c_arpu=50.0,
    supplier_adds={1: 2, 2: 3, 3: 5},
    mkt_adoption_start=0.15, mkt_adoption_end=0.35, mkt_gmv_per_adopting_host=150.0,
    mkt_gmv_per_b2b_unit=5.0,
    prod_start=6,
)

AMBIZIOSO = Scenario(
    name="Ambizioso",
    partners=[
        Partner("Quokka 360 (founding partner)", start=5, units=250, fee=QUOKKA_FEE),
        Partner("Founding partner 2", start=8, units=100, fee=100 * 8.0),
        Partner("Founding partner 3", start=10, units=80, fee=80 * 8.0),
        Partner("Founding partner 4", start=12, units=60, fee=60 * 8.0),
    ],
    list_agency_adds={2: 1.0, 3: 2.0},
    list_agency_units=50, list_agency_price_per_unit=14.0, b2b_churn=0.01,
    b2c_start=7, b2c_adds={1: 10, 2: 20, 3: 40}, b2c_churn=0.025, b2c_arpu=58.0,
    supplier_adds={1: 5, 2: 8, 3: 12},
    mkt_adoption_start=0.25, mkt_adoption_end=0.60, mkt_gmv_per_adopting_host=260.0,
    mkt_gmv_per_b2b_unit=12.0,
)

SCENARIOS = [PRUDENTE, BASE, AMBIZIOSO]

# Struttura dei costi dal mese 37 (founder a tempo pieno + prime assunzioni), costo azienda mensile
TEAM_M37 = [
    ("Founder a tempo pieno (CEO/prodotto)", 4500.0),
    ("Sviluppatore full-stack senior (con AI)", 4000.0),
    ("Customer success e onboarding fornitori", 3000.0),
]


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------

def eur(x: float) -> str:
    sign = "-" if x < 0 else ""
    x = abs(x)
    s = f"{x:,.0f}".replace(",", ".")
    return f"{sign}€ {s}"


def num(x: float) -> str:
    return f"{x:,.0f}".replace(",", ".")


def yearly(rows: list[dict]) -> list[dict]:
    out = []
    for y in (1, 2, 3):
        yr = [r for r in rows if r["anno"] == y]
        agg = {k: sum(r[k] for r in yr) for k in (
            "ricavi_b2b", "ricavi_b2c", "ricavi_marketplace", "ricavi_totali",
            "costi_piattaforma", "costi_garanzia", "costi_marketing", "costi_amministrativi",
            "costi_una_tantum", "costi_totali", "risultato", "gmv_marketplace")}
        last = yr[-1]
        agg.update({"anno": y, "fine": last["periodo"], "mrr_fine": last["ricavi_totali"],
                    "host_b2c": last["host_b2c"], "agenzie": last["agenzie_partner"] + last["agenzie_listino"],
                    "unita_b2b": last["unita_b2b"], "fornitori": last["fornitori_attivi"],
                    "cassa": last["cassa_cumulata"]})
        out.append(agg)
    return out


def milestones(rows: list[dict]) -> list[dict]:
    return [rows[m - 1] for m in (6, 12, 18, 24, 30, 36)]


def breakeven_month(rows: list[dict]) -> int | None:
    for r in rows:
        if r["risultato"] > 0 and all(x["risultato"] > 0 for x in rows[r["mese"] - 1:]):
            return r["mese"]
    return None


def cash_need(rows: list[dict]) -> tuple[float, int]:
    worst = min(rows, key=lambda r: r["cassa_cumulata"])
    return worst["cassa_cumulata"], worst["mese"]


def render(all_rows: dict[str, list[dict]]) -> str:
    L: list[str] = []
    L.append("# CasaZen: modello finanziario a 36 mesi")
    L.append("")
    L.append("> **Documento generato**: non modificare a mano. Sorgente e ipotesi in `scripts/business-plan/modello_finanziario.py`;")
    L.append("> serie mensili complete in `modello-finanziario.csv`. Rigenerare con `python3 scripts/business-plan/modello_finanziario.py`.")
    L.append("> Orizzonte: M1 = novembre 2026, M36 = ottobre 2029. Importi in euro, IVA esclusa. Tutte le cifre sono **stime** [STIMA];")
    L.append("> le fonti dei prezzi e dei costi unitari sono nel business plan (§ 5 e Appendice A).")
    L.append("")
    L.append("## 1. Cosa misura il modello")
    L.append("")
    L.append("- **Ricavi B2B**: canoni delle agenzie. I founding partner pagano un canone fisso (costo documentato + € 1/unità);")
    L.append("  le agenzie a listino pagano il tutto compreso per unità. Le verifiche d'identità sono rifatturate al costo e restano fuori.")
    L.append("- **Ricavi B2C**: abbonamenti degli host (Starter € 29, Pro € 79, Scale € 199; ARPU medio ponderato).")
    L.append("- **Ricavi marketplace**: 10 % sul valore degli interventi dei fornitori pagati tramite CasaZen, a carico del cliente.")
    L.append("  Mai una commissione sulle prenotazioni o sui canoni di locazione.")
    L.append("- **Costi**: piattaforma (hosting, email, channel manager, costi per unità, Stripe sulle nostre fatture, strumenti di sviluppo),")
    L.append("  accantonamento garanzia (1,5 % del volume marketplace per rimborsi e controversie), marketing, amministrazione, una tantum.")
    L.append("- **Il founder non percepisce compenso fino al mese 36** (lavoro part-time, sviluppo assistito da AI). La struttura")
    L.append("  con founder a tempo pieno e prime assunzioni è nella sezione 6.")
    L.append("")
    # --- Ipotesi per scenario ---
    L.append("## 2. Ipotesi per scenario")
    L.append("")
    L.append("| Ipotesi | Prudente | Base | Ambizioso |")
    L.append("|---|---|---|---|")
    def row(name, f):
        L.append(f"| {name} | " + " | ".join(f(s) for s in SCENARIOS) + " |")
    row("Go-live Quokka 360 (250 unità, canone partner € 1.730)", lambda s: f"M{s.partners[0].start} ({label(s.partners[0].start)})")
    row("Altri founding partner (mese: unità)", lambda s: ", ".join(f"M{p.start}: {p.units}" for p in s.partners[1:]))
    row("Nuove agenzie a listino al mese (anno 2 / anno 3)", lambda s: f"{s.list_agency_adds.get(2,0)} / {s.list_agency_adds.get(3,0)}")
    row("Unità medie per agenzia a listino, prezzo medio per unità", lambda s: f"{s.list_agency_units}, € {s.list_agency_price_per_unit:.2f}".replace(".", ","))
    row("Churn mensile agenzie a listino", lambda s: f"{s.b2b_churn*100:.1f} %".replace(".", ","))
    row("Inizio onboarding B2C", lambda s: f"M{s.b2c_start} ({label(s.b2c_start)})")
    row("Nuovi host paganti al mese (anno 1 / 2 / 3)", lambda s: f"{s.b2c_adds[1]} / {s.b2c_adds[2]} / {s.b2c_adds[3]}")
    row("Churn mensile host B2C", lambda s: f"{s.b2c_churn*100:.1f} %".replace(".", ","))
    row("ARPU host B2C", lambda s: f"€ {s.b2c_arpu:.0f}")
    row("Nuovi fornitori attivi al mese (anno 1 / 2 / 3)", lambda s: f"{s.supplier_adds[1]} / {s.supplier_adds[2]} / {s.supplier_adds[3]}")
    row("Host B2C che usano il marketplace (inizio → M24)", lambda s: f"{s.mkt_adoption_start*100:.0f} % → {s.mkt_adoption_end*100:.0f} %")
    row("Volume interventi al mese per host che lo usa", lambda s: f"€ {s.mkt_gmv_per_adopting_host:.0f}")
    row("Volume interventi al mese per unità B2B", lambda s: f"€ {s.mkt_gmv_per_b2b_unit:.0f}")
    row("Commissione marketplace (pagata dal cliente)", lambda s: f"{s.commission*100:.0f} %")
    row("Accantonamento garanzia su volume", lambda s: f"{s.guarantee_reserve*100:.1f} %".replace(".", ","))
    row("Primo ambiente di produzione", lambda s: f"M{s.prod_start}")
    L.append("")
    L.append("Costi comuni: channel manager minimo € 350/mese dal go-live del primo partner (50 unità incluse, poi € 3,15/unità);")
    L.append("hosting € 5 prima della produzione, poi € 85 → € 125 → € 185 per anno; email € 20 → € 90; strumenti di sviluppo € 200;")
    L.append("commercialista € 150 + conto/PEC € 30 dal mese di costituzione (M2); costituzione SRL € 2.000 una tantum (M2);")
    L.append("revisione legale e marchio € 2.800 una tantum (M3); pareri fiscali € 100 e RC professionale € 50 dal go-live;")
    L.append("marketing € 150 → € 400 → € 800 dall'inizio del B2C; costi per unità B2B € 0,40 (SDI, AI, email); AI per host B2C € 0,50;")
    L.append("Stripe sulle nostre fatture 2 % (B2C) e 0,5 % (B2B).")
    L.append("")
    # --- Riepilogo per anno ---
    L.append("## 3. Riepilogo per anno")
    L.append("")
    for s in SCENARIOS:
        rows = all_rows[s.name]
        L.append(f"### Scenario {s.name}")
        L.append("")
        L.append("| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |")
        L.append("|---|---:|---:|---:|---:|")
        ys = yearly(rows)
        def yrow(name, key, money=True):
            vals = [y[key] for y in ys]
            tot = sum(vals)
            f = eur if money else num
            L.append(f"| {name} | " + " | ".join(f(v) for v in vals) + f" | {f(tot)} |")
        yrow("Ricavi B2B (agenzie)", "ricavi_b2b")
        yrow("Ricavi B2C (host)", "ricavi_b2c")
        yrow("Ricavi marketplace (10 % interventi)", "ricavi_marketplace")
        yrow("**Ricavi totali**", "ricavi_totali")
        yrow("Costi piattaforma", "costi_piattaforma")
        yrow("Accantonamento garanzia", "costi_garanzia")
        yrow("Marketing", "costi_marketing")
        yrow("Amministrazione e consulenze", "costi_amministrativi")
        yrow("Una tantum (società, legale, marchio)", "costi_una_tantum")
        yrow("**Costi totali**", "costi_totali")
        yrow("**Risultato operativo** (founder non retribuito)", "risultato")
        L.append("| Volume marketplace transato | " + " | ".join(eur(y["gmv_marketplace"]) for y in ys) + f" | {eur(sum(y['gmv_marketplace'] for y in ys))} |")
        L.append("")
        L.append("| A fine anno | Anno 1 | Anno 2 | Anno 3 |")
        L.append("|---|---:|---:|---:|")
        L.append("| MRR (ricavi mensili ricorrenti) | " + " | ".join(eur(y["mrr_fine"]) for y in ys) + " |")
        L.append("| ARR (MRR × 12) | " + " | ".join(eur(y["mrr_fine"] * 12) for y in ys) + " |")
        L.append("| Agenzie B2B attive | " + " | ".join(num(y["agenzie"]) for y in ys) + " |")
        L.append("| Unità gestite dalle agenzie | " + " | ".join(num(y["unita_b2b"]) for y in ys) + " |")
        L.append("| Host B2C paganti | " + " | ".join(num(y["host_b2c"]) for y in ys) + " |")
        L.append("| Fornitori attivi | " + " | ".join(num(y["fornitori"]) for y in ys) + " |")
        L.append("| Cassa cumulata (risultati sommati) | " + " | ".join(eur(y["cassa"]) for y in ys) + " |")
        L.append("")
    # --- Milestone ---
    L.append("## 4. Le tappe del piano (mese per mese)")
    L.append("")
    for s in SCENARIOS:
        rows = all_rows[s.name]
        L.append(f"### Scenario {s.name}")
        L.append("")
        ms = milestones(rows)
        L.append("| Mese | " + " | ".join(f"M{r['mese']} ({r['periodo']})" for r in ms) + " |")
        L.append("|---|" + "---:|" * len(ms))
        def mrow(name, key, f=eur):
            L.append(f"| {name} | " + " | ".join(f(r[key]) for r in ms) + " |")
        L.append("| Agenzie (partner + listino) | " + " | ".join(f"{r['agenzie_partner']} + {num(r['agenzie_listino'])}" for r in ms) + " |")
        mrow("Unità B2B", "unita_b2b", num)
        mrow("Host B2C paganti", "host_b2c", num)
        mrow("Fornitori attivi", "fornitori_attivi", num)
        mrow("Volume marketplace nel mese", "gmv_marketplace")
        mrow("MRR B2B", "ricavi_b2b")
        mrow("MRR B2C", "ricavi_b2c")
        mrow("MRR marketplace", "ricavi_marketplace")
        mrow("**MRR totale**", "ricavi_totali")
        mrow("Costi del mese", "costi_totali")
        mrow("**Risultato del mese**", "risultato")
        mrow("Cassa cumulata", "cassa_cumulata")
        L.append("")
    # --- Confronto ---
    L.append("## 5. Confronto tra scenari")
    L.append("")
    L.append("| Indicatore | Prudente | Base | Ambizioso |")
    L.append("|---|---:|---:|---:|")
    def crow(name, f):
        L.append(f"| {name} | " + " | ".join(f(all_rows[s.name]) for s in SCENARIOS) + " |")
    crow("MRR a M12", lambda r: eur(r[11]["ricavi_totali"]))
    crow("MRR a M24", lambda r: eur(r[23]["ricavi_totali"]))
    crow("MRR a M36", lambda r: eur(r[35]["ricavi_totali"]))
    crow("ARR a M36", lambda r: eur(r[35]["ricavi_totali"] * 12))
    crow("Quota B2B / B2C / marketplace sui ricavi a M36", lambda r: " / ".join(
        f"{r[35][k] / r[35]['ricavi_totali'] * 100:.0f} %" for k in ("ricavi_b2b", "ricavi_b2c", "ricavi_marketplace")))
    crow("Primo mese stabilmente in utile (founder non retribuito)", lambda r: (f"M{breakeven_month(r)} ({label(breakeven_month(r))})" if breakeven_month(r) else "oltre M36"))
    crow("Fabbisogno massimo di cassa (minimo del cumulato)", lambda r: f"{eur(cash_need(r)[0])} a M{cash_need(r)[1]}")
    crow("Cassa cumulata a M36", lambda r: eur(r[35]["cassa_cumulata"]))
    crow("Risultato cumulato anni 1-3", lambda r: eur(sum(x["risultato"] for x in r)))
    L.append("")
    # --- Team dal M37 ---
    L.append("## 6. Dal mese 37: founder a tempo pieno e prime assunzioni")
    L.append("")
    L.append("Il piano prevede che a 36 mesi lasci il mio lavoro e assuma. La domanda da porsi è: l'MRR di M36 copre la struttura?")
    L.append("")
    L.append("| Voce (costo azienda mensile, stima) | € / mese |")
    L.append("|---|---:|")
    team_total = 0.0
    for name, cost in TEAM_M37:
        L.append(f"| {name} | {eur(cost)} |")
        team_total += cost
    L.append(f"| **Totale persone** | **{eur(team_total)}** |")
    L.append("")
    L.append("| Scenario | MRR M36 | Costi operativi M36 | Margine prima delle persone | Margine dopo le persone | Copertura |")
    L.append("|---|---:|---:|---:|---:|---:|")
    for s in SCENARIOS:
        r = all_rows[s.name][35]
        margin = r["risultato"]
        after = margin - team_total
        cover = (margin / team_total * 100) if team_total else 0
        L.append(f"| {s.name} | {eur(r['ricavi_totali'])} | {eur(r['costi_totali'])} | {eur(margin)} | {eur(after)} | {cover:.0f} % |")
    L.append("")
    L.append("Lettura: con copertura sopra il 100 % le persone sono pagate dai ricavi del mese; sotto, la differenza va coperta")
    L.append("dalla cassa cumulata degli anni 1-3 o da capitale esterno, oppure si scagliona l'ingresso delle assunzioni.")
    L.append("")
    # --- Sensibilità ---
    L.append("## 7. Sensibilità (scenario Base, MRR a M36)")
    L.append("")
    base_rows = all_rows["Base"]
    base36 = base_rows[35]["ricavi_totali"]
    base_cash = base_rows[35]["cassa_cumulata"]
    L.append("| Variazione | MRR M36 | Differenza MRR | Cassa cumulata M36 | Fabbisogno massimo |")
    L.append("|---|---:|---:|---:|---:|")
    def variant(name, **kw):
        s = Scenario(**{**BASE.__dict__, **kw})
        s.name = "variant"
        rows = run(s)
        v = rows[35]["ricavi_totali"]
        need, need_m = cash_need(rows)
        L.append(f"| {name} | {eur(v)} | {eur(v - base36)} | {eur(rows[35]['cassa_cumulata'])} | {eur(need)} (M{need_m}) |")
    L.append(f"| Scenario Base (riferimento) | {eur(base36)} | € 0 | {eur(base_cash)} | {eur(cash_need(base_rows)[0])} (M{cash_need(base_rows)[1]}) |")
    variant("ARPU B2C −20 % (€ 44)", b2c_arpu=BASE.b2c_arpu * 0.8)
    variant("ARPU B2C +20 % (€ 66)", b2c_arpu=BASE.b2c_arpu * 1.2)
    variant("Churn B2C 2 %/mese", b2c_churn=0.02)
    variant("Churn B2C 4 %/mese", b2c_churn=0.04)
    variant("Commissione marketplace 8 %", commission=0.08)
    variant("Commissione marketplace 12 %", commission=0.12)
    variant("Nessuna agenzia a listino (solo founding partner)", list_agency_adds={})
    variant("Go-live Quokka a M9 invece di M6", partners=[Partner("Quokka 360", 9, 250, QUOKKA_FEE)] + BASE.partners[1:])
    L.append("")
    L.append("Il driver principale dei ricavi è il numero di host B2C paganti (e con loro il volume del marketplace); il driver")
    L.append("principale della cassa nei primi 12 mesi è la data di go-live del primo founding partner, che da sola copre i costi fissi.")
    L.append("")
    L.append("## 8. Come leggere e aggiornare")
    L.append("")
    L.append("1. Le ipotesi sono dichiarate in testa allo script, una riga per scenario; le fonti sono nel business plan.")
    L.append("2. Ogni revisione del piano che tocca un'ipotesi rigenera questo file e il CSV nello stesso commit (vedi `CHANGELOG.md`).")
    L.append("3. Dal primo cliente, i valori stimati vanno sostituiti con i dati reali (MRR, churn, GMV) e lo scenario reale diventa il Base.")
    L.append("")
    return "\n".join(L) + "\n"


def main() -> None:
    all_rows = {s.name: run(s) for s in SCENARIOS}
    OUT_MD.parent.mkdir(parents=True, exist_ok=True)
    OUT_MD.write_text(render(all_rows), encoding="utf-8")
    with OUT_CSV.open("w", newline="", encoding="utf-8") as fh:
        keys = ["scenario"] + list(next(iter(all_rows.values()))[0].keys())
        w = csv.DictWriter(fh, fieldnames=keys, delimiter=";")
        w.writeheader()
        for s in SCENARIOS:
            for r in all_rows[s.name]:
                w.writerow({"scenario": s.name, **{k: (round(v, 2) if isinstance(v, float) else v) for k, v in r.items()}})
    for s in SCENARIOS:
        r = all_rows[s.name]
        be = breakeven_month(r)
        print(f"{s.name:10s} MRR M12 {r[11]['ricavi_totali']:9.0f}  M24 {r[23]['ricavi_totali']:9.0f}  M36 {r[35]['ricavi_totali']:9.0f}"
              f"  breakeven M{be}  cassa min {cash_need(r)[0]:9.0f} (M{cash_need(r)[1]})  cassa M36 {r[35]['cassa_cumulata']:9.0f}")


if __name__ == "__main__":
    main()
