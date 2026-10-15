"""Modello economico CasaZen, 36 mesi (nov 2026 - ott 2029).

Fonte unica dei numeri di `Sessions/business-plan-2026-10/business-plan.md`.
Prezzi e costi unitari: PR #526, analisi del tipo host Agenzia (§5-§6).
Tutto ciò che non viene da lì è marcato IPOTESI e va sostituito con dati reali o preventivi.

Uso:  python3 -I modello_economico.py            -> tabelle markdown su stdout
      python3 -I modello_economico.py --xlsx F    -> anche il file Excel con formule
"""

from __future__ import annotations

import sys
from dataclasses import dataclass, field, replace

MONTHS = 36
START_LABEL = ("nov", 2026)
MONTH_NAMES = ["gen", "feb", "mar", "apr", "mag", "giu", "lug", "ago", "set", "ott", "nov", "dic"]


def month_label(m: int) -> str:
    """m = 1 -> nov 2026."""
    idx = (MONTH_NAMES.index(START_LABEL[0]) + m - 1) % 12
    year = START_LABEL[1] + (MONTH_NAMES.index(START_LABEL[0]) + m - 1) // 12
    return f"{MONTH_NAMES[idx]} {year}"


# ---------------------------------------------------------------------------
# Prezzi e costi unitari (PR #526, IVA esclusa, USD = EUR 1:1 per prudenza)
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class UnitEconomics:
    # Listino B2B "tutto compreso" per scaglione (PR #526 §6.2), pagamento annuale
    list_all_in_small: float = 15.0   # 10-49 unità
    list_all_in_mid: float = 14.0     # 50-149 unità
    list_all_in_large: float = 13.0   # 150-399 unità
    verifications_per_unit: float = 8.0          # PR #526 §6.3: 4 prenotazioni x 2 adulti
    # Founding Partner (PR #526 §6.3): canone fisso per unità + verifiche al costo
    fp_fee_per_unit: float = 6.92
    # Costi variabili CasaZen (PR #526 §5.1)
    nextpax_min: float = 350.0        # include 50 unità
    nextpax_included_units: int = 50
    nextpax_per_unit: float = 3.15    # fatturazione annuale
    services_per_unit: float = 0.40   # SDI, AI, video
    verification_cost: float = 0.48   # Didit $0,33 + $0,15 NFC
    # B2C (IPOTESI: canone minimo per host da 1-3 unità, channel manager incluso; vedi business-plan.md §6)
    b2c_arpu: float = 29.0
    b2c_units_per_account: float = 2.0

    def list_price(self, units_per_agency: float) -> float:
        if units_per_agency < 50:
            return self.list_all_in_small
        if units_per_agency < 150:
            return self.list_all_in_mid
        return self.list_all_in_large


# ---------------------------------------------------------------------------
# Costi fissi
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class FixedCosts:
    # Prima del primo cliente (PR #526 §5.4): Railway Hobby 5 + Claude Code 200
    pre_live_platform: float = 205.0
    # In produzione (PR #526 §5.1 senza NextPax, che è nel variabile): hosting 85 + Resend 90 + Claude 200
    live_platform: float = 375.0
    vercel_pro: float = 20.0           # uso commerciale (docs/runbooks/free-hosting-analysis.md:252)
    company_admin: float = 150.0       # IPOTESI: commercialista, PEC, banca (skill financial-strategist, EST)
    insurance: float = 100.0           # IPOTESI segnaposto: RC professionale + cyber, da preventivo
    company_from_month: int = 2        # IPOTESI: società costituita a dicembre 2026
    one_off_legal: float = 6000.0      # IPOTESI segnaposto: DPIA, DAC7, contratti, revisione ToS/DPA
    one_off_legal_month: int = 3
    marketing: float = 300.0           # IPOTESI segnaposto dal go-live: fiere, viaggi, demo


