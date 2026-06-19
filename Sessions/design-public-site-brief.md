# Design Brief — Public Site Template 1 (MVP F0)

**Issue:** #290  
**Feeds:** `spec-public-site-design-system` (US-023, Fase 1)  
**Status:** Approved for Fase 1 implementation

## Moodboard direction — "Mare Premium"

Holidu-inspired coastal luxury: editorial photography, generous whitespace, trust-first booking CTA.

| Element | Direction |
|---|---|
| Photography | Full-bleed hero, 16:9 gallery grid, warm natural light |
| Typography | Display: **Fraunces** or **Playfair Display**; body: **Inter** / **Source Sans 3** |
| Color | Host `primaryColor` drives CTA; default palette sand `#F5F0E8`, deep navy `#1A2B3C`, accent coral `#E07A5F` |
| Tone | Italian hospitality — welcoming, not corporate SaaS |

## Template 1 wireframe — `PublicSiteShell` / theme `mare`

### Desktop

```
┌─────────────────────────────────────────────────────────┐
│ [Logo]                              [IT|EN]  [Prenota]  │
├─────────────────────────────────────────────────────────┤
│                                                         │
│   HERO — full width image + overlay title + tagline     │
│                                                         │
├──────────────────────────────┬──────────────────────────┤
│  Gallery 2×2 grid            │  BookingWidget (sticky)  │
│                              │  - date range            │
│  AmenityGrid icons           │  - guests                │
│                              │  - price breakdown       │
│  Editorial description       │  - CTA "Prenota ora"     │
│                              │  - GDPR consent          │
├──────────────────────────────┴──────────────────────────┤
│  Footer — map link · contacts · Powered by (Starter)      │
└─────────────────────────────────────────────────────────┘
```

### Mobile

- Hero 4:5 crop; gallery horizontal swipe.
- **Fixed bottom bar:** "Verifica disponibilità" → expands booking widget sheet.
- Booking widget becomes full-screen modal on tap.

## Design tokens (for `PublicSiteShell`)

| Token | Value | Usage |
|---|---|---|
| `--ps-font-display` | `Fraunces, serif` | H1, property name |
| `--ps-font-body` | `Inter, sans-serif` | Body, labels |
| `--ps-space-section` | `4rem` / `2rem` mobile | Section padding |
| `--ps-radius-card` | `12px` | Gallery cards, widget |
| `--ps-shadow-widget` | `0 8px 32px rgba(0,0,0,0.12)` | Sticky booking widget |
| `--ps-cta-min-height` | `48px` | Touch target WCAG |

Host branding overrides: `--ps-color-primary` from `Org.primaryColor`.

## LCP budget

| Metric | Target | Tactics |
|---|---|---|
| LCP | **< 2.5s** on 4G | Hero WebP ≤ 200KB; `fetchpriority=high` on hero |
| CLS | < 0.1 | Reserve gallery aspect ratio boxes |
| INP | < 200ms | Defer non-critical JS; widget lazy on scroll |

Measure on `/book/{slug}` with Lighthouse mobile preset before Fase 1 release.

## Figma / assets

F0 delivers this markdown brief only. Figma board link TBD in Fase 1 kickoff.

## Acceptance mapping (#290)

- [x] Moodboard direction documented
- [x] Template 1 wireframe (hero, gallery, booking widget, mobile)
- [x] Token list for `PublicSiteShell`
- [x] LCP budget documented
