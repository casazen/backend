---
id: US-XXX                    # User story ID (optional for non-roadmap work)
slug: spec-example            # Filename without .md — must match spec-{slug}.md
title: Short human title
phase: 1                      # 0 | 1 | 1.5 | 2 | 3 | 4 | ops | compliance | maintenance
type: feature                 # feature | enabler | fix | compliance | ops | spike
priority: P1                  # P0 (now) | P1 (this phase) | P2 (next) | P3 (later) | —
status: specced               # idea | specced | planned | in-dev | shipped | blocked | deferred | frozen
issue:                        # GitHub issue # when planned+ (e.g. 271)
depends_on: []                # slugs of other specs
blocks: []                    # slugs this unblocks
exit_contributes_to:          # Phase exit criterion this item helps satisfy (one line)
last_reviewed: YYYY-MM-DD
---

# Spec — {Title} ({id or Issue #})

> Copy this file to `spec-{slug}.md`. Stage 02 gate **G9b** (`check-ac-depth.ps1 -SpecPath`) fails if Verifiable Outcomes / UX / Export sections are missing when applicable.

## Overview

One paragraph: problem, scope, why now. State what “done” means for a human (not only for CI).

**Phase:** {N} — {phase name} · **Type:** {type} · **Status:** {status} · **Issue:** #{N}

Design (when ready): `Sessions/design-{N}.md`. ADRs: list or “none”.

---

## User Story

As a …, I want …, so that …

---

## Acceptance Criteria

Every AC must be **observable and falsifiable**. Prefer Given / When / Then in spirit even if written as bullets.

Number ACs continuously (`AC1`…`ACn`). Map 1:1 to GitHub issue ACs and to design `## AC Test Map` REQ-IDs `SPEC:{slug}:ACn`.

### Backend

- **AC1**: …

### Frontend

- **AC8**: …

### Cross-cutting (auth, GDPR, errors) — if needed

- **ACn**: …

---

## Verifiable Outcomes

**Required.** One row per AC. This is what Stage 03 L1/L2/L3 must assert — not “page loads”.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 + L3 | `GET …` returns field X = Y and disclaimer contains … | Missing field; wrong enum; empty body |
| AC8 | L1 + L3 | CSV has headers `[…]` and ≥1 data row when seeded; PDF starts with `%PDF` and includes packLabel | Empty file; wrong Content-Type only; button visible but download broken |
| AC9 | L2 + L3 | Primary control shows Italian label “…”; empty state copy “…”; error state shows ProblemDetails title | English-only primary CTA; blank panel with no empty state |

Rules:
- UI ACs need L2 **and** L3 outcomes.
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- “Element is visible” alone is **not** a Verifiable Outcome for export, mutation, or multi-step flows.

---

## UX / UI Quality

**Required when there is a Frontend AC section.** Testable bar (not taste essays).

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | User can complete the happy path without guessing next step | L3: complete flow in ≤ N clicks/screens documented below |
| Language | End-user strings Italian (product policy) | L2/L3: assert Italian labels on primary controls |
| Empty state | No blank dead-end when data length = 0 | L2: empty fixture shows documented copy |
| Error state | 4xx/5xx surfaced as human message (no raw stack) | L2/L3: force error → visible message |
| Destructive / legal copy | Disclaimers / confirmations as in AC | Assert exact phrases from AC |

**Happy-path script (fill in):**

1. Start at `/app/…`
2. …
3. Done when …

---

## Export / Report Criteria

**Required when any AC mentions CSV, PDF, Excel, export, or commercialista pack.** Otherwise delete this section.

### CSV

| Field / column | Required | Notes |
|---|---|---|
| `taxYear` | yes | |
| … | yes | |

- Encoding: UTF-8 (BOM optional, document choice)
- Filename: no CF / P.IVA in `Content-Disposition`
- Empty dataset: header row still present

### PDF

| Requirement | Required |
|---|---|
| Real PDF bytes (`%PDF`) — not HTML renamed / empty stub | yes |
| Header / packLabel on first page | yes |
| Legal disclaimer present | yes |
| No tax-due / IRPEF engine fields | yes if compliance pack |
| Presentable to third party (commercialista): readable table or labeled sections, not debug dump | yes |

### JSON (if offered)

- Same business fields as CSV; document excluded fields.

---

## Technical Notes

| File | Action |
|---|---|
| `path/to/file` | Create / Modify — … |

**Complexity:** S \| M \| L  
**Migration:** yes/no — …  
**Dependencies:** `spec-…`  
**Repos:** BE \| FE \| mobile (list)

---

## Test expectations (process contract)

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit unit/integration asserting AC outcomes | “Compiles” |
| L2 | Playwright demo + `page.route` for UI contract; titled `test('ACn: …')` | One smoke for all ACs; visibility-only for exports |
| L3 | Real API local/staging; titled `test('ACn: …')` per UI AC | Mocking the path under test; AC map pointing at file without titled test |

Design Stage 02 must produce `## AC Test Map` with one row per AC. Stage 03 gate `check-ac-depth.ps1 -RequireTests` enforces titled tests + export depth.

---

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] items, if any — or `None`

---

## Out of Scope

- Explicit non-goals (prevents silent scope creep)

---

## Open Questions

- Resolved before Stage 03, or listed with owner/date