# ---------------------------------------------------------------------------
# Scenari
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class Scenario:
    name: str
    first_fp_start: int | None          # mese di avvio del pilota del primo Founding Partner, None = mai
    first_fp_units: int = 250
    first_fp_pilot_units: int = 5      # IPOTESI: onboarding con pilota su poche unità
    first_fp_pilot_months: int = 2     # IPOTESI
    first_fp_ramp_months: int = 4      # IPOTESI: migrazione progressiva in qualche mese
    nextpax_min_from_full: bool = False  # True = minimo NextPax negoziato dalla migrazione completa
    fp_start: int | None = None        # primo mese in cui entrano altri Founding Partner
    fp_slots: int = 9                  # posti FP oltre il primo (PR #526: prime 10 agenzie)
    fp_per_month: float = 0.0
    list_start: int | None = None      # primo mese con agenzie a listino
    list_per_month: float = 0.0
    units_per_agency: float = 40.0     # IPOTESI
    agency_churn: float = 0.01         # IPOTESI, mensile, solo agenzie a listino (FP vincolati 24 mesi)
    b2c_start: int | None = None
    b2c_per_month: float = 0.0
    b2c_churn: float = 0.03            # IPOTESI: metà della banda 2-5% del piano di giugno
    founder_pay: float = 0.0           # €/mese, costo azienda, dall'avvio del pilota (PO: oggi 0, resta dipendente)
    support_hire_units: int | None = None  # assunzione supporto oltre N unità B2B
    support_cost: float = 3000.0       # IPOTESI: costo azienda mensile 1 FTE supporto


SCENARIOS = [
    Scenario(name="A. Solo il primo Founding Partner", first_fp_start=7),
    Scenario(name="B. Ritardo", first_fp_start=13, fp_start=19, fp_per_month=0.25,
             list_start=None, b2c_start=19, b2c_per_month=2),
    Scenario(name="C. Obiettivo", first_fp_start=7, fp_start=14, fp_per_month=0.6,
             list_start=29, list_per_month=1.0, b2c_start=10, b2c_per_month=6,
             support_hire_units=1000),
]


def first_fp_full_month(s: Scenario) -> int | None:
    """Primo mese con tutte le unità del primo Founding Partner migrate."""
    if s.first_fp_start is None:
        return None
    return s.first_fp_start + s.first_fp_pilot_months + s.first_fp_ramp_months - 1


def first_fp_units_at(s: Scenario, m: int) -> float:
    if s.first_fp_start is None or m < s.first_fp_start:
        return 0.0
    if m < s.first_fp_start + s.first_fp_pilot_months:
        return float(s.first_fp_pilot_units)
    k = m - (s.first_fp_start + s.first_fp_pilot_months) + 1
    step = (s.first_fp_units - s.first_fp_pilot_units) / s.first_fp_ramp_months
    return min(float(s.first_fp_units), s.first_fp_pilot_units + step * k)


@dataclass
class Month:
    m: int
    first_fp_units: float = 0.0
    fp_agencies: float = 0.0
    list_agencies: float = 0.0
    b2c_accounts: float = 0.0
    revenue_b2b_fee: float = 0.0
    revenue_verif_passthrough: float = 0.0
    revenue_b2c: float = 0.0
    cost_nextpax: float = 0.0
    cost_services: float = 0.0
    cost_verifications: float = 0.0
    cost_b2c: float = 0.0
    cost_fixed: float = 0.0
    cost_people: float = 0.0
    cash_in_timing: float = 0.0       # anticipo annuale FP meno quota mensile riconosciuta

    @property
    def b2b_units(self) -> float:
        return self.first_fp_units + self.fp_units + self.list_units

    fp_units: float = 0.0
    list_units: float = 0.0

    @property
    def revenue(self) -> float:
        return self.revenue_b2b_fee + self.revenue_verif_passthrough + self.revenue_b2c

    @property
    def variable_costs(self) -> float:
        return self.cost_nextpax + self.cost_services + self.cost_verifications + self.cost_b2c

    @property
    def gross_margin(self) -> float:
        return self.revenue - self.variable_costs

    @property
    def ebitda(self) -> float:
        return self.gross_margin - self.cost_fixed - self.cost_people


