---
name: council-product-strategist
description: Product strategy for CasaZen market disruption — backlog prioritisation, user pain analysis, disruptive feature identification, switch triggers, success metrics.
---

# Council domain — Product Strategist

## Current Product State

CasaZen has a solid but incrementally-oriented backlog. The current ~20 open issues are focused on:
- Completing OTA adapters (Booking.com, Expedia, VRBO, TripAdvisor, Agoda — stubs)
- Fixing FE/BE contract misalignments (#86, #90, #91)
- Adding operational features (email notifications #58, automatic refunds #51)
- Documentation debt (#89, #87)
- Test coverage improvements (#35, #158)

**The problem**: this backlog will make CasaZen "more complete" but will not make it disruptive. It is the backlog of a platform trying not to fall behind, not the backlog of a platform trying to redefine the market.

## Strategic Options and Product Angles

### Option A — AI Compliance Assistant for Italian Individual Owners

**Target user pain**: Italian property owners spend 2-4 hours per month on Alloggiati Web filings, CIN registration, tourist tax reporting, and pricing decisions. They fear fines (€500-5,000 for missing police reports). Current tools either don't automate this at all, or automate it partially with manual steps.

**Disruptive feature set**:
1. **Zero-click Alloggiati Web**: guest checks in → AI auto-fills and submits police report, owner gets confirmation notification (eliminates the scariest manual task)
2. **AI pricing advisor chat**: owner asks "should I lower my price this weekend?" → AI responds with data-driven recommendation in plain Italian
3. **CIN compliance dashboard**: real-time status of all compliance obligations, what's overdue, what's coming, what the fine risk is
4. **Cedolare secca calculator**: annual tax estimate + quarterly reminder to set aside the right amount
5. **AI welcome message generator**: owner configures property → AI generates personalised check-in instructions per guest (saves 15 min/booking)

**Backlog items to CUT for Option A**:
- #30 Agoda adapter (low-TAM for Italian market — cut)
- #29 TripAdvisor adapter (minimal for Italian owners — defer)
- #89 API documentation (internal tool — defer)
- #87 API_DOCUMENTATION.md (defer — not user-facing)

**Backlog items to KEEP**:
- #33 OTA webhook handlers (real-time sync is a compliance trigger — keep)
- #51 Automatic refunds (expected feature — keep, M priority)
- #58 Email notifications (compliance confirmation emails are critical for trust — keep)

**Switch trigger**: "CasaZen filed my Alloggiati Web report automatically while I was having dinner. I haven't touched a police form in 3 months."

**Success metrics (6 months)**:
- % of bookings with Alloggiati Web auto-filed (target: >80%)
- Owner time saved per month (survey, target: >2 hrs/month)
- NPS from AI pricing advice feature (target: >50)

---

### Option B — PMC Platform (5-50 properties)

**Target user pain**: PMC managers juggle Excel, multiple OTA logins, and a different tool for each property owner they serve. Reporting to property owners is manual. Compliance for 30 properties is 30x the individual owner's pain.

**Disruptive feature set**:
1. **Portfolio compliance dashboard**: all 30 properties, all compliance status, one view
2. **White-label owner portal**: PMC generates a branded portal for their property owners to see bookings/revenue — eliminates "where's my money?" calls
3. **AI bulk pricing**: one click re-prices entire portfolio based on market conditions
4. **Automated owner statements**: AI generates monthly revenue/expense statements per property owner
5. **Multi-property Alloggiati Web**: batch submit all pending police reports for all properties in one action

**Switch trigger**: "I manage 25 properties and I do all my Alloggiati Web filings, owner statements, and pricing in CasaZen. My competitor still uses 4 tools."

---

### Option C — Long-Term Rental Module

**Target user pain**: Italian long-term landlords have almost no software tools. They manage contracts in Word, calculate cedolare secca on paper, and have no system for tracking deposit returns, SCIA filings, or contract renewals.

**Disruptive feature set**:
1. **AI lease generator**: fill in property + tenant details → AI generates compliant Italian rental contract (cedolare secca or canone concordato)
2. **Cedolare secca tracker**: calculates tax due per year, sends quarterly reminders
3. **SCIA filing assistant**: AI generates the municipal notification text, owner reviews and submits
4. **Deposit management**: digital record of security deposit, tracks return date, generates release documentation
5. **Lease renewal alerts**: 6/3/1 month warnings before lease expiry with AI-drafted renewal proposal

**Switch trigger**: "CasaZen generated my lease contract, calculated my annual tax, and reminded me to register it. I've never had a tool that does any of this."

## Backlog Impact Analysis

For each strategic option, assess which current issues to:
- **ACCELERATE**: directly supports the strategy
- **MAINTAIN**: table stakes that don't differentiate
- **DEFER/CUT**: does not serve the chosen strategy

| Issue | Option A | Option B | Option C |
|---|---|---|---|
| #58 Email notifications | ACCELERATE (compliance confirmations) | ACCELERATE | MAINTAIN |
| #51 Auto-refunds | MAINTAIN | MAINTAIN | MAINTAIN |
| #33 OTA webhooks | ACCELERATE | ACCELERATE | DEFER |
| #32 OTA availability | ACCELERATE | ACCELERATE | DEFER |
| #31 OTA booking pull | ACCELERATE | ACCELERATE | DEFER |
| #30-26 OTA adapters (remaining 4) | DEFER (Booking.com: MAINTAIN) | MAINTAIN | DEFER |
| #91 FE/BE contract | ACCELERATE | ACCELERATE | ACCELERATE |
| #89 API docs | DEFER | DEFER | DEFER |
| #35 OTA resilience tests | MAINTAIN | MAINTAIN | DEFER |