def run(s: Scenario, ue: UnitEconomics = UnitEconomics(), fc: FixedCosts = FixedCosts()) -> list[Month]:
    out: list[Month] = []
    fp, lst, b2c = 0.0, 0.0, 0.0
    full_month = first_fp_full_month(s)
    for m in range(1, MONTHS + 1):
        row = Month(m=m)
        live = s.first_fp_start is not None and m >= s.first_fp_start
        full = full_month is not None and m >= full_month
        row.first_fp_units = first_fp_units_at(s, m)
        if s.fp_start and m >= s.fp_start and live:
            fp = min(s.fp_slots, fp + s.fp_per_month)
        if s.list_start and m >= s.list_start and live:
            lst = lst * (1 - s.agency_churn) + s.list_per_month
        if s.b2c_start and m >= s.b2c_start:
            b2c = b2c * (1 - s.b2c_churn) + s.b2c_per_month
        row.fp_agencies, row.list_agencies, row.b2c_accounts = fp, lst, b2c
        row.fp_units = fp * s.units_per_agency
        row.list_units = lst * s.units_per_agency

        fp_total_units = row.first_fp_units + row.fp_units
        row.revenue_b2b_fee = (fp_total_units * ue.fp_fee_per_unit
                               + row.list_units * ue.list_price(s.units_per_agency))
        verifications_fp = fp_total_units * ue.verifications_per_unit
        verifications_list = row.list_units * ue.verifications_per_unit
        row.revenue_verif_passthrough = verifications_fp * ue.verification_cost
        row.revenue_b2c = b2c * ue.b2c_arpu

        b2c_units = b2c * ue.b2c_units_per_account
        cm_units = row.b2b_units + b2c_units
        if cm_units > 0 and s.nextpax_min_from_full and not full:
            row.cost_nextpax = cm_units * ue.nextpax_per_unit
        elif cm_units > 0:
            row.cost_nextpax = ue.nextpax_min + max(0.0, cm_units - ue.nextpax_included_units) * ue.nextpax_per_unit
        row.cost_services = row.b2b_units * ue.services_per_unit
        row.cost_verifications = (verifications_fp + verifications_list) * ue.verification_cost
        row.cost_b2c = b2c_units * ue.services_per_unit

        platform = fc.live_platform + fc.vercel_pro + fc.marketing if live else fc.pre_live_platform
        company = fc.company_admin + fc.insurance if m >= fc.company_from_month else 0.0
        one_off = fc.one_off_legal if m == fc.one_off_legal_month else 0.0
        row.cost_fixed = platform + company + one_off

        row.cost_people = s.founder_pay if live else 0.0
        if s.support_hire_units and row.b2b_units >= s.support_hire_units:
            row.cost_people += s.support_cost

        # Cassa: pilota e migrazione fatturati al mese; a regime il primo Founding Partner paga 12 mesi anticipati (PR #526 §6.3)
        annual = s.first_fp_units * ue.fp_fee_per_unit
        if full:
            prepaid = annual * 12 if (m - full_month) % 12 == 0 else 0.0
            row.cash_in_timing = prepaid - annual
        out.append(row)
    return out


def yearly(rows: list[Month]) -> list[dict[str, float]]:
    years = []
    for y in range(3):
        chunk = rows[y * 12:(y + 1) * 12]
        last = chunk[-1]
        years.append({
            "units_end": last.b2b_units,
            "agencies_end": (1 if last.first_fp_units else 0) + last.fp_agencies + last.list_agencies,
            "b2c_end": last.b2c_accounts,
            "revenue": sum(r.revenue for r in chunk),
            "gross_margin": sum(r.gross_margin for r in chunk),
            "fixed": sum(r.cost_fixed for r in chunk),
            "people": sum(r.cost_people for r in chunk),
            "ebitda": sum(r.ebitda for r in chunk),
            "mrr_end": last.revenue,
        })
    return years


def cumulative_min(rows: list[Month]) -> tuple[float, int]:
    """Minimo della cassa cumulata (EBITDA + timing incassi FP) e mese in cui si tocca."""
    cash, low, low_m = 0.0, 0.0, 0
    for r in rows:
        cash += r.ebitda + r.cash_in_timing
        if cash < low:
            low, low_m = cash, r.m
    return low, low_m


def breakeven_month(rows: list[Month]) -> int | None:
    for r in rows:
        if r.m > 1 and r.ebitda >= 0 and all(x.ebitda >= 0 for x in rows[r.m - 1:]):
            return r.m
    return None


def eur(x: float) -> str:
    sign = "−" if x < 0 else ""
    return f"{sign}€{abs(x):,.0f}".replace(",", ".")


def contribution(price: float, ue: UnitEconomics, verif_cost: float | None = None) -> float:
    vc = ue.verification_cost if verif_cost is None else verif_cost
    return price - ue.nextpax_per_unit - ue.services_per_unit - ue.verifications_per_unit * vc


def print_scenario(s: Scenario, ue: UnitEconomics, fc: FixedCosts) -> None:
    rows_ = run(s, ue, fc)
    ys = yearly(rows_)
    low, low_m = cumulative_min(rows_)
    be = breakeven_month(rows_)
    print(f"## {s.name}\n")
    print("| | Anno 1 (nov 26-ott 27) | Anno 2 | Anno 3 |")
    print("|---|---|---|---|")
    print("| Unità B2B a fine anno | " + " | ".join(f"{y['units_end']:.0f}" for y in ys) + " |")
    print("| Agenzie a fine anno | " + " | ".join(f"{y['agencies_end']:.1f}" for y in ys) + " |")
    print("| Host B2C paganti a fine anno | " + " | ".join(f"{y['b2c_end']:.0f}" for y in ys) + " |")
    print("| Ricavi | " + " | ".join(eur(y['revenue']) for y in ys) + " |")
    print("| Margine lordo | " + " | ".join(eur(y['gross_margin']) for y in ys) + " |")
    print("| Costi fissi | " + " | ".join(eur(-y['fixed']) for y in ys) + " |")
    print("| Persone | " + " | ".join(eur(-y['people']) for y in ys) + " |")
    print("| **EBITDA** | " + " | ".join(f"**{eur(y['ebitda'])}**" for y in ys) + " |")
    print("| Ricavi mensili a fine anno | " + " | ".join(eur(y['mrr_end']) for y in ys) + " |")
    print(f"\nCassa cumulata minima: {eur(low)} ({month_label(low_m) if low_m else '-'}); "
          f"pareggio mensile stabile: {month_label(be) if be else 'non raggiunto'}\n")


def print_tables() -> None:
    ue, fc = UnitEconomics(), FixedCosts()
    prices = [ue.fp_fee_per_unit + ue.verifications_per_unit * ue.verification_cost,
              ue.list_all_in_small, ue.list_all_in_mid, ue.list_all_in_large]
    verif = ue.verifications_per_unit * ue.verification_cost
    var = ue.nextpax_per_unit + ue.services_per_unit
    print("## Margine di contribuzione per unità B2B (€/mese)\n")
    print("| Voce | Founding Partner | Listino 10-49 | Listino 50-149 | Listino 150-399 |")
    print("|---|---|---|---|---|")
    print("| Ricavo | " + " | ".join(f"{p:.2f}" for p in prices) + " |")
    print("| NextPax + servizi | " + " | ".join(f"{-var:.2f}" for _ in prices) + " |")
    print("| Verifiche (8 × €0,48) | " + " | ".join(f"{-verif:.2f}" for _ in prices) + " |")
    contrib = [p - var - verif for p in prices]
    print("| **Contribuzione** | " + " | ".join(f"**{c:.2f}**" for c in contrib) + " |")
    print("| Margine lordo % | " + " | ".join(f"{c / p:.0%}" for c, p in zip(contrib, prices)) + " |")
    sumsub = [contribution(p, ue, 1.35) if i else p - var - ue.verifications_per_unit * 1.35
              for i, p in enumerate(prices)]
    print("| Contribuzione con Sumsub ($1,35) | " + " | ".join(f"{c:.2f}" for c in sumsub) + " |")
    b2c_c = ue.b2c_arpu - ue.b2c_units_per_account * var
    print(f"\nB2C: canone €{ue.b2c_arpu:.0f}, {ue.b2c_units_per_account:.0f} unità, contribuzione €{b2c_c:.2f} ({b2c_c / ue.b2c_arpu:.0%})\n")

    fixed_live = fc.live_platform + fc.vercel_pro + fc.marketing + fc.company_admin + fc.insurance + ue.nextpax_min
    print(f"Costi fissi in produzione incl. minimo NextPax: €{fixed_live:.0f}/mese\n")
    print("## Pareggio: unità a listino (€15, 10-49) necessarie\n")
    print("| Costo da coprire al mese | Unità a listino | Agenzie da 40 unità |")
    print("|---|---|---|")
    for label, extra in [("Solo costi fissi", 0), ("+ fondatore €3.000", 3000),
                         ("+ fondatore €6.000", 6000), ("+ fondatore €6.000 + 1 supporto €3.000", 9000)]:
        need = (fixed_live + extra) / contrib[1]
        print(f"| {label} (€{fixed_live + extra:,.0f}) | {need:,.0f} | {need / 40:.1f} |".replace(",", "."))
    print()

    fp_gap = SCENARIOS[2].fp_slots * SCENARIOS[2].units_per_agency * (contrib[1] - contrib[0])
    print(f"Contribuzione rinunciata dai 9 Founding Partner oltre il primo: €{fp_gap:,.0f}/mese\n".replace(",", "."))

    for s in SCENARIOS:
        print_scenario(s, ue, fc)

    print("## Sensibilità sullo scenario C\n")
    print("| Variante | EBITDA anno 3 | Cassa cumulata minima | Pareggio mensile stabile |")
    print("|---|---|---|---|")
    base = SCENARIOS[2]
    variants = [
        ("Base (fondatore non pagato)", base, ue),
        ("Minimo NextPax solo da migrazione completa", replace(base, nextpax_min_from_full=True), ue),
        ("Migrazione in 8 mesi invece di 4", replace(base, first_fp_ramp_months=8), ue),
        ("Fondatore €3.000/mese dall'avvio del pilota", replace(base, founder_pay=3000), ue),
        ("Fondatore €6.000/mese dall'avvio del pilota", replace(base, founder_pay=6000), ue),
        ("Verifiche con Sumsub ($1,35)", base, replace(ue, verification_cost=1.35)),
        ("Agenzie a listino dimezzate (0,5/mese)", replace(base, list_per_month=0.5), ue),
        ("Nessun B2C", replace(base, b2c_start=None), ue),
    ]
    for label, sc, u in variants:
        rows_ = run(sc, u, fc)
        ys = yearly(rows_)
        low, low_m = cumulative_min(rows_)
        be = breakeven_month(rows_)
        print(f"| {label} | {eur(ys[2]['ebitda'])} | {eur(low)} ({month_label(low_m) if low_m else '-'}) | "
              f"{month_label(be) if be else 'non raggiunto'} |")
    print()


if __name__ == "__main__":
    print_tables()
    if "--xlsx" in sys.argv:
        from export_xlsx import export  # noqa: E402  (stesso folder)
        export(sys.argv[sys.argv.index("--xlsx") + 1], SCENARIOS, UnitEconomics(), FixedCosts())
